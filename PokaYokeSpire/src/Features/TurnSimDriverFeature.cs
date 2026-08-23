using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;             // CombatState, CombatManager
using MegaCrit.Sts2.Core.Context;            // LocalContext
using MegaCrit.Sts2.Core.Entities.Cards;     // CardPile
using MegaCrit.Sts2.Core.Entities.Creatures; // Creature
using MegaCrit.Sts2.Core.Entities.Players;   // Player, PlayerCombatState
using MegaCrit.Sts2.Core.Nodes.Combat;       // NEnergyCounter
using PokaYokeSpire.Combat;                   // TurnSim, TurnSimReader, IncomingDamage, ScheduledDamage
using PokaYokeSpire.Core;                     // Feature, Config, DebugLog

namespace PokaYokeSpire.Features;

/// <summary>
/// The ONE off-thread driver for the turn simulator (invariant 5: expensive work is bounded and off the
/// game thread). On each combat-state change it SNAPSHOTS everything the sim needs on the game thread
/// (reading game objects is only safe here), then solves on a background <see cref="Task"/> and caches
/// the result. Both consumers — the combat orbs (text) and the lethal-gem glow (kill-the-room / kill the
/// hovered enemy) — read the cached <see cref="Latest"/> on the game thread; neither runs the solver, so
/// they can never hitch or crash a frame. Only the newest snapshot's result is published.
/// </summary>
[HarmonyPatch(typeof(NEnergyCounter), "OnCombatStateChanged")]
internal static class TurnSimDriverFeature
{
    public sealed class Out
    {
        public TurnSim.Result Result;
        public List<Creature>? EnemyRefs;
        public int[] Scheduled = System.Array.Empty<int>();
        public int DoNothingIncoming;
        public int PlayerHp;  // current HP, so the defense gem can flag a lethal/near-lethal turn
        public long Gen;
        public bool HasSim;   // false when it's not the player's turn / no enemies
    }

    private static Out? _latest;
    public static Out? Latest => _latest;   // read on the game thread by the consumers
    private static long _gen;

    private static void Postfix(CombatState combatState)
        => Feature.Run("turnsim-driver", () => Config.ShowIncomingGem || Config.LethalGemGlow, () => Submit(combatState));

    private static void Submit(CombatState state)
    {
        var sim = TurnSimReader.Read(state);
        TurnAudit.Observe(sim, state);   // self-audit: freeze prediction / measure actuals / flag mismatches
        var p = IncomingDamage.Compute(state);
        // do-nothing HP loss = end the turn NOW, play nothing. Use the sim's own HpLost so it and the min
        // share one block-correct model (blockable Burn/Toxic absorbed by block; unblockable added after).
        // Fall back to the exact game hook only when there's no sim snapshot (not the player's turn).
        int doNothing = sim != null ? TurnSim.HpLost(sim.Player, sim.Enemies) : (p.Valid ? p.NetHpLoss : 0);
        int playerHp = 0;
        try { playerHp = LocalContext.GetMe((System.Collections.Generic.IEnumerable<Creature>)state.Creatures)?.CurrentHp ?? 0; } catch { }
        long g = Interlocked.Increment(ref _gen);

        if (sim == null)
        {
            _latest = new Out { DoNothingIncoming = doNothing, PlayerHp = playerHp, Gen = g, HasSim = false };
            return;
        }

        var sched = ScheduledDamage.PerEnemy(state, sim.EnemyRefs);   // game-thread read of enemy powers
        Task.Run(() =>
        {
            TurnSim.Result r;
            try { r = TurnSim.Solve(sim.Player, sim.Enemies, sim.Hand, nodeCap: 200000); }
            catch (System.Exception e) { DebugLog.Error("TurnSim.Solve", e); return; }
            if (Interlocked.Read(ref _gen) != g) return;   // a newer snapshot superseded us
            _latest = new Out { Result = r, EnemyRefs = sim.EnemyRefs, Scheduled = sched, DoNothingIncoming = doNothing, PlayerHp = playerHp, Gen = g, HasSim = true };
            if (r.Truncated) DebugLog.Warn($"turnsim hit the node budget ({r.Nodes}) — result is a conservative bound (cards={sim.Hand.Count}, enemies={sim.Enemies.Length})");
            if (DebugLog.Enabled)
                DebugLog.Debug($"turnsim solved: maxDmg={r.MaxDamage} perEnemy=[{string.Join(",", r.MaxPerEnemy)}] minHp={r.MinHpLost} (do-nothing={doNothing}) killAll={r.CanKillAll} nodes={r.Nodes} in {sim.Hand.Count} cards");
        });
    }

    /// F9 panel "Report broken turn" — dump the COMPLETE current turn state to a dedicated file so a wrong
    /// gem can be diagnosed from ground truth: the raw game hand vs what the reader captured (a mismatch is
    /// the smoking gun), every card's read effect, all player fields + unmodeled powers, enemy intents, and
    /// the cached sim result. Fail-open; never throws into the game.
    public static string DumpBrokenTurn()
    {
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("==================== BROKEN TURN REPORT ====================");
            CombatState? state = null;
            try { state = CombatManager.Instance?.DebugOnlyGetState(); } catch { }
            if (state == null) { DebugLog.Warn("BROKEN TURN: no active combat state"); return "no combat"; }

            Player? me = LocalContext.GetMe(state);
            Creature? meC = LocalContext.GetMe((IEnumerable<Creature>)state.Creatures);
            var pcs = me?.PlayerCombatState;

            // ── full combat / player state ──
            try { sb.AppendLine($"combat: side={state.CurrentSide} inProgress={CombatManager.Instance?.IsInProgress}"); } catch { }
            try { sb.AppendLine($"player: hp={meC?.CurrentHp}/{meC?.MaxHp} block={meC?.Block} energy={pcs?.Energy}/{pcs?.MaxEnergy}"); } catch { }
            try { sb.Append("powers: "); foreach (var pw in meC!.Powers) sb.Append($"{pw.GetType().Name}={pw.Amount} "); sb.AppendLine(); } catch { }
            try { sb.Append("relics: "); foreach (var r in me!.Relics) sb.Append(r.GetType().Name).Append(' '); sb.AppendLine(); } catch { }
            try { sb.Append("potions: "); foreach (var po in me!.Potions) if (po != null) sb.Append(po.GetType().Name).Append(' '); sb.AppendLine(); } catch { }

            // ── ALL card piles (draw pile matters for the draw-leak question) ──
            void DumpPile(string label, CardPile? pile)
            {
                try
                {
                    if (pile == null) { sb.AppendLine($"{label}: (null)"); return; }
                    sb.Append($"{label} ({pile.Cards.Count}): ");
                    foreach (var c in pile.Cards) sb.Append(c.GetType().Name).Append(' ');
                    sb.AppendLine();
                }
                catch { sb.AppendLine($"{label}: (unreadable)"); }
            }
            int rawHand = 0; try { rawHand = pcs?.Hand.Cards.Count ?? 0; } catch { }
            DumpPile("HAND", pcs?.Hand);
            DumpPile("draw pile", pcs?.DrawPile);
            DumpPile("discard pile", pcs?.DiscardPile);
            DumpPile("exhaust pile", pcs?.ExhaustPile);

            // ── each hand card: RAW game vars vs what the reader interpreted ──
            sb.AppendLine("hand cards (raw game DynamicVars):");
            try
            {
                foreach (var cm in pcs!.Hand.Cards)
                {
                    sb.Append($"    {cm.GetType().Name} type={cm.Type} vars={{");
                    try { foreach (var v in cm.DynamicVars.Values) sb.Append($"{v.Name}={v.BaseValue},"); } catch { }
                    sb.Append("} keywords={");
                    try { foreach (var k in cm.Keywords) sb.Append(k).Append(','); } catch { }
                    sb.Append("} tags={");
                    try { foreach (var t in cm.Tags) sb.Append(t).Append(','); } catch { }
                    sb.AppendLine("}");
                }
            }
            catch { }

            // ── what the READER captured — the snapshot the sim actually solves ──
            var snap = TurnSimReader.Read(state);
            if (snap != null)
            {
                var p = snap.Player;
                sb.AppendLine($"snapshot player: energy={p.Energy} str={p.Strength} dex={p.Dexterity} weak={p.Weak} frail={p.Frail} vuln={p.Vulnerable} shrink={p.Shrink} intangible={p.Intangible} block={p.Block} vigor={p.Vigor} reactiveBlk={p.ReactiveBlock} endTurnBlockable={p.EndTurnSelfDamageBlockable} endTurnUnblockable={p.EndTurnSelfDamage}");
                sb.AppendLine($"snapshot hand ({snap.Hand.Count}) — the sim's interpretation:");
                foreach (var c in snap.Hand)
                    sb.AppendLine($"    {c.Name}: cost={c.Cost} dmg={c.Damage}x{c.Hits} tgt={c.AttackTarget} block={c.Block} flatBlk={c.FlatBlock} applyVuln={c.ApplyVulnerable} applyWeak={c.ApplyWeak} strGain={c.StrengthGain} xcost={c.XCost} exhaust={c.Exhausts} dyn={c.Dynamic}");
                sb.AppendLine($"enemies ({snap.Enemies.Length}):");
                for (int i = 0; i < snap.Enemies.Length; i++)
                {
                    var e = snap.Enemies[i];
                    sb.AppendLine($"    [{i}] hp={e.Hp} block={e.Block} vuln={e.Vulnerable} weak={e.Weak} str={e.Strength} intent={e.IntentDamage}x{e.IntentHits} perHitCap={e.PerHitCap} dmgTakenPct={e.DamageTakenPct} doom={e.Doom}");
                }
                // full raw enemy powers too
                try
                {
                    int ei = 0;
                    foreach (var en in state.Enemies)
                    {
                        if (en == null || en.CurrentHp <= 0) continue;
                        sb.Append($"    enemy[{ei}] {en.GetType().Name} hp={en.CurrentHp}/{en.MaxHp} block={en.Block} powers=");
                        foreach (var pw in en.Powers) sb.Append($"{pw.GetType().Name}={pw.Amount} ");
                        sb.AppendLine();
                        ei++;
                    }
                }
                catch { }
                sb.AppendLine($">>> HAND DIVERGENCE: rawHand={rawHand} snapshotHand={snap.Hand.Count}   (rawHand != snapshotHand ⇒ the reader captured a DIFFERENT hand than the game shows)");
            }

            var o = _latest;
            if (o != null && o.HasSim)
                sb.AppendLine($"sim result: maxDmg={o.Result.MaxDamage} perEnemy=[{string.Join(",", o.Result.MaxPerEnemy)}] minHp={o.Result.MinHpLost} killAll={o.Result.CanKillAll} nodes={o.Result.Nodes} truncated={o.Result.Truncated} doNothing={o.DoNothingIncoming} scheduled=[{string.Join(",", o.Scheduled)}] gen={o.Gen}");
            sb.AppendLine("===========================================================");

            string report = sb.ToString();
            DebugLog.Warn(report);
            try
            {
                var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    "Library", "Application Support", "SlayTheSpire2", "modding", "logs");
                var path = Path.Combine(dir, $"broken-turn-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
                File.WriteAllText(path, report);
                DebugLog.Warn($"broken-turn report written to {path}");
                return path;
            }
            catch { return "(logged)"; }
        }
        catch (System.Exception e) { DebugLog.Error("DumpBrokenTurn", e); return "(error)"; }
    }
}
