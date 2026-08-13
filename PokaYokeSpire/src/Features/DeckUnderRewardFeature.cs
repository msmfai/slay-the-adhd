using System.Collections.Generic;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Context;                     // LocalContext
using MegaCrit.Sts2.Core.Entities.Players;            // Player
using MegaCrit.Sts2.Core.Models;                      // CardModel
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;         // NGridCardHolder
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection; // NCardRewardSelectionScreen
using MegaCrit.Sts2.Core.Runs;                        // RunManager, RunState
using PokaYokeSpire.Core;
using PokaYokeSpire.Spatial;

namespace PokaYokeSpire.Features;

/// <summary>
/// DECK UNDER REWARD — on the card-reward screen, fan the player's whole current deck across the
/// bottom so you can see what you own while picking, and lift the choices a little to clear it.
///
/// Correct by construction: the patch body runs through <see cref="Feature.RunUi"/> (fail-open,
/// auto-disable, config- + kill-switch-gated); the cards are shown through <see cref="CardDisplay"/>
/// (the game's create→defer→ready path — never "broken card" — and mouse-transparent, so it can
/// never block a pick); the geometry is the pure <see cref="DeckRowLayout"/> re-checked at runtime
/// against the same <see cref="SpatialGraph"/> lints the tests use (no eyeballed offsets).
/// </summary>
[HarmonyPatch(typeof(NCardRewardSelectionScreen), "_Ready")]
internal static class DeckUnderRewardFeature
{
    private const string RowName = "PokaYokeDeckRow";

    private static void Postfix(NCardRewardSelectionScreen __instance) =>
        Feature.RunUi("deck-under-reward", () => Config.ShowDeckUnderReward, () =>
        {
            var deck = GetDeck();
            if (deck == null || deck.Count == 0) return;   // fail-closed: no deck -> nothing
            CardDisplay.Attach(__instance, RowName, deck, (row, made) => Layout(__instance, made));
        });

    /// Places the (already-rendered) holders using the pure layout + runtime lint self-correction.
    private static void Layout(NCardRewardSelectionScreen screen, List<NGridCardHolder> made)
    {
        Vector2 vp = screen.GetViewportRect().Size;
        float baseW = made[0].Size.X > 1f ? made[0].Size.X : 360f;
        float baseH = made[0].Size.Y > 1f ? made[0].Size.Y : 760f;

        var cardRow = screen.GetNodeOrNull<Control>("UI/CardRow");
        float rewCenterY, rewW, rewH;
        if (cardRow != null && cardRow.Size.X > 1f)
        {
            var gr = cardRow.GetGlobalRect();
            rewCenterY = gr.Position.Y + gr.Size.Y / 2f; rewW = gr.Size.X; rewH = gr.Size.Y;
        }
        else { rewCenterY = vp.Y * 0.32f; rewW = vp.X * 0.55f; rewH = 470f; }

        // shrink the card scale until the fan is on-screen, clear of the choices, every card visible
        float scale = 0.34f;
        DeckRowLayout.Result layout = default;
        for (int iter = 0; iter < 6; iter++)
        {
            layout = DeckRowLayout.Compute(vp.X, vp.Y, made.Count, baseW * scale, baseH * scale, rewCenterY, rewW, rewH,
                Tunables.RewardRaise, Tunables.RewardRaise * Tunables.DeckRaiseFracOfReward,
                Tunables.DeckFanGap, Tunables.DeckMinVisibleStep);
            var deckBox = SpatialGraph.BoundingBox("deck", layout.DeckCards);
            bool onScreen = SpatialGraph.OutOfBounds(layout.DeckCards, vp.X, vp.Y).Count == 0;
            bool clear = SpatialGraph.GroupsVerticallyClear(layout.RewardArea, deckBox, DeckRowLayout.GroupMinGap);
            bool spaced = SpatialGraph.MinSpacingViolations(layout.DeckCards, Tunables.DeckMinVisibleStep).Count == 0;
            if ((onScreen && clear && spaced) || scale <= 0.18f) break;
            scale *= 0.85f;
        }

        // drop the fan + skip buttons by half the skip font height so the owned cards don't clip the choices
        float skipDrop = MeasureSkipFontHeight(screen) * 0.5f;
        var alts = screen.GetNodeOrNull<Control>("UI/RewardAlternatives");
        if (alts != null && GodotObject.IsInstanceValid(alts)) alts.Position += new Vector2(0f, skipDrop);

        for (int i = 0; i < made.Count; i++)
        {
            made[i].Scale = new Vector2(scale, scale);
            made[i].Position = new Vector2(layout.DeckCards[i].X, layout.DeckCards[i].Y + skipDrop);
        }

        if (cardRow != null && GodotObject.IsInstanceValid(cardRow))
            cardRow.Position -= new Vector2(0f, Tunables.RewardRaise);
    }

    private static float MeasureSkipFontHeight(Node screen)
    {
        try
        {
            var alts = screen.GetNodeOrNull<Control>("UI/RewardAlternatives");
            if (alts != null)
                foreach (var child in alts.GetChildren())
                    if (child is Control c)
                    {
                        var label = c.GetNodeOrNull<Control>("Label");
                        if (label != null && label.Size.Y > 1f) return label.Size.Y;
                    }
        }
        catch { }
        return 40f;
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
