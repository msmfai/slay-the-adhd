using System;
using System.Collections.Generic;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Context;                     // LocalContext
using MegaCrit.Sts2.Core.Entities.Cards;              // CardType, CostModifiers
using MegaCrit.Sts2.Core.Entities.Players;            // Player
using MegaCrit.Sts2.Core.Models;                      // CardModel
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection; // NCardRewardSelectionScreen
using MegaCrit.Sts2.Core.Runs;                        // RunManager, RunState
using PokaYokeSpire.Combat;
using PokaYokeSpire.Core;
using PokaYokeSpire.Spatial;

namespace PokaYokeSpire.Features;

/// <summary>
/// DECK STATS — flank the card-reward choices with aggregate deck metrics (see <see cref="DeckStats"/>
/// for the chosen orthogonal basis). Additive + fail-open. Values are read structurally from each
/// card's canonical DynamicVars (the same boundary the lethal solver uses), so the numbers are the
/// ones printed on the cards, never invented. Panels are placed by the pure SidePanelLayout and
/// re-checked against the measured reward rectangle so they can't occlude the choices.
/// </summary>
[HarmonyPatch(typeof(NCardRewardSelectionScreen), "_Ready")]
internal static class DeckStatsFeature
{
    private const string LeftName = "PokaYokeStatsLeft";
    private const string RightName = "PokaYokeStatsRight";

    private static void Postfix(NCardRewardSelectionScreen __instance) =>
        Feature.RunUi("deck-stats", () => Config.ShowDeckStats, () => Build(__instance));

    private static void Build(NCardRewardSelectionScreen screen)
    {
        var deck = GetDeck();
        if (deck == null || deck.Count == 0) return;   // fail-closed

        var facts = new List<DeckStats.CardFact>(deck.Count);
        foreach (var c in deck) { try { facts.Add(CardFactReader.Read(c)); } catch { /* skip an unreadable card */ } }
        if (facts.Count == 0) return;
        var stats = DeckStats.Compute(facts);

        Vector2 vp = screen.GetViewportRect().Size;
        float panelW = MathF.Max(240f, vp.X * 0.15f);
        float panelH = 46f + 30f * 5 + 14f;   // sized for the taller (5-row) panel

        // centre the panels on the reward choices (measured; falls back to upper third)
        float centerY = vp.Y * 0.30f;
        var cardRow = screen.GetNodeOrNull<Control>("UI/CardRow");
        UiRect rewardArea;
        if (cardRow != null && cardRow.Size.X > 1f)
        {
            var gr = cardRow.GetGlobalRect();
            centerY = gr.Position.Y + gr.Size.Y / 2f;
            rewardArea = new UiRect("reward", gr.Position.X, gr.Position.Y, gr.Size.X, gr.Size.Y);
        }
        else rewardArea = new UiRect("reward", (vp.X - vp.X * 0.55f) / 2f, vp.Y * 0.10f, vp.X * 0.55f, vp.Y * 0.55f);

        // solve panel width against the occlusion lint — shrink until clear of the choices
        (UiRect left, UiRect right) rects = default;
        for (int i = 0; i < 6; i++)
        {
            rects = SidePanelLayout.Compute(vp.X, vp.Y, panelW, panelH, centerY, Tunables.SidePanelMarginFrac);
            if (!SpatialGraph.Intersects(rects.left, rewardArea) && !SpatialGraph.Intersects(rects.right, rewardArea)) break;
            panelW *= 0.85f;
            if (panelW < 150f) break;
        }

        var leftPanel = Overlay.Attach(screen, LeftName, () => BuildPanel("Deck", DeckStats.LeftRows(stats), rects.left.W, panelH, new Color(0.6f, 0.85f, 1f)));
        if (leftPanel != null) leftPanel.Position = new Vector2(rects.left.X, rects.left.Y);

        var rightPanel = Overlay.Attach(screen, RightName, () => BuildPanel("Output", DeckStats.RightRows(stats), rects.right.W, panelH, new Color(1f, 0.8f, 0.55f)));
        if (rightPanel != null) rightPanel.Position = new Vector2(rects.right.X, rects.right.Y);
    }

    private static Control BuildPanel(string title,
        IReadOnlyList<(string label, string value)> rows, float panelW, float panelH, Color accent)
    {
        var panel = new Control { Size = new Vector2(panelW, panelH), MouseFilter = Control.MouseFilterEnum.Ignore };

        var bg = new ColorRect { Color = new Color(0.05f, 0.06f, 0.09f, 0.72f), Size = new Vector2(panelW, panelH), MouseFilter = Control.MouseFilterEnum.Ignore };
        panel.AddChild(bg);

        var titleLabel = MakeLabel(title, 24, accent, HorizontalAlignment.Center);
        titleLabel.Position = new Vector2(0, 8); titleLabel.Size = new Vector2(panelW, 30);
        panel.AddChild(titleLabel);

        float rowY = 46f;
        foreach (var (label, value) in rows)
        {
            var l = MakeLabel(label, 20, new Color(0.82f, 0.85f, 0.9f), HorizontalAlignment.Left);
            l.Position = new Vector2(14, rowY); l.Size = new Vector2(panelW - 28, 28);
            panel.AddChild(l);

            var val = MakeLabel(value, 20, Colors.White, HorizontalAlignment.Right);
            val.Position = new Vector2(14, rowY); val.Size = new Vector2(panelW - 28, 28);
            panel.AddChild(val);

            rowY += 30f;
        }
        return panel;
    }

    private static Label MakeLabel(string text, int size, Color color, HorizontalAlignment h)
    {
        var l = new Label { Text = text, MouseFilter = Control.MouseFilterEnum.Ignore, HorizontalAlignment = h, VerticalAlignment = VerticalAlignment.Center };
        var font = ThemeDB.Singleton?.FallbackFont;
        if (font != null) l.AddThemeFontOverride("font", font);
        l.AddThemeFontSizeOverride("font_size", size);
        l.AddThemeColorOverride("font_color", color);
        return l;
    }

    private static IReadOnlyList<CardModel>? GetDeck()
    {
        try
        {
            var t = Traverse.Create(RunManager.Instance);
            var state = t.Property("State").GetValue<RunState>() ?? t.Field("State").GetValue<RunState>();
            if (state == null) return null;
            Player? me = LocalContext.GetMe(state.Players);
            return me?.Deck?.Cards;
        }
        catch { return null; }
    }
}
