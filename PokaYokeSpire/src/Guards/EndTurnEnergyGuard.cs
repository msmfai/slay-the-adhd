using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;           // CombatState
using MegaCrit.Sts2.Core.Context;          // LocalContext
using MegaCrit.Sts2.Core.Entities.Players; // Player, PlayerCombatState
using MegaCrit.Sts2.Core.Nodes.Combat;     // NEndTurnButton

namespace PokaYokeSpire.Guards;

/// <summary>
/// GUARD 1 — confirm ending your turn while you still have unspent energy AND a playable
/// card. Intercepts the end-turn button's OnRelease (which then calls CallReleaseLogic
/// → enqueues EndPlayerTurnAction), so nothing has committed yet.
///
/// IMPORTANT: this hooks OnRelease, NOT CallReleaseLogic. Harmony wove CallReleaseLogic
/// into invalid IL ("Bad IL range") — every end-turn click threw. OnRelease is the
/// simpler caller and patches cleanly; CallReleaseLogic is left untouched. Re-invoke on
/// confirm goes through reflection (a cached MethodInfo), not a direct call.
/// </summary>
[HarmonyPatch(typeof(NEndTurnButton), "OnRelease")]
public static class EndTurnEnergyGuard
{
    private static bool _bypass;
    private static readonly MethodInfo _onRelease = AccessTools.Method(typeof(NEndTurnButton), "OnRelease");

    private static bool Prefix(NEndTurnButton __instance)
    {
        // FAIL-OPEN: any exception (e.g. another mod changed the state we read) must let the
        // end turn proceed normally — never crash or trap the button.
        try
        {
            if (!Config.GuardEndTurnEnergy) return true;
            if (_bypass) { _bypass = false; return true; }

            CombatState? state = Traverse.Create(__instance).Field("_combatState").GetValue<CombatState>();
            if (state == null) return true;

            Player? me = LocalContext.GetMe(state);
            if (me == null) return true;
            PlayerCombatState pcs = me.PlayerCombatState;

            if (GuardLogic.ShouldConfirmEndTurn(pcs.Energy, pcs.HasCardsToPlay()))
            {
                int energy = pcs.Energy;
                MegaCrit.Sts2.Core.Logging.Log.Info($"[Poka-Yoke] guard1 FIRED: end-turn with {energy} energy + playable card");
                PopupHelper.ShowConfirm(
                    title: "End turn?",
                    body: $"You still have {energy} energy and a playable card in hand.",
                    onYes: () => { _bypass = true; _onRelease.Invoke(__instance, null); });
                return false; // block the end-turn until confirmed
            }
            return true;
        }
        catch (System.Exception e)
        {
            MegaCrit.Sts2.Core.Logging.Log.Info($"[Poka-Yoke] guard1 error (failing open): {e.Message}");
            return true;
        }
    }
}
