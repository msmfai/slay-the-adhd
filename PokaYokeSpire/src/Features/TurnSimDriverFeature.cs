using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;             // CombatState
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
        int doNothing = p.Valid ? p.NetHpLoss : 0;
        long g = Interlocked.Increment(ref _gen);

        if (sim == null)
        {
            _latest = new Out { DoNothingIncoming = doNothing, Gen = g, HasSim = false };
            return;
        }

        var sched = ScheduledDamage.PerEnemy(state, sim.EnemyRefs);   // game-thread read of enemy powers
        Task.Run(() =>
        {
            TurnSim.Result r;
            try { r = TurnSim.Solve(sim.Player, sim.Enemies, sim.Hand, nodeCap: 200000); }
            catch (System.Exception e) { DebugLog.Error("TurnSim.Solve", e); return; }
            if (Interlocked.Read(ref _gen) != g) return;   // a newer snapshot superseded us
            _latest = new Out { Result = r, EnemyRefs = sim.EnemyRefs, Scheduled = sched, DoNothingIncoming = doNothing, Gen = g, HasSim = true };
            if (r.Truncated) DebugLog.Warn($"turnsim hit the node budget ({r.Nodes}) — result is a conservative bound (cards={sim.Hand.Count}, enemies={sim.Enemies.Length})");
            if (DebugLog.Enabled)
                DebugLog.Debug($"turnsim solved: maxDmg={r.MaxDamage} perEnemy=[{string.Join(",", r.MaxPerEnemy)}] defense base={r.BaselineHpLost}->min={r.MinHpLost} (exact do-nothing={doNothing}) killAll={r.CanKillAll} nodes={r.Nodes} in {sim.Hand.Count} cards");
        });
    }
}
