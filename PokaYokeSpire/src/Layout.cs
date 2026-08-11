using System;

namespace PokaYokeSpire;

/// <summary>
/// Pure positioning math for the energy-counter overlays — no Godot dependency, so it's
/// directly unit/metamorphic testable. Offsets are from the cluster CENTRE to each
/// element's centre, in local (y-down) coordinates.
/// </summary>
public static class Layout
{
    public const float RadialStepDeg = 30f;
    public const float RadialTopDeg = -90f; // 12 o'clock in screen space (y down)

    /// Relic <paramref name="index"/> of <paramref name="count"/> fanned around the top,
    /// balanced so the set stays centred on 12 o'clock.
    public static (float x, float y) RadialCenterOffset(
        int index, int count, float radius, float stepDeg = RadialStepDeg, float topDeg = RadialTopDeg)
    {
        float offsetDeg = (index - (count - 1) / 2f) * stepDeg;
        float a = (topDeg + offsetDeg) * MathF.PI / 180f;
        return (MathF.Cos(a) * radius, MathF.Sin(a) * radius);
    }

    /// Blue counter <paramref name="index"/> in a row to the LEFT of the cluster centre.
    public static (float x, float y) RowCenterOffset(int index, float gap) => (-gap * (index + 1), 0f);
}
