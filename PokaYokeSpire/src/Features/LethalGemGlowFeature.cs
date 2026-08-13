using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.Combat;   // NEnergyCounter
using PokaYokeSpire.Core;

namespace PokaYokeSpire.Features;

/// <summary>
/// LETHAL GEM GLOW — turn the energy orb green when you can KILL. If you're hovering an enemy it glows
/// when your damage to THAT enemy (cards + scheduled) finishes it; with nothing hovered it glows when a
/// single play sequence can clear the whole room. Both come straight from the shared OFF-THREAD turn-sim
/// result (<see cref="TurnSimDriverFeature"/>) — this reads a cached value only, never runs a solver, so
/// it can't hitch or crash a frame (invariant 5). Opt-in via Config.LethalGemGlow.
/// </summary>
[HarmonyPatch(typeof(NEnergyCounter), "_Process")]
internal static class LethalGemGlowFeature
{
    private static bool _applied;   // did WE tint the orb (so we know to restore it)

    private static void Postfix(NEnergyCounter __instance) => Feature.Run("lethal-gem-render", () => true, () =>
    {
        try
        {
            bool lethal = Config.LethalGemGlow && IsLethal();

            var layers = Traverse.Create(__instance).Field("_layers").GetValue<Control>();
            if (layers == null || !GodotObject.IsInstanceValid(layers)) return;

            if (lethal)
            {
                layers.Modulate = new Color(0.2f, 1.4f, 0.3f);   // solid green — no pulse
                _applied = true;
            }
            else if (_applied)
            {
                layers.Modulate = Colors.White;                  // restore what we changed
                _applied = false;
            }
        }
        catch { }
    });

    private static bool IsLethal()
    {
        var o = TurnSimDriverFeature.Latest;
        if (o == null || !o.HasSim || o.Result.MaxPerEnemy == null || o.EnemyRefs == null) return false;

        var hov = EndTurnDamageFeature.HoveredEnemy;
        if (hov == null) return o.Result.CanKillAll;   // no hover → can we clear the whole room?

        // hovering an enemy → can we kill THAT enemy (its cards-damage + scheduled ≥ its HP)?
        for (int i = 0; i < o.EnemyRefs.Count && i < o.Result.MaxPerEnemy.Length && i < o.Scheduled.Length; i++)
            if (ReferenceEquals(o.EnemyRefs[i], hov))
            {
                int hp; try { hp = hov.CurrentHp; } catch { return false; }
                return hp > 0 && (o.Result.MaxPerEnemy[i] + o.Scheduled[i]) >= hp;
            }
        return false;
    }
}
