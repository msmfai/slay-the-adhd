using System.Collections.Generic;
using Xunit;
using PokaYokeSpire.Combat;
using Block = PokaYokeSpire.Combat.DefenseCalc.BlockCard;

namespace PokaYokeSpire.Tests.Runtime;

/// <summary>
/// The defense minimum (mirror of the offense gem's max-damage): the least HP you can lose by playing
/// your hand's block optimally, on top of current + end-of-turn block. Pure knapsack + arithmetic.
/// </summary>
public class DefenseCalcTests
{
    [Fact]
    public void EffectiveBlock_AddsDexterity_AndFrailReduces()
    {
        Assert.Equal(5, DefenseCalc.EffectiveBlock(5, 0, false));
        Assert.Equal(8, DefenseCalc.EffectiveBlock(5, 3, false));   // +Dexterity
        Assert.Equal(6, DefenseCalc.EffectiveBlock(8, 0, true));    // Frail: 8*3/4 = 6
        Assert.Equal(0, DefenseCalc.EffectiveBlock(-4, 1, false));  // never negative
    }

    [Fact]
    public void MaxBlock_PicksBestWithinEnergy()
    {
        var hand = new List<Block> { new(1, 5), new(1, 5), new(2, 12) };   // two Defends + a big block
        Assert.Equal(12, DefenseCalc.MaxBlock(hand, energy: 2, dexterity: 0, frail: false));  // the 2-cost 12
        Assert.Equal(17, DefenseCalc.MaxBlock(hand, energy: 3, dexterity: 0, frail: false));  // 12 + one Defend
        Assert.Equal(22, DefenseCalc.MaxBlock(hand, energy: 4, dexterity: 0, frail: false));  // all three
    }

    [Fact]
    public void MaxBlock_FreeCardsAlwaysCounted()
    {
        var hand = new List<Block> { new(0, 4), new(2, 10) };
        Assert.Equal(4, DefenseCalc.MaxBlock(hand, energy: 0, dexterity: 0, frail: false));   // only the free one
        Assert.Equal(14, DefenseCalc.MaxBlock(hand, energy: 2, dexterity: 0, frail: false));  // free + costed
    }

    [Fact]
    public void MaxBlock_AppliesDexterityAndFrail_PerCard()
    {
        var hand = new List<Block> { new(1, 5), new(1, 5) };
        Assert.Equal(16, DefenseCalc.MaxBlock(hand, energy: 2, dexterity: 3, frail: false));  // (5+3)*2
        Assert.Equal(12, DefenseCalc.MaxBlock(hand, energy: 2, dexterity: 3, frail: true));   // floor(8*.75)=6 each
    }

    [Fact]
    public void MaxBlock_Metamorphic_MoreEnergyNeverLessBlock()
    {
        var hand = new List<Block> { new(1, 5), new(2, 9), new(3, 16), new(1, 6) };
        int prev = 0;
        for (int e = 0; e <= 6; e++)
        {
            int b = DefenseCalc.MaxBlock(hand, e, dexterity: 1, frail: false);
            Assert.True(b >= prev, $"energy {e}: block {b} < {prev}");
            prev = b;
        }
    }

    [Fact]
    public void MinDamageTaken_SubtractsBlock_FlooredAtZero()
    {
        Assert.Equal(4, DefenseCalc.MinDamageTaken(incoming: 20, blockAtEnemyTurn: 6, handBlock: 10));  // 20-16
        Assert.Equal(0, DefenseCalc.MinDamageTaken(incoming: 12, blockAtEnemyTurn: 8, handBlock: 10));  // over-blocked
        Assert.Equal(0, DefenseCalc.MinDamageTaken(incoming: 0, blockAtEnemyTurn: 0, handBlock: 0));
    }

    [Fact]
    public void NoBlockCards_MinEqualsIncomingMinusEndOfTurnBlock()
    {
        // with an empty hand, the minimum is just what end-of-turn block leaves (the x/do-nothing case)
        Assert.Equal(0, DefenseCalc.MaxBlock(new List<Block>(), 3, 0, false));
        Assert.Equal(9, DefenseCalc.MinDamageTaken(15, 6, 0));
    }
}
