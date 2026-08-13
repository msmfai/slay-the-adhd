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

    /// Bind a control to show the game hover tip on hover. The text is read from <paramref name="text"/>
    /// EACH hover (so live edits show immediately, no rebuild): its first line is the title (registered in
    /// the loc table), the rest is the body. Placed at <c>node.GlobalPosition + <paramref name="offset"/></c>
    /// — exactly how NEnergyCounter positions its own tip. The node is set to STOP so it consumes its own
    /// hover — otherwise the hover also propagates to its parent (the energy counter) and shows its tip too.
    public static void Bind(Control node, string titleKey, Func<string> text, Vector2 offset)
    {
        try
        {
            if (node == null || !GodotObject.IsInstanceValid(node)) return;
            node.MouseFilter = Control.MouseFilterEnum.Stop;   // consume our own hover; don't leak it to the counter
            node.MouseEntered += () =>
            {
                try
                {
                    var (title, body) = Split(text?.Invoke() ?? "");
                    _titles[titleKey] = title;
                    EnsureLoc();
                    var tip = new HoverTip(new LocString(Table, titleKey), body);
                    var set = NHoverTipSet.CreateAndShow(node, tip);
                    if (set != null && GodotObject.IsInstanceValid(set))
                        set.GlobalPosition = node.GlobalPosition + offset;
                    if (DebugLog.Enabled) DebugLog.Debug($"hovertip show: {titleKey} at {(GodotObject.IsInstanceValid(set) ? set.GlobalPosition : Vector2.Zero)}");
                }
                catch (Exception e) { DebugLog.Error("GameTooltip.enter", e); }
            };
            node.MouseExited += () => { try { NHoverTipSet.Remove(node); } catch { } };
        }
        catch (Exception e) { DebugLog.Error("GameTooltip.Bind", e); }
    }

    /// First line = title, the rest = body.
    private static (string title, string body) Split(string s)
    {
        int nl = s.IndexOf('\n');
        return nl < 0 ? (s, "") : (s[..nl], s[(nl + 1)..]);
    }
}
