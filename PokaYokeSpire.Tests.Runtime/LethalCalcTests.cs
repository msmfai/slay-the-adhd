using PokaYokeSpire.Combat;
using Xunit;

namespace PokaYokeSpire.Tests.Runtime;

public class LethalCalcTests
{
    private static (int,int)[] C(params (int,int)[] c) => c;

    [Fact] public void IsLethal_WhenDealableMeetsHp()
    { Assert.True(LethalCalc.IsLethal(12, 12)); Assert.True(LethalCalc.IsLethal(13, 12)); Assert.False(LethalCalc.IsLethal(11, 12)); }

    [Fact] public void IsLethal_ZeroHpEnemyIsNotLethal()  // dead / no enemy -> never glow
    { Assert.False(LethalCalc.IsLethal(5, 0)); }

    [Fact] public void NaiveSum_AddsEveryCard()
    { Assert.Equal(18, LethalCalc.NaiveSum(C((1,6),(1,6),(1,6)))); }

    [Fact] public void Knapsack_RespectsEnergyBudget()
    {
        // three 1-cost Strikes (6 each) but only 2 energy -> best 12
        Assert.Equal(12, LethalCalc.MaxDamageWithinEnergy(C((1,6),(1,6),(1,6)), energy: 2));
        // all affordable -> 18
        Assert.Equal(18, LethalCalc.MaxDamageWithinEnergy(C((1,6),(1,6),(1,6)), energy: 3));
    }

    [Fact] public void Knapsack_PicksHighestDamageWithinBudget()
    {
        // 2 energy: Bash(2cost,8) vs two Strikes(1cost,6 each=12) -> knapsack picks 12
        Assert.Equal(12, LethalCalc.MaxDamageWithinEnergy(C((2,8),(1,6),(1,6)), energy: 2));
    }

    [Fact] public void Knapsack_FreeCardsAlwaysCounted()
    {
        Assert.Equal(10, LethalCalc.MaxDamageWithinEnergy(C((0,4),(0,6)), energy: 0)); // free cards
    }

    [Fact] public void Knapsack_Metamorphic_MoreEnergyNeverLessDamage()
    {
        var cards = C((1,6),(2,10),(1,5),(3,14));
        for (int e = 0; e < 8; e++)
            Assert.True(LethalCalc.MaxDamageWithinEnergy(cards, e + 1) >= LethalCalc.MaxDamageWithinEnergy(cards, e));
    }
}
