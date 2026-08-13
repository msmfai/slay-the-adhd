using Godot;

namespace PokaYokeSpire;

/// <summary>
/// Hardening: every decorative overlay this mod adds MUST be transparent to the mouse, or it can sit
/// on top of a card / button / enemy and swallow the click (the class of regression that broke the
/// end-turn button and card-picking). Call <see cref="Passthrough"/> on any overlay subtree after
/// building it — it forces MouseFilter=Ignore on the node and ALL descendants (including nodes cloned
/// from the game, whose children default to capturing the mouse).
/// </summary>
public static class UiSafety
{
    public static void Passthrough(Node n)
    {
        if (n == null || !GodotObject.IsInstanceValid(n)) return;
        if (n is Control c) c.MouseFilter = Control.MouseFilterEnum.Ignore;
        foreach (var child in n.GetChildren()) Passthrough(child);
    }
}
