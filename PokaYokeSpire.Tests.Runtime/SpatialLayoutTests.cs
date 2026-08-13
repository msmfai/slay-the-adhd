using System.Collections.Generic;
using System.Linq;
using PokaYokeSpire.Spatial;
using Xunit;

namespace PokaYokeSpire.Tests.Runtime;

/// <summary>
/// The spatial lints ARE the eyes an LLM doesn't have (CLAUDE.md § "Spatial placement").
/// These pin the hypergraph feature math and assert every "deck under the reward" layout is
/// occlusion-free, on-screen, and clear of the reward choices — for real viewport sizes.
/// </summary>
public class SpatialLayoutTests
{
    // representative rendered footprints (scaled ~0.34 fan card; a reward choice card)
    const float FanW = 122f, FanH = 258f;
    const float RewCenterY1080 = 345f, RewGroupW1080 = 1050f, RewGroupH1080 = 470f;

    static UiRect R(string id, float x, float y, float w, float h) => new(id, x, y, w, h);

    // Real UI scales with resolution, so the representative footprints do too (relative to 1080p).
    static (float fanW, float fanH, float rewH) Foot(float vpH)
    {
        float s = vpH / 1080f;
        return (FanW * s, FanH * s, 470f * s);
    }

    // ───────────────────────────── pairwise primitives ─────────────────────────────

    [Fact] public void Intersect_TrueWhenOverlapping() =>
        Assert.True(SpatialGraph.Intersects(R("a", 0, 0, 10, 10), R("b", 5, 5, 10, 10)));

    [Fact] public void Intersect_FalseWhenTouchingEdges() =>
        Assert.False(SpatialGraph.Intersects(R("a", 0, 0, 10, 10), R("b", 10, 0, 10, 10)));

    [Fact] public void OverlapFraction_HalfCoverOfSmaller()
    {
        // b (10×10) half-covered by a: overlap 5×10=50, smaller area 100 -> 0.5
        Assert.Equal(0.5f, SpatialGraph.OverlapFraction(R("a", 0, 0, 10, 10), R("b", 5, 0, 10, 10)), 3);
    }

    [Fact] public void HorizontalGap_PositiveWhenApart() =>
        Assert.Equal(5f, SpatialGraph.HorizontalGap(R("a", 0, 0, 10, 10), R("b", 15, 0, 10, 10)), 3);

    [Fact] public void HorizontalGap_NegativeWhenOverlapping() =>
        Assert.Equal(-4f, SpatialGraph.HorizontalGap(R("a", 0, 0, 10, 10), R("b", 6, 0, 10, 10)), 3);

    [Fact] public void VerticalGap_PositiveWhenStacked() =>
        Assert.Equal(20f, SpatialGraph.VerticalGap(R("top", 0, 0, 10, 10), R("bot", 0, 30, 10, 10)), 3);

    [Fact] public void VisibleStep_IsLeftEdgeDelta() =>
        Assert.Equal(7f, SpatialGraph.VisibleStep(R("a", 0, 0, 30, 30), R("b", 7, 0, 30, 30)), 3);

    // ───────────────────────────── triplet / quad features ─────────────────────────

    [Fact]
    public void Triplet_CollinearOrderedEvenlySpaced()
    {
        var a = R("a", 0, 100, 20, 20); var b = R("b", 40, 100, 20, 20); var c = R("c", 80, 100, 20, 20);
        Assert.True(SpatialGraph.Collinear(a, b, c));
        Assert.True(SpatialGraph.OrderedX(a, b, c));
        Assert.True(SpatialGraph.EvenlySpacedX(a, b, c));
    }

    [Fact]
    public void Triplet_UnevenSpacingDetected()
    {
        var a = R("a", 0, 0, 20, 20); var b = R("b", 30, 0, 20, 20); var c = R("c", 100, 0, 20, 20);
        Assert.False(SpatialGraph.EvenlySpacedX(a, b, c));
    }

    [Fact]
    public void Quad_MirrorSymmetricFan()
    {
        // centres at 10,40,60,90 -> axis 50; 10↔90 and 40↔60 mirror.
        var a = R("a", 0, 0, 20, 20); var b = R("b", 30, 0, 20, 20);
        var c = R("c", 50, 0, 20, 20); var d = R("d", 80, 0, 20, 20);
        Assert.True(SpatialGraph.MirrorSymmetricX(a, b, c, d, 50f));
    }

    // ───────────────────────────── lints on hand-built sets ────────────────────────

    [Fact]
    public void Occlusion_FlagsForbiddenOverlap()
    {
        var rects = new List<UiRect> { R("a", 0, 0, 10, 10), R("b", 5, 0, 10, 10) };
        Assert.NotEmpty(SpatialGraph.OcclusionViolations(rects, maxOverlapFrac: 0f)); // zero budget -> flagged
        Assert.Empty(SpatialGraph.OcclusionViolations(rects, maxOverlapFrac: 0.6f)); // 0.5 cover within budget
    }

    [Fact]
    public void MinSpacing_FlagsBuriedCard()
    {
        var row = new List<UiRect> { R("a", 0, 0, 30, 30), R("b", 4, 0, 30, 30) }; // 4px sliver
        Assert.NotEmpty(SpatialGraph.MinSpacingViolations(row, minStep: 10f));
        Assert.Empty(SpatialGraph.MinSpacingViolations(row, minStep: 3f));
    }

    // ───────────────────────── the real deck-under-reward layout ───────────────────

    public static IEnumerable<object[]> Screens() => new[]
    {
        new object[] { 1920f, 1080f }, new object[] { 2560f, 1440f }, new object[] { 1280f, 720f },
    };

    [Theory]
    [MemberData(nameof(Screens))]
    public void Deck_AllOnScreen_ForTypicalSizes(float vpW, float vpH)
    {
        var (fw, fh, rh) = Foot(vpH);
        for (int n = 5; n <= 40; n += 5)
        {
            var r = DeckRowLayout.Compute(vpW, vpH, n, fw, fh, vpH * 0.32f, vpW * 0.55f, rh);
            Assert.Empty(SpatialGraph.OutOfBounds(r.DeckCards, vpW, vpH));
        }
    }

    [Fact]
    public void Deck_IsCollinearOrderedAndEvenlySpaced()
    {
        var r = DeckRowLayout.Compute(1920, 1080, 12, FanW, FanH, RewCenterY1080, RewGroupW1080, RewGroupH1080);
        var cards = r.DeckCards;
        for (int i = 0; i + 2 < cards.Length; i++)
        {
            Assert.True(SpatialGraph.Collinear(cards[i], cards[i + 1], cards[i + 2]), $"row not flat at {i}");
            Assert.True(SpatialGraph.OrderedX(cards[i], cards[i + 1], cards[i + 2]), $"row not ordered at {i}");
            Assert.True(SpatialGraph.EvenlySpacedX(cards[i], cards[i + 1], cards[i + 2]), $"uneven at {i}");
        }
    }

    [Fact]
    public void Deck_FanIsMirrorSymmetric()
    {
        var r = DeckRowLayout.Compute(1920, 1080, 8, FanW, FanH, RewCenterY1080, RewGroupW1080, RewGroupH1080);
        var c = r.DeckCards;
        float axis = 1920f / 2f;
        Assert.True(SpatialGraph.MirrorSymmetricX(c[0], c[1], c[^2], c[^1], axis, eps: 1.0f));
    }

    [Theory]
    [InlineData(5)][InlineData(10)][InlineData(20)][InlineData(30)]
    public void Deck_EveryCardHasAVisibleSliver_AtTypicalSizes(int n)
    {
        var r = DeckRowLayout.Compute(1920, 1080, n, FanW, FanH, RewCenterY1080, RewGroupW1080, RewGroupH1080);
        Assert.Empty(SpatialGraph.MinSpacingViolations(r.DeckCards, DeckRowLayout.MinVisibleStep));
    }

    [Fact]
    public void Deck_MinSpacingLint_FiresWhenAbsurdlyCrowded()
    {
        // 200 cards on a 1280-wide screen physically cannot each show 26px — the lint must SAY so
        // (proving it's a real check, not decoration), rather than silently clipping cards.
        var r = DeckRowLayout.Compute(1280, 720, 200, FanW, FanH, 720 * 0.32f, 700f, 470f);
        Assert.NotEmpty(SpatialGraph.MinSpacingViolations(r.DeckCards, DeckRowLayout.MinVisibleStep));
        Assert.Empty(SpatialGraph.OutOfBounds(r.DeckCards, 1280, 720)); // …but still keeps them on-screen
    }

    [Theory]
    [MemberData(nameof(Screens))]
    public void RewardAndDeck_NeverOcclude(float vpW, float vpH)
    {
        var (fw, fh, rh) = Foot(vpH);
        for (int n = 5; n <= 40; n += 5)
        {
            var r = DeckRowLayout.Compute(vpW, vpH, n, fw, fh, vpH * 0.32f, vpW * 0.55f, rh);
            var deckBox = SpatialGraph.BoundingBox("deck", r.DeckCards);
            Assert.True(SpatialGraph.GroupsVerticallyClear(r.RewardArea, deckBox, DeckRowLayout.GroupMinGap),
                $"reward choices collide with the deck fan (n={n}, {vpW}x{vpH})");
        }
    }

    [Fact]
    public void Raise_ChoicesLiftMoreThanFan_WidensTheGap()
    {
        // requested relationship: reward lifts by X, deck lifts by X/2 -> gap grows vs no raise.
        var raised = DeckRowLayout.Compute(1920, 1080, 12, FanW, FanH, RewCenterY1080, RewGroupW1080, RewGroupH1080,
            rewardRaise: DeckRowLayout.RewardRaise, deckRaise: DeckRowLayout.DeckRaise);
        var flat = DeckRowLayout.Compute(1920, 1080, 12, FanW, FanH, RewCenterY1080, RewGroupW1080, RewGroupH1080,
            rewardRaise: 0f, deckRaise: 0f);

        float gapRaised = SpatialGraph.VerticalGap(raised.RewardArea, SpatialGraph.BoundingBox("d", raised.DeckCards));
        float gapFlat = SpatialGraph.VerticalGap(flat.RewardArea, SpatialGraph.BoundingBox("d", flat.DeckCards));
        Assert.True(gapRaised > gapFlat, "lifting the choices more than the fan must widen their gap");
        Assert.True(DeckRowLayout.RewardRaise > DeckRowLayout.DeckRaise, "choices must lift more than the fan");
    }
}
