using System;

namespace PokaYokeSpire.Spatial;

/// <summary>
/// Pure placement for the two deck-stat panels that flank the card-reward choices — left and right
/// gutters, vertically centred on the choices, clamped on-screen (CLAUDE.md § "Spatial placement").
/// The feature then lints the result against the measured reward/deck rectangles and shrinks the
/// panel until it's clear, so it's correct by construction rather than by eyeballed offsets.
/// </summary>
public static class SidePanelLayout
{
    public const float SideMarginFrac = 0.02f;

    public static (UiRect left, UiRect right) Compute(
        float vpW, float vpH, float panelW, float panelH, float centerY, float marginFrac = SideMarginFrac)
    {
        float m = vpW * marginFrac;
        float y = Clamp(centerY - panelH / 2f, m, vpH - panelH - m);
        var left = new UiRect("statsLeft", m, y, panelW, panelH);
        var right = new UiRect("statsRight", vpW - panelW - m, y, panelW, panelH);
        return (left, right);
    }

    private static float Clamp(float v, float lo, float hi) => v < lo ? lo : (v > hi ? hi : (hi < lo ? lo : v));
}
