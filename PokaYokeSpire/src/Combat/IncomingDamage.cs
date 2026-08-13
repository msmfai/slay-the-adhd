using System;
using System.Collections.Generic;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.MonsterMoves.Intents;

namespace PokaYokeSpire.Combat;

/// <summary>
/// Predicts what happens if you end your turn RIGHT NOW:
///   incoming = sum over each enemy's queued attack intent of GetTotalDamage([me], enemy)
///              — this runs the GAME'S OWN damage hook, so Strength/Weak/Vulnerable and any
///              relic/power that modifies damage are already baked in and exact;
///   blockAtEnemyTurn = your current block + end-of-turn block gains (Plating, Metallicize,
///              Orichalcum, ... from EndOfTurnBlockRegistry);
///   netHpLoss = max(0, incoming - blockAtEnemyTurn).
/// </summary>
public static class IncomingDamage
{
    public readonly record struct Prediction(int Incoming, int BlockAtEnemyTurn, int NetHpLoss, bool Valid);

    /// Pure arithmetic — unit-testable without the game.
    public static int NetHpLoss(int incoming, int blockAtEnemyTurn) => Math.Max(0, incoming - blockAtEnemyTurn);

    public static Prediction Compute(CombatState state)
    {
        Creature? me = LocalContext.GetMe((IEnumerable<Creature>)state.Creatures);
        if (me == null) return new Prediction(0, 0, 0, false);

        var targets = new[] { me };
        int incoming = 0;
        foreach (var enemy in state.Enemies)
        {
            var move = enemy.Monster?.NextMove;
            if (move == null) continue;
            foreach (var intent in move.Intents)
                if (intent is AttackIntent atk)
                    incoming += atk.GetTotalDamage(targets, enemy); // game-accurate, includes my Vulnerable
        }

        int blockNow = me.Block;
        int blockAtEnemyTurn = blockNow + EndOfTurnBlockRegistry.Predict(me, blockNow);
        return new Prediction(incoming, blockAtEnemyTurn, NetHpLoss(incoming, blockAtEnemyTurn), true);
    }
}
