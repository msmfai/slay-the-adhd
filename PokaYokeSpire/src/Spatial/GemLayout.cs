using System.Collections.Generic;

namespace PokaYokeSpire.Spatial;

/// <summary>
/// Pure placement math for the two combat gems that flank the energy counter (blue incoming / red
/// offense). LLMs are blind, so this is DATA + LINTS, not eyeballed pixels (CLAUDE.md § Spatial): the
/// feature derives pivot/offset/scale from here, and the SAME function is lint-tested, so a mis-placed
/// gem — art off-screen, a gem overlapping the counter or the other gem, asymmetric flanking — fails
/// the build instead of shipping.
///
/// Coordinate model (counter-local; counter origin = top-left; the orb art fills [0,size] so its centre
/// is size/2). A gem is a child Control at Position=(±OffX,0), PivotOffset=(half,half), Scale=Scale.
/// A point at gem-local L maps to counter-local  Position + Pivot + (L-Pivot)*Scale, so a gem's orb
/// centre (L = Pivot) lands at (±OffX + half, half) — the SAME height as the counter's orb centre
/// (half,half), offset horizontally by exactly OffX. This is the geometry the real bug got wrong:
/// scaling about the origin (0,0) instead of the orb centre put the gems up-and-left; a zero-size gem
/// collapsed the anchored art to nothing.
/// </summary>
public static class GemLayout
{
    public const float Scale = 2f / 3f;   // gems are ⅔ the counter's size (2/3 radius)
    public const float Gap = 14f;         // clear px between the counter's orb edge and a gem's orb edge

    public readonly record struct Plan(float Pivot, float OffX, float Scale);

    /// Pivot (orb centre), horizontal offset, and scale for a gem, from the square counter's side length.
    public static Plan For(float counterSize)
    {
        float half = counterSize * 0.5f;
        float offX = half + Gap + half * Scale;   // counterHalf + gap + scaled gemHalf  ⇒  edge clearance == Gap
        return new Plan(half, offX, Scale);
    }

    public static float CounterOrbRadius(float counterSize) => counterSize * 0.5f;
    public static float GemOrbRadius(float counterSize) => counterSize * 0.5f * Scale;

    /// The three orb rectangles (counter, left gem, right gem) in counter-local space — the hypergraph
    /// the spatial lints run over. Each orb is a square centred on its computed orb centre.
    public static List<UiRect> OrbRects(float counterSize)
    {
        float half = counterSize * 0.5f;
        var plan = For(counterSize);
        float gemR = GemOrbRadius(counterSize);
        return new List<UiRect>
        {
            Square("counter", half, half, half),
            Square("gemL", -plan.OffX + half, half, gemR),
            Square("gemR", plan.OffX + half, half, gemR),
        };
    }

    private static UiRect Square(string id, float cx, float cy, float radius) =>
        new(id, cx - radius, cy - radius, radius * 2f, radius * 2f);
}
