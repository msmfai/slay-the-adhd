using System;
using System.Collections.Generic;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Context;                     // LocalContext
using MegaCrit.Sts2.Core.Entities.Players;            // Player
using MegaCrit.Sts2.Core.Models;                      // CardModel
using MegaCrit.Sts2.Core.Nodes.Cards;                 // NCard, NCardHighlight
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;         // NGridCardHolder
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection; // NCardRewardSelectionScreen
using MegaCrit.Sts2.Core.Runs;                        // RunManager, RunState
using PokaYokeSpire.Spatial;

namespace PokaYokeSpire.Features;

/// <summary>
/// DECK UNDER REWARD — on the card-reward screen, lift the choices a little and fan the player's
/// whole current deck across the bottom (overlapping when crowded; hover a card to bring it to the
/// front and highlight it exactly like a reward choice). Additive + fail-open: it can never block
/// selecting a card.
///
/// SPATIAL CORRECTNESS (CLAUDE.md § "Spatial placement"): an LLM can't see this screen, so the
/// geometry is produced by the pure <see cref="DeckRowLayout"/> and then re-checked at runtime
/// against the MEASURED rectangles with the same <see cref="Spatial"/> lints the tests use — the
/// card scale is shrunk until the fan is on-screen, clear of the choices, and every card shows a
/// visible sliver. No eyeballed pixel offsets.
/// </summary>
[HarmonyPatch(typeof(NCardRewardSelectionScreen), "_Ready")]
internal static class DeckUnderRewardFeature
{
    private const string RowName = "PokaYokeDeckRow";

    private static void Postfix(NCardRewardSelectionScreen __instance)
    {
        try
        {
            if (Config.DisableAllOverlays || !Config.ShowDeckUnderReward) return;
            if (__instance.GetNodeOrNull(RowName) != null) return;   // already built for this screen
            // Build DEFERRED: adding cards during the screen's own _Ready leaves them as "broken card"
            // because each NCard's _Ready→Reload (the render) can't run cleanly mid-construction. The
            // game adds cards after setup (AddChildSafely); deferring one idle frame does the same.
            Callable.From(() => BuildRow(__instance)).CallDeferred();
        }
        catch (Exception e) { MegaCrit.Sts2.Core.Logging.Log.Info($"[Poka-Yoke] deck-under-reward schedule error: {e.Message}"); }
    }

    private static void BuildRow(NCardRewardSelectionScreen __instance)
    {
        try
        {
            if (!GodotObject.IsInstanceValid(__instance)) return;
            if (__instance.GetNodeOrNull(RowName) != null) return;

            var deck = GetDeck();
            if (deck == null || deck.Count == 0) return;

            Vector2 vp = __instance.GetViewportRect().Size;

            // The whole fan is DISPLAY-ONLY and mouse-transparent (UiSafety.Passthrough at the end) so
            // it can never sit over the reward choices and swallow a pick — hardening over the old
            // hover-to-front, which required input and could block card selection when the fan clipped.
            var row = new Control { Name = RowName, MouseFilter = Control.MouseFilterEnum.Ignore };
            __instance.AddChild(row);

            // 1) instantiate each card wrapped in an NGridCardHolder — the SAME path the reward screen
            //    uses for its choices. A raw NCard added straight to the tree renders as "broken card".
            var made = new List<NGridCardHolder>();
            foreach (var card in deck)
            {
                var nc = NCard.Create(card);
                if (nc == null) continue;
                var holder = NGridCardHolder.Create(nc);
                if (holder == null) continue;
                row.AddChild(holder);
                made.Add(holder);
            }
            if (made.Count == 0) { row.QueueFree(); return; }

            // 2) measure real footprints (fall back to sane defaults if not laid out yet)
            float baseW = made[0].Size.X > 1f ? made[0].Size.X : 360f;
            float baseH = made[0].Size.Y > 1f ? made[0].Size.Y : 760f;

            var cardRow = __instance.GetNodeOrNull<Control>("UI/CardRow");
            float rewCenterY, rewW, rewH;
            if (cardRow != null && cardRow.Size.X > 1f)
            {
                var gr = cardRow.GetGlobalRect();
                rewCenterY = gr.Position.Y + gr.Size.Y / 2f; rewW = gr.Size.X; rewH = gr.Size.Y;
            }
            else { rewCenterY = vp.Y * 0.32f; rewW = vp.X * 0.55f; rewH = 470f; }

            // 3) solve the scale against the lints — correct by construction, not by guessing
            float scale = 0.34f;
            DeckRowLayout.Result layout = default;
            for (int iter = 0; iter < 6; iter++)
            {
                layout = DeckRowLayout.Compute(vp.X, vp.Y, made.Count, baseW * scale, baseH * scale, rewCenterY, rewW, rewH);
                var deckBox = SpatialGraph.BoundingBox("deck", layout.DeckCards);
                bool onScreen = SpatialGraph.OutOfBounds(layout.DeckCards, vp.X, vp.Y).Count == 0;
                bool clear = SpatialGraph.GroupsVerticallyClear(layout.RewardArea, deckBox, DeckRowLayout.GroupMinGap);
                bool spaced = SpatialGraph.MinSpacingViolations(layout.DeckCards, DeckRowLayout.MinVisibleStep).Count == 0;
                if ((onScreen && clear && spaced) || scale <= 0.18f) break;
                scale *= 0.85f;
            }

            // 4) drop the deck fan + the skip/reward-alternative buttons by HALF the skip button's
            // font height, so the owned cards don't clip the ones you're choosing.
            float skipDrop = MeasureSkipFontHeight(__instance) * 0.5f;
            var alts = __instance.GetNodeOrNull<Control>("UI/RewardAlternatives");
            if (alts != null && GodotObject.IsInstanceValid(alts)) alts.Position += new Vector2(0f, skipDrop);

            // 5) apply the solved geometry (fan dropped by skipDrop)
            for (int i = 0; i < made.Count; i++)
            {
                made[i].Scale = new Vector2(scale, scale);
                made[i].Position = new Vector2(layout.DeckCards[i].X, layout.DeckCards[i].Y + skipDrop);
            }

            // 6) raise the choices (the deck fan already lifted by half this inside DeckRowLayout)
            if (cardRow != null && GodotObject.IsInstanceValid(cardRow))
                cardRow.Position -= new Vector2(0f, DeckRowLayout.RewardRaise);

            // HARDENING: guarantee the whole fan is transparent to the mouse — it can never block a pick.
            UiSafety.Passthrough(row);
        }
        catch (Exception e) { MegaCrit.Sts2.Core.Logging.Log.Info($"[Poka-Yoke] deck-under-reward error: {e.Message}"); }
    }

    /// Height of the skip / reward-alternative button's text label (its font height), used as the
    /// vertical drop. Falls back if the label isn't laid out yet.
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
