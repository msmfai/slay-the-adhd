using PokaYokeSpire.Combat;
using Xunit;

namespace PokaYokeSpire.Tests.Runtime;

public class CardTargetPreviewTests
{
    [Theory]
    [InlineData(9, 0, 9)]    // no block -> full damage
    [InlineData(9, 5, 4)]    // partial block
    [InlineData(6, 6, 0)]    // exactly blocked
    [InlineData(6, 20, 0)]   // over-blocked -> 0, never negative
    [InlineData(0, 4, 0)]    // non-attack card
    public void HpThroughBlock_IsDamageMinusBlockFlooredAtZero(int dmg, int block, int expected)
        => Assert.Equal(expected, CardTargetPreview.HpThroughBlock(dmg, block));

    [Fact]
    public void HpThroughBlock_Metamorphic_MoreBlockNeverIncreasesHp()
    {
        for (int d = 0; d <= 40; d += 4)
            for (int b = 0; b < 40; b++)
                Assert.True(CardTargetPreview.HpThroughBlock(d, b + 1) <= CardTargetPreview.HpThroughBlock(d, b));
    }
}
