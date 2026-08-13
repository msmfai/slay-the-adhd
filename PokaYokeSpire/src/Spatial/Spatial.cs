using System;
using System.Collections.Generic;
using System.Linq;

namespace PokaYokeSpire.Spatial;

/// <summary>
/// An axis-aligned rectangle for a single UI element, tagged with an id. Pure value type —
/// no Godot dependency — so layouts built from these are directly unit-testable. Coordinates
/// are screen-space, y-down (top-left origin), matching Godot's Control coordinates.
/// </summary>
public readonly record struct UiRect(string Id, float X, float Y, float W, float H)
{
    public float Left => X;
    public float Right => X + W;
    public float Top => Y;
    public float Bottom => Y + H;
    public float CenterX => X + W / 2f;
    public float CenterY => Y + H / 2f;
    public float Area => MathF.Max(0f, W) * MathF.Max(0f, H);
}

/// <summary>
/// The spatial hypergraph toolkit. LLMs can't see the screen, so layout correctness is
/// expressed as features over the rectangles (nodes) and their relationships (hyperedges):
/// PAIRWISE (overlap, gaps, visible sliver), TRIPLET (collinear / ordered / evenly spaced),
/// QUAD (mirror-symmetric fan). Lints turn those features into pass/fail predicates that both
/// the tests and the runtime feature enforce. See CLAUDE.md § "Spatial placement".
/// </summary>
public static class SpatialGraph
{
    // ───────────────────────── pairwise (2-element) features ─────────────────────────

    /// Do the two rectangles overlap at all (open intersection — touching edges don't count)?
    public static bool Intersects(UiRect a, UiRect b) =>
        a.Left < b.Right && b.Left < a.Right && a.Top < b.Bottom && b.Top < a.Bottom;

    /// Area of the overlap region (0 if disjoint).
    public static float OverlapArea(UiRect a, UiRect b)
    {
        float w = MathF.Min(a.Right, b.Right) - MathF.Max(a.Left, b.Left);
        float h = MathF.Min(a.Bottom, b.Bottom) - MathF.Max(a.Top, b.Top);
        return (w > 0f && h > 0f) ? w * h : 0f;
    }

    /// Overlap as a fraction of the SMALLER rectangle's area (1.0 == smaller fully covered).
    public static float OverlapFraction(UiRect a, UiRect b)
    {
        float smaller = MathF.Min(a.Area, b.Area);
        return smaller <= 0f ? 0f : OverlapArea(a, b) / smaller;
    }

    /// Signed horizontal gap between the left rect's right edge and the right rect's left edge.
    /// Positive == clear space between them; negative == they overlap horizontally.
    public static float HorizontalGap(UiRect a, UiRect b)
    {
        var (l, r) = a.CenterX <= b.CenterX ? (a, b) : (b, a);
        return r.Left - l.Right;
    }

    /// Signed vertical gap (positive == clear space; negative == vertical overlap).
    public static float VerticalGap(UiRect a, UiRect b)
    {
        var (t, btm) = a.CenterY <= b.CenterY ? (a, b) : (b, a);
        return btm.Top - t.Bottom;
    }

    /// The visible sliver of the earlier card in a left-to-right fan: how far the later card's
    /// left edge sits past the earlier card's left edge. This is the width the player can still
    /// see + click of an overlapped card, so it's the quantity the min-spacing lint guards.
    public static float VisibleStep(UiRect earlier, UiRect later) => later.Left - earlier.Left;

    // ───────────────────────── triplet (3-element) features ──────────────────────────

    public static bool Collinear(UiRect a, UiRect b, UiRect c, float eps = 0.5f) =>
        MathF.Abs(a.CenterY - b.CenterY) <= eps && MathF.Abs(b.CenterY - c.CenterY) <= eps;

    public static bool OrderedX(UiRect a, UiRect b, UiRect c) =>
        a.CenterX < b.CenterX && b.CenterX < c.CenterX;

    public static bool EvenlySpacedX(UiRect a, UiRect b, UiRect c, float eps = 0.5f) =>
        MathF.Abs((b.CenterX - a.CenterX) - (c.CenterX - b.CenterX)) <= eps;

    // ───────────────────────── quad (4-element) feature ──────────────────────────────

    /// a↔d and b↔c are horizontal mirrors about `axisX` (a balanced, centred fan).
    public static bool MirrorSymmetricX(UiRect a, UiRect b, UiRect c, UiRect d, float axisX, float eps = 0.5f) =>
        MathF.Abs((a.CenterX - axisX) + (d.CenterX - axisX)) <= eps &&
        MathF.Abs((b.CenterX - axisX) + (c.CenterX - axisX)) <= eps;

    // ───────────────────────── lints (predicates over a set) ─────────────────────────

    /// Pairs whose overlap exceeds <paramref name="maxOverlapFrac"/> of the smaller rect.
    /// Intentional fans pass a nonzero budget; groups that must never occlude pass 0.
    public static List<(string, string)> OcclusionViolations(IReadOnlyList<UiRect> rects, float maxOverlapFrac)
    {
        var bad = new List<(string, string)>();
        for (int i = 0; i < rects.Count; i++)
            for (int j = i + 1; j < rects.Count; j++)
                if (OverlapFraction(rects[i], rects[j]) > maxOverlapFrac)
                    bad.Add((rects[i].Id, rects[j].Id));
        return bad;
    }

    /// Adjacent-by-x pairs whose visible sliver is below <paramref name="minStep"/> — i.e. a
    /// card so buried behind its neighbour that too little of it can be seen/clicked.
    public static List<(string, string)> MinSpacingViolations(IReadOnlyList<UiRect> row, float minStep)
    {
        var sorted = row.OrderBy(r => r.CenterX).ToList();
        var bad = new List<(string, string)>();
        for (int i = 0; i + 1 < sorted.Count; i++)
            if (VisibleStep(sorted[i], sorted[i + 1]) < minStep)
                bad.Add((sorted[i].Id, sorted[i + 1].Id));
        return bad;
    }

    /// Ids of rectangles that poke outside the [0,vpW]×[0,vpH] viewport (with a small slack).
    public static List<string> OutOfBounds(IReadOnlyList<UiRect> rects, float vpW, float vpH, float slack = 0.5f)
    {
        var bad = new List<string>();
        foreach (var r in rects)
            if (r.Left < -slack || r.Top < -slack || r.Right > vpW + slack || r.Bottom > vpH + slack)
                bad.Add(r.Id);
        return bad;
    }

    /// Do two element GROUPS (e.g. the reward choices vs the deck fan) collide vertically?
    /// Returns true when they are clear of each other by at least <paramref name="minGap"/>.
    public static bool GroupsVerticallyClear(UiRect groupA, UiRect groupB, float minGap = 0f) =>
        VerticalGap(groupA, groupB) >= minGap;

    /// Bounding box of a set of rectangles (useful for treating a group as one node).
    public static UiRect BoundingBox(string id, IReadOnlyList<UiRect> rects)
    {
        if (rects.Count == 0) return new UiRect(id, 0, 0, 0, 0);
        float l = rects.Min(r => r.Left), t = rects.Min(r => r.Top);
        float rgt = rects.Max(r => r.Right), b = rects.Max(r => r.Bottom);
        return new UiRect(id, l, t, rgt - l, b - t);
    }
}
