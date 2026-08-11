using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.Combat; // NEnergyCounter

namespace PokaYokeSpire.Features;

/// <summary>
/// FEATURE 4 — recenter the energy counter to the middle of the screen, just above the
/// hand. Also caches the live counter instance so other features (radial relics, blue
/// counters) can position relative to it.
///
/// A _Process POSTFIX sets GlobalPosition each frame — the counter free-positions via
/// AnimIn/AnimOut tweens and its own _Process only spins the orb, so this holds.
/// </summary>
[HarmonyPatch(typeof(NEnergyCounter), "_Process")]
internal static class EnergyCounterFeature
{
    /// The energy counter currently in combat (null outside combat). Used by the
    /// radial-relic and blue-counter features to anchor around it.
    internal static NEnergyCounter? Instance;

    private static Vector2? _manualPos;   // set when the counter is ctrl-dragged
    private static double _lastHeight = double.NaN;

    private static void Postfix(NEnergyCounter __instance)
    {
        Instance = __instance;

        // Overlays parented to the energy counter: keep them (re)built + positioned.
        RadialRelicsManager.UpdateAll(__instance);
        BlueCounterManager.UpdateAll(__instance);

        if (!Config.CenterEnergyCounter) return;

        // Moving the height slider cancels any dragged position.
        if (Config.EnergyCounterHeight != _lastHeight) { _manualPos = null; _lastHeight = Config.EnergyCounterHeight; }

        // While being ctrl-dragged, let the drag own the position (and remember it).
        if (ReferenceEquals(DragHandler.Dragged, __instance)) { _manualPos = __instance.GlobalPosition; return; }
        // Dropped somewhere by a drag -> stay put until the slider moves.
        if (_manualPos is { } mp) { __instance.GlobalPosition = mp; return; }

        Vector2 viewport = __instance.GetViewportRect().Size;
        Vector2 size = __instance.Size;
        __instance.GlobalPosition = new Vector2(
            viewport.X * 0.5f - size.X * 0.5f,
            viewport.Y * (float)Config.EnergyCounterHeight - size.Y * 0.5f);
    }
}
