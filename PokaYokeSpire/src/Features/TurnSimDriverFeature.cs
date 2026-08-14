using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;             // CombatState
using MegaCrit.Sts2.Core.Context;            // LocalContext
using MegaCrit.Sts2.Core.Entities.Creatures; // Creature
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
        var p = IncomingDamage.Compute(state);
        // do-nothing incoming = enemy attacks (exact game hook) + Burn/Toxic-style end-of-turn self-damage
        int doNothing = (p.Valid ? p.NetHpLoss : 0) + (sim?.Player.EndTurnSelfDamage ?? 0);
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
                DebugLog.Debug($"turnsim solved: maxDmg={r.MaxDamage} perEnemy=[{string.Join(",", r.MaxPerEnemy)}] minHp={r.MinHpLost} (exact do-nothing={doNothing}) killAll={r.CanKillAll} nodes={r.Nodes} in {sim.Hand.Count} cards");

            // When defense drops below the do-nothing, dump the hand so the CAUSE is unambiguous:
            // a real block/Weaken card explains it; a pure attack (dN,blk0,wk0) would be a bug.
            if (r.MinHpLost < doNothing && DebugLog.Enabled)
            {
                var sb = new System.Text.StringBuilder();
                foreach (var c in sim.Hand) sb.Append($"{c.Name}(d{c.Damage},blk{c.Block},wk{c.ApplyWeak},vul{c.ApplyVulnerable},x{(c.XCost ? 1 : 0)}) ");
                DebugLog.Warn($"defense {doNothing}->{r.MinHpLost}: hand = {sb}");
            }
        });
    }
}
