using PokaYokeSpire;
using Xunit;

namespace PokaYokeSpire.Tests.Runtime;

/// Unit + metamorphic tests for the overlay positioning math (pure, no Godot).
public class LayoutTests
{
    static double Mag(float x, float y) => System.Math.Sqrt(x * (double)x + y * (double)y);

    // --- Row (blue counters) ---
    [Fact] public void Row_FirstIsOneGapLeft() => Assert.Equal((-96f, 0f), Layout.RowCenterOffset(0, 96));
    [Fact] public void Row_IsHorizontal() => Assert.Equal(0f, Layout.RowCenterOffset(3, 96).y);

    [Theory]
    [InlineData(0, 1)][InlineData(1, 2)][InlineData(2, 3)][InlineData(5, 9)]
    public void Row_MonotonicallyLeftAndEvenlySpaced(int i, int j)
    {
        // METAMORPHIC: later index is further left, spacing is exactly the gap.
        const float gap = 40f;
        var a = Layout.RowCenterOffset(i, gap);
        var b = Layout.RowCenterOffset(j, gap);
        Assert.True(b.x < a.x, "later counter must be further left");
        Assert.Equal((j - i) * -gap, b.x - a.x, 3);
    }

    // --- Radial (relic wheel) ---
    [Fact]
    public void Radial_SingleIsStraightAbove()
    {
        var (x, y) = Layout.RadialCenterOffset(0, 1, 100f);
        Assert.Equal(0f, x, 3);      // centred on top
        Assert.Equal(-100f, y, 3);   // straight up (y-down)
    }

    [Theory]
    [InlineData(1)][InlineData(2)][InlineData(3)][InlineData(6)][InlineData(11)]
    public void Radial_AllOnTheCircle(int count)
    {
        // METAMORPHIC: every relic sits exactly `radius` from the centre.
        const float r = 130f;
        for (int i = 0; i < count; i++)
        {
            var (x, y) = Layout.RadialCenterOffset(i, count, r);
            Assert.Equal(r, Mag(x, y), 2);
        }
    }

    [Theory]
    [InlineData(2)][InlineData(3)][InlineData(4)][InlineData(7)]
    public void Radial_BalancedAroundTop(int count)
    {
        // METAMORPHIC: the set's angular centre of gravity is straight up -> the x
        // offsets cancel (sum ~ 0) and the average position is directly above centre.
        float sumX = 0, sumY = 0;
        for (int i = 0; i < count; i++)
        {
            var (x, y) = Layout.RadialCenterOffset(i, count, 100f);
            sumX += x; sumY += y;
        }
        Assert.Equal(0f, sumX, 2);   // horizontally balanced
        Assert.True(sumY < 0, "cluster sits above the centre");
    }

    [Fact]
    public void Radial_MirrorSymmetry()
    {
        // METAMORPHIC: element i and element (count-1-i) are horizontal mirrors.
        const int count = 5; const float r = 100f;
        for (int i = 0; i < count; i++)
        {
            var (xi, yi) = Layout.RadialCenterOffset(i, count, r);
            var (xj, yj) = Layout.RadialCenterOffset(count - 1 - i, count, r);
            Assert.Equal(-xi, xj, 2); // mirrored x
            Assert.Equal(yi, yj, 2);  // same height
        }
    }
}
