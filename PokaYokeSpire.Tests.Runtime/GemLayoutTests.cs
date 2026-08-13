using System.Linq;
using Xunit;
using PokaYokeSpire.Spatial;

namespace PokaYokeSpire.Tests.Runtime;

/// <summary>
/// Lints over the combat-gem placement (<see cref="GemLayout"/>) — the exact geometry the gem bug got
/// wrong this session (art collapsed to 0×0; gems drifted up-and-left onto the counter). Placement is
/// data + lints, not eyeballed pixels (CLAUDE.md § Spatial): construct the orb hypergraph and assert
/// no occlusion, mirror symmetry, and same-height flanking, for the real counter size and a range.
/// </summary>
public class GemLayoutTests
{
    // The real counter measured in-game is 128px square; test a spread so the math is size-independent.
    public static readonly float[] Sizes = { 96f, 110f, 128f, 160f, 200f };

    [Theory]
    [InlineData(96f)] [InlineData(110f)] [InlineData(128f)] [InlineData(160f)] [InlineData(200f)]
    public void Orbs_NeverOcclude_CounterOrOtherGem(float size)
    {
        var orbs = GemLayout.OrbRects(size);
        // Zero tolerance: a gem touching the counter's orb (or the other gem) at all is a violation.
        var bad = SpatialGraph.OcclusionViolations(orbs, maxOverlapFrac: 0f);
        Assert.True(bad.Count == 0, $"size {size}: occluding orb pairs: {string.Join(", ", bad)}");
    }

    [Theory]
    [InlineData(96f)] [InlineData(110f)] [InlineData(128f)] [InlineData(160f)] [InlineData(200f)]
    public void Gems_FlankSymmetrically_AtCounterHeight(float size)
    {
        var orbs = GemLayout.OrbRects(size);
        var counter = orbs.Single(o => o.Id == "counter");
        var l = orbs.Single(o => o.Id == "gemL");
        var r = orbs.Single(o => o.Id == "gemR");

        float cx = counter.X + counter.W / 2f, cy = counter.Y + counter.H / 2f;
        float lx = l.X + l.W / 2f, ly = l.Y + l.H / 2f;
        float rx = r.X + r.W / 2f, ry = r.Y + r.H / 2f;

        Assert.Equal(cy, ly, 3);                       // same height as the counter orb (the "top-left" bug)
        Assert.Equal(cy, ry, 3);
        Assert.True(lx < cx && rx > cx, "left gem must be left of centre, right gem right of centre");
        Assert.Equal(cx - lx, rx - cx, 3);             // mirror-symmetric about the counter centre
    }

    [Theory]
    [InlineData(96f)] [InlineData(110f)] [InlineData(128f)] [InlineData(160f)] [InlineData(200f)]
    public void Gems_AreTwoThirdsRadius_WithExactGapClearance(float size)
    {
        Assert.Equal(size * 0.5f * (2f / 3f), GemLayout.GemOrbRadius(size), 3);   // ⅔ radius (user spec)

        // edge-to-edge clearance between the counter orb and each gem orb is exactly Gap.
        var orbs = GemLayout.OrbRects(size);
        float gap = SpatialGraph.HorizontalGap(
            orbs.Single(o => o.Id == "counter"), orbs.Single(o => o.Id == "gemR"));
        Assert.Equal(GemLayout.Gap, gap, 3);
    }

    [Theory]   // live tuning can change scale/gap; the geometry invariants must hold across their range
    [InlineData(0.40f, 6f)] [InlineData(0.5f, 14f)] [InlineData(2f / 3f, 14f)]
    [InlineData(0.80f, 30f)] [InlineData(1.0f, 0f)]
    public void Invariants_HoldAcrossTunableScaleAndGap(float scale, float gap)
    {
        const float size = 128f;
        var orbs = GemLayout.OrbRects(size, scale, gap);
        Assert.Empty(SpatialGraph.OcclusionViolations(orbs, maxOverlapFrac: 0f));   // never occlude

        var counter = orbs.Single(o => o.Id == "counter");
        var l = orbs.Single(o => o.Id == "gemL");
        var r = orbs.Single(o => o.Id == "gemR");
        float cx = counter.X + counter.W / 2f, cy = counter.Y + counter.H / 2f;
        Assert.Equal(cy, l.Y + l.H / 2f, 3);                    // same height
        Assert.Equal(cy, r.Y + r.H / 2f, 3);
        Assert.Equal(cx - (l.X + l.W / 2f), (r.X + r.W / 2f) - cx, 3);   // symmetric
        Assert.Equal(gap, SpatialGraph.HorizontalGap(counter, r), 3);    // clearance == gap exactly
    }

    [Fact]
    public void ArtCentre_MapsBesideCounter_NotOntoOrigin()
    {
        // Regression pin for the exact fix: with pivot at size/2 (not 0), the gem's orb centre lands at
        // ±OffX + half — beside the counter's centre at half. Scaling about (0,0) would have put it at
        // ±OffX + half*Scale, up-and-left. Assert the correct mapping.
        float size = 128f, half = size / 2f;
        var plan = GemLayout.For(size);
        float correctRightCentre = plan.OffX + half;         // Position.x + Pivot.x
        float buggyRightCentre = plan.OffX + half * plan.Scale; // if pivot were 0 → wrong
        Assert.NotEqual(correctRightCentre, buggyRightCentre, 3);

        var r = GemLayout.OrbRects(size).Single(o => o.Id == "gemR");
        Assert.Equal(correctRightCentre, r.X + r.W / 2f, 3);
    }
}
