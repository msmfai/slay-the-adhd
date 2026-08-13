using System;

namespace PokaYokeSpire.Spatial;

/// <summary>
/// Pure layout math for the "deck under the reward" display — no Godot, so it's driven entirely
/// by the spatial tests + lints (see CLAUDE.md § "Spatial placement"). Produces the rectangle of
/// every card in the fan plus the (raised) reward-choices area, so the same geometry the feature
/// renders is the geometry the lints check.
///
/// Raise relationship the player asked for: the reward CHOICES lift by <see cref="RewardRaise"/>,
/// the deck FAN lifts by half that (<see cref="DeckRaise"/>). Lifting the choices more than the
/// fan widens the vertical gap between the two groups (they must never occlude — see the tests).
/// </summary>
public static class DeckRowLayout
{
    public const float RewardRaise = 64f;          // reward choices lifted this many px …
    public const float DeckRaise = RewardRaise / 2f; // … the deck fan lifts by half that
    public const float FanGap = 14f;               // preferred clear gap between fully-spread cards
    public const float MinVisibleStep = 26f;       // min sliver of an overlapped card (spacing lint)
    public const float SideMarginFrac = 0.06f;     // keep the fan this far off each screen edge
    public const float BottomPad = 12f;            // gap from the fan to the screen bottom
    public const float GroupMinGap = 24f;          // min clearance between reward area and the fan

    public readonly record struct Result(UiRect[] DeckCards, UiRect RewardArea, float Step);

    /// <param name="vpW">viewport width</param><param name="vpH">viewport height</param>
    /// <param name="deckCount">cards in the player's deck</param>
    /// <param name="cardW">rendered (scaled) width of one fan card</param>
    /// <param name="cardH">rendered (scaled) height of one fan card</param>
    /// <param name="rewardCenterY">y-centre of the reward choices BEFORE raising</param>
    /// <param name="rewardGroupW">total width spanned by the reward choices</param>
    /// <param name="rewardGroupH">height of a reward choice card</param>
    public static Result Compute(
        float vpW, float vpH, int deckCount, float cardW, float cardH,
        float rewardCenterY, float rewardGroupW, float rewardGroupH,
        float rewardRaise = RewardRaise, float deckRaise = DeckRaise,
        float fanGap = FanGap, float minVisibleStep = MinVisibleStep)
    {
        // reward choices, raised up and centred horizontally
        float rewCenterY = rewardCenterY - rewardRaise;
        var rewardArea = new UiRect(
            "reward", (vpW - rewardGroupW) / 2f, rewCenterY - rewardGroupH / 2f, rewardGroupW, rewardGroupH);

        int n = Math.Max(deckCount, 0);
        var cards = new UiRect[n];
        if (n == 0) return new Result(cards, rewardArea, 0f);

        float step = Step(vpW, n, cardW, fanGap, minVisibleStep);
        float totalW = cardW + step * (n - 1);
        float startX = (vpW - totalW) / 2f;
        float y = vpH - cardH - BottomPad - deckRaise;
        for (int i = 0; i < n; i++)
            cards[i] = new UiRect($"deck{i}", startX + step * i, y, cardW, cardH);

        return new Result(cards, rewardArea, step);
    }

    /// Horizontal step between successive fan cards: spread up to one card + <see cref="FanGap"/>
    /// when there's room, otherwise overlap — but never buried tighter than an edge-to-edge fill.
    public static float Step(float vpW, int deckCount, float cardW, float fanGap = FanGap, float minVisibleStep = MinVisibleStep)
    {
        if (deckCount <= 1) return 0f;
        float margin = vpW * SideMarginFrac;
        float usable = vpW - margin * 2f - cardW;
        float step = MathF.Min(cardW + fanGap, usable / (deckCount - 1));
        if (step < minVisibleStep)
        {
            // too crowded for the preferred sliver: fall back to spanning the FULL width so every
            // card stays on-screen. For an enormous deck this can still dip below MinVisibleStep,
            // which the spacing lint reports honestly rather than shoving cards off-screen.
            step = (vpW - cardW) / (deckCount - 1);
            if (step < 1f) step = 1f;
        }
        return step;
    }
}
