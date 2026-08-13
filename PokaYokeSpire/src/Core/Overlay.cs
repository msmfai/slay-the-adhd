using System;
using Godot;

namespace PokaYokeSpire.Core;

/// <summary>
/// The ONE sanctioned way to attach mod UI to the game's tree. Everything it returns is, by
/// construction:
///   • mouse-transparent (invariant 1) — <see cref="UiSafety.Passthrough"/> is applied to the whole
///     subtree, so a mod overlay can never swallow a click meant for a card / button / enemy;
///   • idempotent (invariant 6) — a second Attach with the same name returns the existing node
///     instead of building a duplicate;
///   • self-identifying — the node is named, so features find/remove it deterministically.
/// Features never call <c>parent.AddChild(rawControl)</c> themselves (a meta-test enforces this); a
/// mod overlay that captures input or duplicates is therefore not expressible.
/// </summary>
public static class Overlay
{
    /// Attach (once) a named overlay built by <paramref name="factory"/> under <paramref name="parent"/>.
    /// Returns the live node (existing or new), or null if it couldn't be attached. Never throws.
    public static T? Attach<T>(Node parent, string name, Func<T> factory) where T : Control
    {
        try
        {
            if (parent == null || !GodotObject.IsInstanceValid(parent)) return null;
            if (parent.GetNodeOrNull(name) is T existing) return existing;   // idempotent
            if (parent.GetNodeOrNull(name) != null) return null;             // name taken by something else

            var node = factory();
            if (node == null) return null;
            node.Name = name;
            node.MouseFilter = Control.MouseFilterEnum.Ignore;
            parent.AddChild(node);
            UiSafety.Passthrough(node);   // input-safe by construction, incl. cloned game subtrees
            DebugLog.Debug($"Overlay.Attach('{name}') under {parent.GetType().Name}");
            return node;
        }
        catch (Exception e) { DebugLog.Error($"Overlay.Attach('{name}')", e); return null; }
    }

    /// Re-apply the mouse-transparency invariant after a feature has mutated an overlay's children
    /// (e.g. rebuilt content). Cheap; safe to call every update.
    public static void Reseal(Node overlay) => UiSafety.Passthrough(overlay);

    /// Give an overlay a hover tooltip WITHOUT letting it eat clicks. A tooltip needs the node to
    /// receive mouse motion, so the ROOT uses MouseFilter.Pass — it gets hover (tooltip shows) but the
    /// click still propagates to whatever is behind it and is never consumed (unlike Stop). Children
    /// stay Ignore. So an overlay can be informative and still input-safe by construction (invariant 1).
    public static void SetTooltip(Control node, string text)
    {
        try
        {
            if (node == null || !GodotObject.IsInstanceValid(node)) return;
            node.TooltipText = text;
            node.MouseFilter = Control.MouseFilterEnum.Pass;   // hover-only; never consumes a click
        }
        catch (Exception e) { DebugLog.Error("Overlay.SetTooltip", e); }
    }

    public static void Remove(Node parent, string name)
    {
        try
        {
            if (parent != null && GodotObject.IsInstanceValid(parent) && parent.GetNodeOrNull(name) is Node n)
                n.QueueFree();
        }
        catch { }
    }
}
