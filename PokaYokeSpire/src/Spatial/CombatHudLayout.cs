using System;
using System.Collections.Generic;
using System.Linq;

namespace PokaYokeSpire.Spatial;

/// <summary>
/// Pure combat-HUD geometry, so the "does the (re-centred) energy meter occlude anything?" question
/// can be swept over every encounter as data — no Godot, no running game (CLAUDE.md § "Spatial
/// placement"). Enemy rectangles are an INPUT (produced per-encounter by <see cref="EnemyFormation"/>
/// from the real slot/enemy-count data), so this module stays independent of how enemies are placed.
/// </summary>
public static class CombatHudLayout
{
    /// The energy meter under our CenterEnergyCounter feature: horizontally centred, its vertical
    /// centre at <paramref name="heightFrac"/> of the viewport height (EnergyCounterFeature.cs).
    public static UiRect EnergyMeterRect(float vpW, float vpH, float heightFrac, float meterW, float meterH) =>
        new("energy", vpW * 0.5f - meterW / 2f, vpH * heightFrac - meterH / 2f, meterW, meterH);

    /// Ids of the elements the energy meter overlaps by more than <paramref name="minOverlapFrac"/>
    /// of the smaller rect (0 == any overlap at all). This is the per-encounter occlusion probe.
    public static List<string> EnergyOcclusions(UiRect energy, IReadOnlyList<UiRect> others, float minOverlapFrac = 0f) =>
        others.Where(o => SpatialGraph.OverlapFraction(energy, o) > minOverlapFrac)
              .Select(o => o.Id).ToList();

    /// Clip a rect to the visible viewport (an element mostly below the screen edge occludes less).
    /// Returns the on-screen portion; zero-area if fully off-screen.
    public static UiRect ClipToViewport(UiRect r, float vpW, float vpH)
    {
        float l = MathF.Max(0f, r.Left), t = MathF.Max(0f, r.Top);
        float rgt = MathF.Min(vpW, r.Right), b = MathF.Min(vpH, r.Bottom);
        return new UiRect(r.Id, l, t, MathF.Max(0f, rgt - l), MathF.Max(0f, b - t));
    }
}
