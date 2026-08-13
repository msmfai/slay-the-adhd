using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Helpers;   // HandPosHelper

namespace PokaYokeSpire.Features;

/// <summary>
/// Shared state for the HUD raise. <see cref="HandOffsetY"/> is the Y offset applied to every RESTING
/// hand card (NEGATIVE = raised up); it's written each frame by <see cref="EnergyCounterFeature"/>
/// (from the measured energy-text height) and read by the <see cref="HandLowerFeature"/> patch.
/// Both run on the game thread, so a plain field is fine.
/// </summary>
internal static class HudLowerState
{
    internal static float HandOffsetY;
}

/// <summary>
/// Shifts the resting hand by <see cref="HudLowerState.HandOffsetY"/> px (negative = raised) by
/// post-processing the game's own per-card layout function (HandPosHelper.GetPosition). The HOVERED
/// card's Y is recomputed separately inside NPlayerHand.RefreshLayout (it does not use GetPosition's
/// Y), so hovering is unaffected. Fail-open.
/// </summary>
[HarmonyPatch(typeof(HandPosHelper), "GetPosition")]
internal static class HandLowerFeature
{
    private static void Postfix(ref Vector2 __result)
    {
        try
        {
            float dy = HudLowerState.HandOffsetY;
            if (dy != 0f) __result = new Vector2(__result.X, __result.Y + dy);
        }
        catch { /* fail-open: never disturb hand layout */ }
    }
}
