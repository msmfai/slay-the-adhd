using System.Linq;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;           // CombatState
using MegaCrit.Sts2.Core.Context;          // LocalContext
using MegaCrit.Sts2.Core.Entities.Players; // Player
using MegaCrit.Sts2.Core.Nodes.Combat;     // NEndTurnButton
using MegaCrit.Sts2.Core.Rooms;            // RoomType

namespace PokaYokeSpire.Guards;

/// <summary>
/// GUARD 2 — start-of-combat potion reminder. At the start of your turn in an
/// elite/boss fight, once per fight (so it lands on turn 1), if you're holding a
/// potion, pop a reminder. If EVERY potion slot is full, show an all-caps red warning.
///
/// Hook: POSTFIX on NEndTurnButton.AnimIn — the end-turn button flies in at the start
/// of the player's turn. This is a Godot NODE (the same class guard 1 patches safely)
/// and a postfix (runs AFTER the original; never alters control flow), so it CANNOT
/// brick anything. Deliberately unrelated to card-play: prefixing card-play methods
/// (CardModel.EnqueueManualPlay or NCardPlay.TryPlayCard) either wove bad IL or left
/// the card-play UI stuck mid-play — both bricked the game.
/// </summary>
[HarmonyPatch(typeof(NEndTurnButton), "AnimIn")]
public static class ElitePotionGuard
{
    private static CombatState? _remindedForCombat;

    private static void Postfix(NEndTurnButton __instance)
    {
        // FAIL-OPEN: a postfix, so on any error we simply skip the reminder — never disturb
        // the turn-start the button just animated in.
        try
        {
            if (!Config.GuardElitePotion) return;

            // _combatState is private on the button; read it the way guard 1 does.
            CombatState? state = Traverse.Create(__instance).Field("_combatState").GetValue<CombatState>();
            if (state == null) return;

            // Once per fight -> lands on the first player turn (turn 1); enforced by the
            // alreadyReminded flag fed into GuardLogic.ShouldRemindPotion below.
            RoomType? room = state.Encounter?.RoomType;
            bool isEliteOrBoss = room == RoomType.Elite || room == RoomType.Boss;

            Player? me = LocalContext.GetMe(state);
            bool hasPotion = me != null && me.Potions.Any();
            bool alreadyReminded = ReferenceEquals(state, _remindedForCombat);

            if (!GuardLogic.ShouldRemindPotion(isEliteOrBoss, hasPotion, alreadyReminded)) return;

            _remindedForCombat = state;
            bool allSlotsFull = !me!.HasOpenPotionSlots; // every potion slot occupied
            MegaCrit.Sts2.Core.Logging.Log.Info($"[Poka-Yoke] guard2 FIRED: potion reminder (allSlotsFull={allSlotsFull})");
            PopupHelper.ShowNotice(title: "Potions", body: GuardLogic.PotionBody(allSlotsFull)); // reminder only — single OK
        }
        catch (System.Exception e) { MegaCrit.Sts2.Core.Logging.Log.Info($"[Poka-Yoke] guard2 error (skipping): {e.Message}"); }
    }
}
