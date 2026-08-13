using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes;   // NSceneContainer
using PokaYokeSpire.Core;

namespace PokaYokeSpire.Features;

/// <summary>
/// Makes the F9 tuning panel available on EVERY screen, not just combat. Every top-level scene swap and
/// every in-run room swap flows through <c>NSceneContainer.SetCurrentScene</c>, so a postfix here
/// idempotently spawns the panel (parented to the tree root, so it persists across all later swaps and
/// overlay pushes). Gated + fail-open via the runner; <see cref="LiveTuning.Ensure"/> is a no-op unless
/// Config.LiveTuning is on and is idempotent, so re-asserting on every swap is free.
/// </summary>
[HarmonyPatch(typeof(NSceneContainer), "SetCurrentScene")]
internal static class LiveTuningBootstrapFeature
{
    private static void Postfix(NSceneContainer __instance)
        => Feature.Run("live-tuning-bootstrap", () => true, () => LiveTuning.Ensure(__instance));
}
