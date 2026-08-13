using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.Combat; // NEnergyCounter
using PokaYokeSpire.Core;
using PokaYokeSpire.Spatial;           // CombatHudLower

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
    private static float _lastHeight = float.NaN;

    private static bool _loggedActive;
    private static bool _subscribedLive;

    /// The hand only re-lays-out on demand (draw/play/hover), so a live tunable edit updates the stored
    /// HandOffset but nothing re-applies it — the hand appears frozen. Force a layout refresh on reload.
    private static void RefreshHandLayout()
    {
        try
        {
            var hand = NPlayerHand.Instance;
            bool ok = hand != null && GodotObject.IsInstanceValid(hand);
            if (ok) Traverse.Create(hand).Method("RefreshLayout").GetValue();
            if (DebugLog.Enabled)
                DebugLog.Debug($"hand refresh: handFound={ok} offset=({HudLowerState.HandOffsetX:0.#},{HudLowerState.HandOffsetY:0.#})");
        }
        catch (System.Exception e) { DebugLog.Error("RefreshHandLayout", e); }
    }

    private static void Postfix(NEnergyCounter __instance)
        => Feature.Run("energy-counter", () => true, () => Body(__instance));

    // Runs every frame via the guarded runner (fail-open + auto-disable): an exception here can never
    // break the energy counter's own _Process.
    private static void Body(NEnergyCounter __instance)
    {
        {
            Instance = __instance;
            LiveTuning.Ensure(__instance);   // spawn the F9 tuning panel here too — independent of any
                                             // feature toggle, so F9 works whenever the counter is alive
            if (!_loggedActive)
            {
                _loggedActive = true;
                MegaCrit.Sts2.Core.Logging.Log.Info("[Poka-Yoke] feature4 ACTIVE: energy-counter _Process hook running in combat");
            }

            // Measure the energy-number text height and publish the hand RAISE for HandLowerFeature
            // (negative Y = up).
            float textH = MeasureTextHeight(__instance);
            HudLowerState.HandOffsetY = (Config.RaiseCombatHud ? -CombatHudLower.HandRaise(textH, Tunables.HandRaiseFrac) : 0f) + Tunables.HudGroupOffsetY;
            HudLowerState.HandOffsetX = Tunables.HudGroupOffsetX;
            if (!_subscribedLive) { _subscribedLive = true; LiveTuning.Reloaded += RefreshHandLayout; }

            // Overlays parented to the energy counter: keep them (re)built + positioned.
            RadialRelicsManager.UpdateAll(__instance);
            BlueCounterManager.UpdateAll(__instance);

            if (!Config.CenterEnergyCounter) return;

            // Moving the height slider cancels any dragged position.
            if (Tunables.EnergyHeightFrac != _lastHeight) { _manualPos = null; _lastHeight = Tunables.EnergyHeightFrac; }

            // While being ctrl-dragged, let the drag own the position (and remember it).
            if (ReferenceEquals(DragHandler.Dragged, __instance)) { _manualPos = __instance.GlobalPosition; return; }
            // Dropped somewhere by a drag -> stay put until the slider moves.
            if (_manualPos is { } mp) { __instance.GlobalPosition = mp; return; }

            Vector2 viewport = __instance.GetViewportRect().Size;
            Vector2 size = __instance.Size;
            float raise = Config.RaiseCombatHud ? CombatHudLower.CounterRaise(textH, Tunables.HandRaiseFrac, Tunables.CounterRaiseFracOfHand) : 0f;
            __instance.GlobalPosition = new Vector2(
                viewport.X * 0.5f - size.X * 0.5f + Tunables.HudGroupOffsetX,
                viewport.Y * Tunables.EnergyHeightFrac - size.Y * 0.5f - raise + Tunables.HudGroupOffsetY);
        }
    }

    /// Height of the energy number ("Label" MegaLabel child), clamped to a sane range; falls back if
    /// it isn't laid out yet. This is the unit all the HUD-lowering offsets are measured in.
    private static float MeasureTextHeight(NEnergyCounter counter)
    {
        try
        {
            var label = counter.GetNodeOrNull<Control>("Label");
            float h = (label != null && label.Size.Y > 1f) ? label.Size.Y : 48f;
            return Mathf.Clamp(h, 20f, 120f);
        }
        catch { return 48f; }
    }

}
