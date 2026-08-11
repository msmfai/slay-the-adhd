using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;        // NCardHolder
using MegaCrit.Sts2.Core.Nodes.Screens;               // NDeckViewScreen
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection; // NCardRewardSelectionScreen

namespace PokaYokeSpire.Guards;

/// <summary>
/// GUARD 3 — a speed bump, not a block. If you take a card from a reward WITHOUT having
/// opened your deck, a "Be sure to check your deck" notice pops up; clicking OK then
/// takes the card. If you did open your deck first, it just takes the card. The point:
/// taking a card should cost at least as much friction as glancing at your deck. Can
/// never soft-lock — OK always proceeds.
/// </summary>
internal static class DeckCheckState
{
    internal static bool DeckViewedSinceRewardShown;
    internal static object? LastRewardScreen;
}

[HarmonyPatch(typeof(NCardRewardSelectionScreen), "RefreshOptions")]
internal static class DeckCheck_RewardShown
{
    private static void Postfix(NCardRewardSelectionScreen __instance)
    {
        // Only reset for a genuinely new reward screen (not a re-refresh of the same one).
        if (!ReferenceEquals(__instance, DeckCheckState.LastRewardScreen))
        {
            DeckCheckState.LastRewardScreen = __instance;
            DeckCheckState.DeckViewedSinceRewardShown = false;
        }
    }
}

[HarmonyPatch(typeof(NDeckViewScreen), "ShowScreen")]
internal static class DeckCheck_DeckOpened
{
    private static void Postfix() => DeckCheckState.DeckViewedSinceRewardShown = true;
}

[HarmonyPatch(typeof(NCardRewardSelectionScreen), "SelectCard")]
internal static class DeckCheck_SelectCard
{
    private static bool _bypass;
    private static readonly MethodInfo _selectCard =
        AccessTools.Method(typeof(NCardRewardSelectionScreen), "SelectCard");

    private static bool Prefix(NCardRewardSelectionScreen __instance, NCardHolder cardHolder)
    {
        if (!Config.GuardCardRewardDeckCheck) return true;
        if (_bypass) { _bypass = false; return true; }              // re-entry after OK
        if (DeckCheckState.DeckViewedSinceRewardShown) return true; // already looked -> just take it

        // Haven't looked: nudge, then take the card when they click OK (never blocks).
        PopupHelper.ShowNotice(
            title: "Check your deck",
            body: "Be sure to check your deck.",
            onOk: () =>
            {
                _bypass = true;
                _selectCard.Invoke(__instance, new object[] { cardHolder });
            });
        return false; // hold only this click; OK re-invokes and takes the card
    }
}
