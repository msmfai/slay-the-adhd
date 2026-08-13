using System;
using HarmonyLib;
using MegaCrit.Sts2.Core.Localization;   // LocString
using MegaCrit.Sts2.Core.Models;         // ActModel
using MegaCrit.Sts2.Core.Nodes;          // NActBanner
using MegaCrit.Sts2.Core.Runs;           // RunManager, RunState
using PokaYokeSpire.Core;

namespace PokaYokeSpire.Features;

/// <summary>
/// ACT-START NOTIFICATION — when the act-start banner appears, pop a notice naming the act
/// (Underdocks / Overgrowth / ...) and the boss you'll fight this act, so you know what
/// you're heading into before you pick your Neow relic.
///
/// Hooks NActBanner._Ready (fires at the start of each act; note it re-invokes itself during
/// its vfx, so we gate to once per act by the ActModel reference — a new run's acts are new
/// instances, so it re-shows next run). Fail-open.
/// </summary>
[HarmonyPatch(typeof(NActBanner), "_Ready")]
internal static class ActStartNotificationFeature
{
    private static ActModel? _shownFor;

    private static void Postfix() => Feature.Run("act-start-notification", () => Config.ShowActStartNotification, () =>
    {
        try
        {

            var t = Traverse.Create(RunManager.Instance);
            var state = t.Property("State").GetValue<RunState>() ?? t.Field("State").GetValue<RunState>();
            var act = state?.Act;
            if (act == null || ReferenceEquals(act, _shownFor)) return;   // once per act
            _shownFor = act;

            string actName = Safe(() => act.Title.GetFormattedText(), "This Act");
            string bossName = Safe(() => act.BossEncounter?.Title.GetFormattedText(), "Unknown");

            MegaCrit.Sts2.Core.Logging.Log.Info($"[Poka-Yoke] feature10 ACT-START: {actName} — boss {bossName}");
            PopupHelper.ShowNotice(title: actName, body: $"Boss ahead: {bossName}");
        }
        catch (Exception e) { MegaCrit.Sts2.Core.Logging.Log.Info($"[Poka-Yoke] act-start error: {e.Message}"); }
    });

    private static string Safe(Func<string?> f, string fallback)
    {
        try { var s = f(); return string.IsNullOrWhiteSpace(s) ? fallback : s!; } catch { return fallback; }
    }
}
