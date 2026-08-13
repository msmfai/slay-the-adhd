using System.Collections.Generic;
using System.Reflection;
using MegaCrit.Sts2.Core.Combat;             // CombatState
using MegaCrit.Sts2.Core.Context;            // LocalContext
using MegaCrit.Sts2.Core.Entities.Creatures; // Creature

namespace PokaYokeSpire.Combat;

/// <summary>
/// The HP damage that will land on each enemy between the player pressing End Turn and their next
/// chance to play a card — resolved as an ORDERED, HP-capped sim so kills stop later effects and
/// Vulnerable's tick-down is respected (CLAUDE.md-style: correct by construction, not eyeballed):
///
///   enemy turn start : Poison ticks — flat, Unpowered, ignores Vulnerable
///   enemy turn end   : Constrict ticks; Vulnerable/Weak DECREMENT
///   your turn start  : Inferno (Combust) AOE — with the now-DECREMENTED Vulnerable
///
/// A conservative "bag of specific cases": only the reliably-predictable sources are counted; an
/// unrecognized power is simply omitted, so the number never overstates. (Deferred niche cases:
/// Hailstorm — needs a Frost orb in queue; TheBomb — needs its countdown at 1.)
/// </summary>
public static class ScheduledDamage
{
    public static int[] PerEnemy(CombatState combatState, IReadOnlyList<Creature> enemyRefs)
    {
        int n = enemyRefs.Count;
        var outp = new int[n];

        int inferno = 0;
        try
        {
            var meC = LocalContext.GetMe((IEnumerable<Creature>)combatState.Creatures);
            if (meC != null) inferno = CombatSnapshot.PowerAmount(meC, "InfernoPower");
        }
        catch { }

        for (int i = 0; i < n; i++)
        {
            try
            {
                var e = enemyRefs[i];
                int hp = e.CurrentHp;
                int vuln = CombatSnapshot.PowerAmount(e, "VulnerablePower");
                int dealt = 0;

                // poison — flat, ignores Vulnerable
                dealt += Take(ref hp, PoisonNextTurn(e));
                // constrict — flat
                dealt += Take(ref hp, CombatSnapshot.PowerAmount(e, "ConstrictPower"));
                // Vulnerable ticks down at the end of the enemy's turn ...
                int vulnAfter = vuln > 0 ? vuln - 1 : 0;
                // ... so Inferno at YOUR turn start sees the decremented value
                if (inferno > 0) dealt += Take(ref hp, vulnAfter > 0 ? inferno * 3 / 2 : inferno);

                outp[i] = dealt;
            }
            catch { outp[i] = 0; }
        }
        return outp;
    }

    private static int Take(ref int hp, int dmg)
    {
        if (dmg <= 0 || hp <= 0) return 0;
        int d = dmg < hp ? dmg : hp;
        hp -= d;
        return d;
    }

    /// The exact HP a poisoned creature will lose next turn — prefers the game's own
    /// CalculateTotalDamageNextTurn (accounts for Accelerant + hooks), falls back to the raw stacks.
    private static int PoisonNextTurn(Creature e)
    {
        foreach (var p in e.Powers)
            if (p.GetType().Name == "PoisonPower")
            {
                try
                {
                    var m = p.GetType().GetMethod("CalculateTotalDamageNextTurn", BindingFlags.Public | BindingFlags.Instance);
                    if (m != null) return System.Convert.ToInt32(m.Invoke(p, null));
                }
                catch { }
                return p.Amount;
            }
        return 0;
    }
}
