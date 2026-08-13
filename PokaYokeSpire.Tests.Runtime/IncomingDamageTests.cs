using PokaYokeSpire.Combat;
using Xunit;

namespace PokaYokeSpire.Tests.Runtime;

/// Unit + metamorphic tests for the end-turn damage arithmetic (the game-coupled parts —
/// intent damage + block registry against live creatures — are exercised by the headless
/// drive; this pins the pure math the readout depends on).
public class IncomingDamageTests
{
    [Theory]
    [InlineData(20, 6, 14)]   // 20 incoming, 6 block -> lose 14
    [InlineData(6, 6, 0)]     // fully blocked
    [InlineData(6, 20, 0)]    // over-blocked -> never negative
    [InlineData(0, 0, 0)]     // nothing incoming
    [InlineData(15, 0, 15)]   // no block
    public void NetHpLoss_IsIncomingMinusBlockFlooredAtZero(int incoming, int block, int expected)
        => Assert.Equal(expected, IncomingDamage.NetHpLoss(incoming, block));

    [Fact]
    public void NetHpLoss_Metamorphic_MoreBlockNeverIncreasesLoss()
    {
        for (int inc = 0; inc <= 30; inc += 5)
            for (int b = 0; b < 30; b++)
                Assert.True(IncomingDamage.NetHpLoss(inc, b + 1) <= IncomingDamage.NetHpLoss(inc, b));
    }

    [Fact]
    public void NetHpLoss_Metamorphic_NeverNegative()
    {
        for (int inc = 0; inc <= 50; inc++)
            for (int b = 0; b <= 60; b += 3)
                Assert.True(IncomingDamage.NetHpLoss(inc, b) >= 0);
    }

    [Fact]
    public void BlockRegistry_HasTheNamedEndOfTurnSources()
    {
        // The user explicitly wanted Plating + relics accounted for.
        Assert.Contains("PlatingPower", EndOfTurnBlockRegistry.Powers.Keys);
        Assert.Contains("MetallicizePower", EndOfTurnBlockRegistry.Powers.Keys);
        Assert.Contains("Orichalcum", EndOfTurnBlockRegistry.Relics.Keys);
    }

    [Fact]
    public void BlockRegistry_Orichalcum_OnlyWhenBlockIsZero()
    {
        var oric = EndOfTurnBlockRegistry.Relics["Orichalcum"];
        Assert.Equal(6, oric(null!, 0)); // no block -> +6
        Assert.Equal(0, oric(null!, 3)); // already have block -> none
    }
}
