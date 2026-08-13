using System;
using System.Collections.Generic;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.HoverTips;             // HoverTip
using MegaCrit.Sts2.Core.Localization;          // LocManager, LocString, LocTable
using MegaCrit.Sts2.Core.Nodes.HoverTips;       // NHoverTipSet

namespace PokaYokeSpire.Core;

/// <summary>
/// Show tooltips through the GAME'S OWN hover-tip system (the one <c>NCommonTooltipsHoverTip</c> uses),
/// not a bespoke panel: on hover we build a <see cref="HoverTip"/> (title from a mod loc table, body as
/// literal text) and call <see cref="NHoverTipSet.CreateAndShow"/>; on unhover, <see cref="NHoverTipSet.Remove"/>.
///
/// The game resolves a HoverTip's title from a <see cref="LocManager"/> loc table, so — exactly as mods
/// add localized strings — we register/merge a "pokayoke" table with our title entries. Body text is
/// passed literally via <c>HoverTip(LocString title, string description)</c>, so it can stay dynamic
/// (our live-tunable tooltip text). Fully fail-open.
/// </summary>
public static class GameTooltip
{
    private const string Table = "pokayoke";
    private static readonly Dictionary<string, string> _titles = new();

    /// Register/replace the title text for <paramref name="key"/> in the mod loc table.
    public static void SetTitle(string key, string title)
    {
        _titles[key] = title ?? "";
        EnsureLoc();
    }

    /// Ensure our loc table exists and holds the current titles (re-runs cheaply; also recovers if the
    /// game rebuilt its tables, e.g. on a language change).
    private static void EnsureLoc()
    {
        try
        {
            var lm = LocManager.Instance;
            if (lm == null) return;
            var tables = Traverse.Create(lm).Field("_tables").GetValue<Dictionary<string, LocTable>>();
            if (tables == null) return;
            if (tables.TryGetValue(Table, out var t)) t.MergeWith(new Dictionary<string, string>(_titles));
            else tables[Table] = new LocTable(Table, new Dictionary<string, string>(_titles));
        }
        catch (Exception e) { DebugLog.Error("GameTooltip.EnsureLoc", e); }
    }

    /// Bind a control to show the game hover tip (title = loc <paramref name="titleKey"/>, body = literal
    /// <paramref name="body"/>) on hover. Pass mouse filter so hover fires but clicks pass through.
    public static void Bind(Control node, string titleKey, string body)
    {
        try
        {
            if (node == null || !GodotObject.IsInstanceValid(node)) return;
            node.MouseFilter = Control.MouseFilterEnum.Pass;
            node.MouseEntered += () =>
            {
                try
                {
                    EnsureLoc();
                    var tip = new HoverTip(new LocString(Table, titleKey), body ?? "");
                    NHoverTipSet.CreateAndShow(node, tip);
                    if (DebugLog.Enabled) DebugLog.Debug($"hovertip show: {titleKey}");
                }
                catch (Exception e) { DebugLog.Error("GameTooltip.enter", e); }
            };
            node.MouseExited += () => { try { NHoverTipSet.Remove(node); } catch { } };
        }
        catch (Exception e) { DebugLog.Error("GameTooltip.Bind", e); }
    }
}
