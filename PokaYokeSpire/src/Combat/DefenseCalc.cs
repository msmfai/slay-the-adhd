using System;
using System.Collections.Generic;

namespace PokaYokeSpire.Combat;

/// <summary>
/// The MINIMUM HP you can lose this turn — the defensive mirror of the offense gem's "max damage you
/// can deal". It's <c>max(0, incoming − blockAtEnemyTurn − bestHandBlock)</c> where:
///   • <c>incoming</c> is the game-accurate enemy attack total (already includes your Vulnerable);
///   • <c>blockAtEnemyTurn</c> is your current block PLUS everything that triggers when you end the turn
///     before the enemies attack — Plating, Metallicize, Orichalcum, end-of-turn relics (computed by
///     <see cref="IncomingDamage"/> / <see cref="EndOfTurnBlockRegistry"/>);
///   • <c>bestHandBlock</c> is the most block you can put up by playing your hand's block cards within
///     your energy, each adjusted for Dexterity (+) and Frail (−25%).
///
/// Conservative by construction: it counts BLOCK only (not killing an attacker to remove its damage),
/// so the number it shows is an upper bound on the damage you'd actually take — it never claims you're
/// safer than you provably are. Pure + unit-tested.
/// </summary>
public static class DefenseCalc
{
    /// A hand card that grants block, with its printed block and energy cost.
    public readonly record struct BlockCard(int Cost, int Block);

    /// Effective block one card grants: (printed + Dexterity), then −25% (floored) if Frail; never < 0.
    public static int EffectiveBlock(int printed, int dexterity, bool frail)
    {
        int b = printed + dexterity;
        if (b < 0) b = 0;
        if (frail) b = b * 3 / 4;   // Frail: gain 25% less block, rounded down
        return b;
    }

    /// Most total block obtainable by playing a subset of block cards within <paramref name="energy"/>
    /// (0/1 knapsack; free cards always taken). Each card's block is Dexterity/Frail-adjusted; cards that
    /// would grant ≤ 0 block are skipped.
    public static int MaxBlock(IReadOnlyList<BlockCard> cards, int energy, int dexterity, bool frail)
    {
        if (cards == null || cards.Count == 0) return 0;
        if (energy < 0) energy = 0;

        var dp = new int[energy + 1];   // dp[e] = best block using exactly-up-to e energy
        foreach (var c in cards)
        {
            int val = EffectiveBlock(c.Block, dexterity, frail);
            if (val <= 0) continue;                       // never worth playing for block
            int cost = c.Cost < 0 ? 0 : c.Cost;
            if (cost == 0) { for (int e = 0; e <= energy; e++) dp[e] += val; continue; }  // free: always play
            if (cost > energy) continue;                  // unaffordable alone
            for (int e = energy; e >= cost; e--)
                if (dp[e - cost] + val > dp[e]) dp[e] = dp[e - cost] + val;
        }
        return dp[energy];
    }

    /// Minimum HP loss = max(0, incoming − block-at-enemy-turn − best hand block).
    public static int MinDamageTaken(int incoming, int blockAtEnemyTurn, int handBlock) =>
        Math.Max(0, incoming - blockAtEnemyTurn - handBlock);
}
