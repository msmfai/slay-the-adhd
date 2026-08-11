using System;
using System.Collections;
using System.Collections.Generic;
using Godot;
using HarmonyLib;                           // Traverse
using MegaCrit.Sts2.Core.Localization;      // LocString, LocTable, LocManager
using MegaCrit.Sts2.Core.Logging;           // Log
using MegaCrit.Sts2.Core.Nodes.CommonUi;    // NModalContainer
using MegaCrit.Sts2.Core.Nodes.Multiplayer; // NGenericPopup

namespace PokaYokeSpire;

/// <summary>
/// Shows dialogs through the game's REAL modal system: NGenericPopup added to
/// NModalContainer (exactly how the game shows its own confirm dialogs). The previous
/// hand-rolled CanvasLayer approach never sized/centered the popup, so it rendered
/// invisibly — which meant the blocking guards trapped the player with no way to confirm
/// (e.g. couldn't end turn with energy left). This makes the popup actually visible and
/// clickable.
///
/// - ShowConfirm: Yes/No (end-turn-with-energy).
/// - ShowNotice:  single button (potions, check-your-deck).
///
/// Fail-safe: if the modal can't be shown (another modal open, API error), we proceed
/// (call onYes/onOk) so a guard can NEVER trap the player.
/// </summary>
public static class PopupHelper
{
    private static LocString Confirm => new LocString("main_menu_ui", "GENERIC_POPUP.confirm");
    private static LocString Cancel  => new LocString("main_menu_ui", "GENERIC_POPUP.cancel");

    // NGenericPopup renders LocStrings, and LocTable.GetRawText THROWS on a missing key
    // (empty table or missing key both blow up -> "you should never see this"). So we can't
    // pass literal text as a fake key. Instead we register a private loc table at runtime
    // (LocTable has a public ctor; LocManager._tables is a mutable dict) and stash each
    // string in it, returning a LocString that points at a REAL entry.
    private const string Table = "PokaYokeSpire";
    private static Dictionary<string, string>? _entries;

    private static LocString Lit(string text)
    {
        EnsureTable();
        string key = "k" + (uint)text.GetHashCode();
        if (_entries != null) _entries[key] = text; // shared with the LocTable's translations
        return new LocString(Table, key);
    }

    private static void EnsureTable()
    {
        if (_entries != null) return;
        _entries = new Dictionary<string, string>();
        try
        {
            var table = new LocTable(Table, _entries);
            var tables = Traverse.Create(LocManager.Instance).Field("_tables").GetValue<IDictionary>();
            tables[Table] = table; // GetTable(Table) now finds our entries
        }
        catch (Exception e)
        {
            Log.Warn($"[Poka-Yoke] could not register loc table ({e.GetType().Name}): text may be blank.");
        }
    }

    public static void ShowConfirm(string title, string body, Action onYes, Action? onNo = null)
        => Show(title, body, hasNo: true, onYes, onNo);

    public static void ShowNotice(string title, string body, Action? onOk = null)
        => Show(title, body, hasNo: false, onOk, null);

    private static async void Show(string title, string body, bool hasNo, Action? onYes, Action? onNo)
    {
        try
        {
            NModalContainer? modal = NModalContainer.Instance;
            NGenericPopup? popup = NGenericPopup.Create();
            if (modal == null || popup == null) { onYes?.Invoke(); return; }

            modal.Add(popup);
            // If a modal was already open, Add() no-ops — don't await forever, just proceed.
            if (!GodotObject.IsInstanceValid(popup) || !popup.IsInsideTree())
            {
                onYes?.Invoke();
                return;
            }

            bool yes = await popup.WaitForConfirmation(Lit(body), Lit(title), hasNo ? Cancel : null, Confirm);
            if (yes) onYes?.Invoke(); else onNo?.Invoke();
        }
        catch (Exception e)
        {
            Log.Warn($"[Poka-Yoke] popup failed ({e.GetType().Name}: {e.Message}); proceeding.");
            onYes?.Invoke(); // never trap the player
        }
    }
}
