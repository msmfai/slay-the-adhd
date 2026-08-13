using System;
using System.Collections.Generic;

namespace PokaYokeSpire.Combat;

/// <summary>
/// Pure lethal arithmetic for the green-gem glow: "can the damage cards in my hand finish
/// the enemy this turn?" Deliberately conservative so it never claims a lethal you can't
/// actually land:
///   - respect-energy mode solves a small knapsack for the MOST damage you can afford
///     (0-cost cards are free; X-cost cards are excluded by the caller);
///   - naive mode just sums every damage card ("added up");
///   - it compares against the enemy's effective HP (current HP + block).
/// The feature only invokes this for a SINGLE enemy, so summing is unambiguous.
/// </summary>
public static class LethalCalc
{
    public static bool IsLethal(int dealableDamage, int enemyEffectiveHp)
        => enemyEffectiveHp > 0 && dealableDamage >= enemyEffectiveHp;

    /// Just add up every card's damage, ignoring energy.
    public static int NaiveSum(IReadOnlyList<(int cost, int dmg)> cards)
    {
        int t = 0;
        foreach (var c in cards) t += Math.Max(0, c.dmg);
        return t;
    }

    /// Max total damage from a subset of cards whose total cost fits in `energy`
    /// (0/1 knapsack; 0-cost cards are always free to add).
    public static int MaxDamageWithinEnergy(IReadOnlyList<(int cost, int dmg)> cards, int energy)
    {
        if (energy < 0) energy = 0;
        int free = 0;
        var paid = new List<(int cost, int dmg)>();
        foreach (var c in cards)
        {
            int dmg = Math.Max(0, c.dmg);
            if (c.cost <= 0) free += dmg;            // free cards always playable
            else paid.Add((c.cost, dmg));
        }
        var dp = new int[energy + 1];
        foreach (var (cost, dmg) in paid)
            for (int j = energy; j >= cost; j--)
                dp[j] = Math.Max(dp[j], dp[j - cost] + dmg);
        return free + dp[energy];
    }

    /// A damage card: TotalDamage already folds in multi-hit (per-hit × hits); HitsAll marks
    /// AOE (hits every enemy at once).
    public readonly record struct AtkCard(int Cost, int TotalDamage, bool HitsAll);

    /// Can some affordable play reduce EVERY enemy to 0 this turn? Handles AOE (applies to all
    /// enemies), single-target (a pool distributed across enemies), and multi-hit (baked into
    /// TotalDamage). enemyEffHps are each enemy's HP + block. Conservative: any play it finds
    /// is a real kill (never a false lethal); it may miss some exotic packings (stays silent).
    public static bool CanKillAll(IReadOnlyList<AtkCard> cards, int energy, IReadOnlyList<int> enemyEffHps, bool respectEnergy)
    {
        if (enemyEffHps == null || enemyEffHps.Count == 0) return false;

        if (!respectEnergy)
            return SubsetKills(cards, enemyEffHps);

        // energy-aware: is there an affordable subset (total cost <= energy) that kills all?
        int n = cards.Count;
        if (n == 0) return SubsetKills(cards, enemyEffHps);
        if (n > 20) // too many to enumerate: fall back to cheapest-first affordable set
        {
            var chosen = new List<AtkCard>(); int spent = 0;
            foreach (var c in SortByCost(cards))
                if (spent + System.Math.Max(0, c.Cost) <= energy) { chosen.Add(c); spent += System.Math.Max(0, c.Cost); }
            return SubsetKills(chosen, enemyEffHps);
        }
        for (int mask = 0; mask < (1 << n); mask++)
        {
            int cost = 0; var subset = new List<AtkCard>();
            for (int i = 0; i < n; i++)
                if ((mask & (1 << i)) != 0) { cost += System.Math.Max(0, cards[i].Cost); subset.Add(cards[i]); }
            if (cost <= energy && SubsetKills(subset, enemyEffHps)) return true;
        }
        return false;
    }

    private static IEnumerable<AtkCard> SortByCost(IReadOnlyList<AtkCard> cards)
    {
        var copy = new List<AtkCard>(cards);
        copy.Sort((a, b) => a.Cost.CompareTo(b.Cost));
        return copy;
    }

    /// Does playing exactly this set of cards kill every enemy? AOE hits all; single-target
    /// damage is greedily assigned (largest chunks to largest remaining needs) — a
    /// constructive check, so success means it's genuinely lethal.
    public static bool SubsetKills(IReadOnlyList<AtkCard> cards, IReadOnlyList<int> enemyEffHps)
    {
        int aoe = 0;
        var singles = new List<int>();
        foreach (var c in cards)
        {
            if (c.TotalDamage <= 0) continue;
            if (c.HitsAll) aoe += c.TotalDamage;
            else singles.Add(c.TotalDamage);
        }
        var needs = new List<int>();
        foreach (var hp in enemyEffHps) { int need = hp - aoe; if (need > 0) needs.Add(need); }
        if (needs.Count == 0) return true; // AOE alone finishes everyone

        needs.Sort((a, b) => b.CompareTo(a));      // largest need first
        singles.Sort((a, b) => b.CompareTo(a));    // largest chunk first
        int si = 0;
        foreach (var need in needs)
        {
            int acc = 0;
            while (acc < need && si < singles.Count) { acc += singles[si]; si++; }
            if (acc < need) return false;
        }
        return true;
    }
}
