using System;
using System.Collections.Generic;
using Godot;
using MegaCrit.Sts2.Core.Models;              // CardModel
using MegaCrit.Sts2.Core.Nodes.Cards;         // NCard
using MegaCrit.Sts2.Core.Nodes.Cards.Holders; // NGridCardHolder

namespace PokaYokeSpire.Core;

/// <summary>
/// The ONE sanctioned way to display game cards. It reproduces the game's own path exactly — an
/// <see cref="NCard"/> wrapped in an <see cref="NGridCardHolder"/> — and, crucially, attaches them on
/// a DEFERRED call so each card's <c>_Ready → Reload</c> (the thing that actually renders it) runs on
/// a settled tree. Adding cards during a screen's own <c>_Ready</c> is exactly what left them as
/// "broken card"; routing every card display through here makes that state unrepresentable
/// (invariant 4). The row is a mouse-transparent, idempotently-named overlay (invariants 1, 6).
/// </summary>
public static class CardDisplay
{
    /// Build holders for <paramref name="models"/> under a named row on <paramref name="parent"/>,
    /// deferred, then invoke <paramref name="onBuilt"/> so the caller can lay them out. Never throws.
    public static void Attach(Node parent, string rowName, IReadOnlyList<CardModel> models, Action<Control, List<NGridCardHolder>> onBuilt)
    {
        try
        {
            if (parent == null || !GodotObject.IsInstanceValid(parent)) return;
            if (parent.GetNodeOrNull(rowName) != null) return;   // idempotent (pre-check)

            Callable.From(() =>
            {
                try
                {
                    if (!GodotObject.IsInstanceValid(parent) || parent.GetNodeOrNull(rowName) != null) return;

                    var row = new Control { Name = rowName, MouseFilter = Control.MouseFilterEnum.Ignore };
                    parent.AddChild(row);

                    var holders = new List<NGridCardHolder>();
                    foreach (var m in models)
                    {
                        var nc = NCard.Create(m);
                        if (nc == null) continue;
                        var h = NGridCardHolder.Create(nc);
                        if (h == null) continue;
                        row.AddChild(h);              // deferred build -> NCard._Ready → Reload renders it
                        holders.Add(h);
                    }
                    if (holders.Count == 0) { row.QueueFree(); return; }

                    try { onBuilt(row, holders); } catch { }
                    UiSafety.Passthrough(row);        // display-only: can never eat a pick
                }
                catch { }
            }).CallDeferred();
        }
        catch { }
    }
}
