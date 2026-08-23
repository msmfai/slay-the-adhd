using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using MegaCrit.Sts2.Core.Combat;             // CombatState, CombatManager, CombatSide
using MegaCrit.Sts2.Core.Context;            // LocalContext
using MegaCrit.Sts2.Core.Entities.Cards;     // CardPile
using MegaCrit.Sts2.Core.Entities.Creatures; // Creature
using MegaCrit.Sts2.Core.Entities.Players;   // Player, PlayerCombatState
using PokaYokeSpire.Combat;                   // TurnSim, TurnSimReader
using PokaYokeSpire.Core;                     // Config, DebugLog

namespace PokaYokeSpire.Features;

/// <summary>
/// SELF-AUDIT — the play-and-fix loop. Each turn this freezes the gems' prediction from the turn-START hand,
/// then measures what ACTUALLY happened and flags any turn where reality violated the prediction, writing a
/// full-state report you can hand back for a fix. It relies on two facts the solver guarantees, so a
/// violation is always a real modelling gap (never a heuristic miss):
///
///   • OFFENSE — you can never deal MORE than <c>MaxPerEnemy[i]</c> (the max over every play sequence). So
///     <c>actualDealt[i] &gt; MaxPerEnemy[i]</c> ⇒ the sim under-modelled your offense (an unmodelled
///     enchant / power / relic added damage it didn't count). Measured at end-of-turn, before enemies act.
///   • DEFENSE — you can never lose FEWER HP than <c>MinHpLost</c> (the min over every play sequence). So
///     <c>actualHpLost &lt; MinHpLost</c> ⇒ the sim over-modelled the danger (it thought that much was
///     unavoidable, but you avoided it). Measured across the enemy turn, at the next turn start.
///
/// Drawing cards mid-turn (more options than the frozen hand) or healing confounds those bounds, so those
/// turns are annotated and NOT flagged. It's opt-in (<see cref="Config.TurnAudit"/>) and log-only: it never
/// touches the game, only reads state the driver already snapshots and writes to modding/logs/.
///
/// Wiring: the driver calls <see cref="Observe"/> on every combat-state change (freeze / defense / flags),
/// and we subscribe once to CombatManager's <c>PlayerEndedTurn</c> for the offense measurement.
/// </summary>
internal static class TurnAudit
{
    private sealed class Turn
    {
        public TurnSim.Result Pred;
        public TurnSimReader.Snapshot Snap = null!;   // the FROZEN turn-start snapshot (what produced Pred)
        public int PlayerHpStart;
        public int[] EnemyBaselineHp = Array.Empty<int>();   // index-aligned with Snap.EnemyRefs
        public HashSet<string> HandStart = new(StringComparer.Ordinal);
        public bool Drew;          // a card whose type wasn't in the start hand appeared ⇒ bounds don't hold
        public bool Healed;        // player HP rose above its turn-start value ⇒ defense measure confounded
        public bool OffenseDone;
        // filled at end-of-turn so the summary line (written at defense-finalize) can report both halves:
        public string OffenseSummary = "offense=?";
    }

    private static bool _subscribed;
    private static CombatSide _lastSide = CombatSide.None;
    private static Turn? _cur;

    // ── entry point, called from TurnSimDriverFeature.Submit on every combat-state change ──
    public static void Observe(TurnSimReader.Snapshot? snap, CombatState state)
    {
        if (!Config.TurnAudit) { _cur = null; _lastSide = CombatSide.None; return; }
        try
        {
            EnsureSubscribed();
            var side = state.CurrentSide;

            // Enemy → Player transition: the previous player turn is over. Finalise its DEFENSE, then start fresh.
            if (side == CombatSide.Player && _lastSide != CombatSide.Player)
            {
                FinalizeDefenseAndSummary(state);
                _cur = (snap != null) ? Freeze(snap, state) : null;
            }
            // Very first observation of a player turn (e.g. combat opened already on the player's side).
            else if (side == CombatSide.Player && _cur == null && snap != null)
            {
                _cur = Freeze(snap, state);
            }

            // While it's our turn, track the confounds (heal / draw) against the frozen turn.
            if (side == CombatSide.Player && _cur != null)
            {
                int hp = PlayerHp(state);
                if (hp > _cur.PlayerHpStart) _cur.Healed = true;
                foreach (var name in RawHandNames(state))
                    if (!_cur.HandStart.Contains(name)) { _cur.Drew = true; break; }
            }

            _lastSide = side;
        }
        catch (Exception e) { DebugLog.Error("TurnAudit.Observe", e); }
    }

    private static void EnsureSubscribed()
    {
        if (_subscribed) return;
        var cm = CombatManager.Instance;
        if (cm == null) return;
        cm.PlayerEndedTurn += OnPlayerEndedTurn;
        _subscribed = true;
    }

    // Fires the instant the player commits End Turn — enemy HP now reflects your plays, before enemies act.
    private static void OnPlayerEndedTurn(Player _, bool __)
    {
        if (!Config.TurnAudit) return;
        try { FinalizeOffense(); }
        catch (Exception e) { DebugLog.Error("TurnAudit.OnPlayerEndedTurn", e); }
    }

    private static Turn Freeze(TurnSimReader.Snapshot snap, CombatState state)
    {
        // Solve the turn-START hand right now so the prediction matches the hand we're auditing (Latest is
        // async and may still hold the previous turn). Once per turn on the game thread — a debug-only cost.
        TurnSim.Result pred;
        try { pred = TurnSim.Solve(snap.Player, snap.Enemies, snap.Hand, nodeCap: 200000); }
        catch (Exception e) { DebugLog.Error("TurnAudit.Freeze/Solve", e); pred = default; }

        var t = new Turn
        {
            Pred = pred,
            Snap = snap,
            PlayerHpStart = PlayerHp(state),
            EnemyBaselineHp = new int[snap.EnemyRefs.Count],
        };
        for (int i = 0; i < snap.EnemyRefs.Count; i++)
            t.EnemyBaselineHp[i] = SafeHp(snap.EnemyRefs[i]);
        foreach (var name in RawHandNames(state)) t.HandStart.Add(name);
        return t;
    }

    private static void FinalizeOffense()
    {
        var t = _cur;
        if (t == null || t.OffenseDone) return;
        t.OffenseDone = true;

        var refs = t.Snap.EnemyRefs;
        var max = t.Pred.MaxPerEnemy ?? Array.Empty<int>();
        var dealt = new int[refs.Count];
        var over = new List<int>();
        for (int i = 0; i < refs.Count; i++)
        {
            int d = t.EnemyBaselineHp[i] - SafeHp(refs[i]);
            if (d < 0) d = 0;
            dealt[i] = d;
            int cap = i < max.Length ? max[i] : 0;
            if (d > cap) over.Add(i);
        }
        t.OffenseSummary = $"dealt=[{string.Join(",", dealt)}] vs max=[{string.Join(",", max)}]";

        // A real over-model needs at least one enemy hit harder than the sim's max — and NOT via drawn cards.
        if (over.Count > 0 && !t.Drew)
        {
            var sb = new StringBuilder();
            sb.AppendLine("VIOLATION: OFFENSE — actual damage dealt exceeded the sim's MaxPerEnemy.");
            foreach (int i in over)
                sb.AppendLine($"    enemy[{i}]: dealt {dealt[i]} > max {(i < max.Length ? max[i] : 0)}  (+{dealt[i] - (i < max.Length ? max[i] : 0)})");
            sb.AppendLine("  ⇒ the sim UNDER-modelled your offense (an unmodelled enchant/power/relic added damage).");
            WriteMismatch("offense", sb.ToString(), t);
        }
    }

    private static void FinalizeDefenseAndSummary(CombatState state)
    {
        var t = _cur;
        if (t == null) return;

        int hpNow = PlayerHp(state);
        int lost = t.PlayerHpStart - hpNow;
        if (lost < 0) lost = 0;
        int min = t.Pred.MinHpLost;

        // A real over-model needs to have lost FEWER HP than the sim's minimum — and NOT because you healed
        // (which lowers net loss) or drew extra defense (more options than the frozen hand).
        bool violation = lost < min && !t.Healed && !t.Drew;
        if (violation)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"VIOLATION: DEFENSE — you lost {lost} HP but the sim's MinHpLost was {min} (−{min - lost}).");
            sb.AppendLine("  ⇒ the sim OVER-modelled the danger (over-counted incoming, or under-counted your mitigation).");
            WriteMismatch("defense", sb.ToString(), t);
        }

        // One summary line per completed turn — coverage + near-misses at a glance, even when clean.
        string flags = (t.Drew ? " drew" : "") + (t.Healed ? " healed" : "");
        string verdict = violation ? "DEFENSE-MISMATCH" : "OK";
        AppendSummary($"turn done: offense {t.OffenseSummary} | hpLost={lost} vs min={min} | killAll={t.Pred.CanKillAll}{(flags.Length > 0 ? " |" + flags : "")} => {verdict}");
    }

    // ── report writers ──
    private static void WriteMismatch(string kind, string verdict, Turn t)
    {
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine($"==================== AUDIT MISMATCH ({kind.ToUpperInvariant()}) ====================");
            sb.AppendLine(verdict);
            sb.AppendLine($"confounds: drew={t.Drew} healed={t.Healed}   (a flagged mismatch has neither set)");
            sb.AppendLine();
            sb.AppendLine("── FROZEN TURN-START STATE (what produced the prediction) ──");
            RenderFrozen(sb, t);
            sb.AppendLine("===============================================================");

            string report = sb.ToString();
            DebugLog.Warn(report);
            var path = Path.Combine(LogDir(), $"mismatch-{kind}-{Stamp()}.txt");
            File.WriteAllText(path, report);
            DebugLog.Warn($"AUDIT: {kind} mismatch written to {path}");
            AppendSummary($"!! {kind.ToUpperInvariant()} MISMATCH -> {Path.GetFileName(path)}");
        }
        catch (Exception e) { DebugLog.Error("TurnAudit.WriteMismatch", e); }
    }

    private static void RenderFrozen(StringBuilder sb, Turn t)
    {
        var p = t.Snap.Player;
        sb.AppendLine($"player: hpStart={t.PlayerHpStart} energy={p.Energy} str={p.Strength} dex={p.Dexterity} weak={p.Weak} frail={p.Frail} vuln={p.Vulnerable} shrink={p.Shrink} intangible={p.Intangible} block={p.Block} vigor={p.Vigor} reactiveBlk={p.ReactiveBlock} endTurnBlockable={p.EndTurnSelfDamageBlockable} endTurnUnblockable={p.EndTurnSelfDamage}");
        sb.AppendLine($"start hand types: [{string.Join(", ", t.HandStart)}]");
        sb.AppendLine($"sim hand ({t.Snap.Hand.Count}) — interpreted:");
        foreach (var c in t.Snap.Hand)
            sb.AppendLine($"    {c.Name}: cost={c.Cost} dmg={c.Damage}x{c.Hits} tgt={c.AttackTarget} block={c.Block} flatBlk={c.FlatBlock} applyVuln={c.ApplyVulnerable} applyWeak={c.ApplyWeak} strGain={c.StrengthGain} xcost={c.XCost} exhaust={c.Exhausts} dyn={c.Dynamic}");
        sb.AppendLine($"enemies ({t.Snap.Enemies.Length}):");
        for (int i = 0; i < t.Snap.Enemies.Length; i++)
        {
            var e = t.Snap.Enemies[i];
            int baseHp = i < t.EnemyBaselineHp.Length ? t.EnemyBaselineHp[i] : -1;
            sb.AppendLine($"    [{i}] hpStart={baseHp} block={e.Block} vuln={e.Vulnerable} weak={e.Weak} str={e.Strength} intent={e.IntentDamage}x{e.IntentHits} perHitCap={e.PerHitCap} dmgTakenPct={e.DamageTakenPct} doom={e.Doom}");
        }
        sb.AppendLine($"prediction: maxDmg={t.Pred.MaxDamage} perEnemy=[{string.Join(",", t.Pred.MaxPerEnemy ?? Array.Empty<int>())}] minHp={t.Pred.MinHpLost} killAll={t.Pred.CanKillAll} nodes={t.Pred.Nodes} truncated={t.Pred.Truncated}");
    }

    private static void AppendSummary(string line)
    {
        try { File.AppendAllText(Path.Combine(LogDir(), "audit-summary.log"), $"[{Stamp()}] {line}{Environment.NewLine}"); }
        catch { }
    }

    // ── helpers ──
    private static int PlayerHp(CombatState state)
    {
        try { return LocalContext.GetMe((IEnumerable<Creature>)state.Creatures)?.CurrentHp ?? 0; }
        catch { return 0; }
    }

    private static int SafeHp(Creature? c)
    {
        try { int hp = c?.CurrentHp ?? 0; return hp < 0 ? 0 : hp; }
        catch { return 0; }
    }

    private static IEnumerable<string> RawHandNames(CombatState state)
    {
        List<string> names = new();
        try
        {
            var hand = LocalContext.GetMe(state)?.PlayerCombatState?.Hand;
            if (hand != null) foreach (var c in hand.Cards) names.Add(c.GetType().Name);
        }
        catch { }
        return names;
    }

    private static string Stamp() => DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");

    private static string LogDir() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Library", "Application Support", "SlayTheSpire2", "modding", "logs");
}
