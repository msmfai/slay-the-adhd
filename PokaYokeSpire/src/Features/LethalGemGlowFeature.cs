using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;             // CombatState, CombatSide
using MegaCrit.Sts2.Core.Context;            // LocalContext
using MegaCrit.Sts2.Core.Entities.Cards;     // CostModifiers, TargetType
using MegaCrit.Sts2.Core.Entities.Creatures; // Creature
using MegaCrit.Sts2.Core.Entities.Players;   // Player
using MegaCrit.Sts2.Core.Localization.DynamicVars; // CalculatedVar (dynamic hit counts)
using MegaCrit.Sts2.Core.Models;             // CardModel
using MegaCrit.Sts2.Core.Nodes.Combat;       // NEnergyCounter
using PokaYokeSpire.Combat;
using PokaYokeSpire.Core;

namespace PokaYokeSpire.Features;

/// <summary>
/// LETHAL GEM GLOW — turn the energy gem green + glowing when a deterministic line of card
/// plays could finish the fight this turn (chains Strength / Vulnerable / multi-hit / AOE across
/// enemies; see LethalSolver). Opt-in via Config.LethalGemGlow.
///
/// PERFORMANCE: the search is potentially expensive, so it runs OFF the game thread. On each
/// combat-state change we snapshot the state into PLAIN data (no game/Godot objects) on the game
/// thread, then solve on a background Task and cache a bool. _Process only reads that bool and
/// animates — it never runs the solver, so it can never hitch or crash the frame.
/// </summary>
[HarmonyPatch(typeof(NEnergyCounter), "OnCombatStateChanged")]
internal static class LethalGemGlowState
{
    internal static CombatState? State;
    internal static volatile bool IsLethal;
    private static long _gen;
    private static bool _logged;

    private static void Postfix(CombatState combatState) => Feature.Run("lethal-gem-solve", () => true, () =>
    {
        State = combatState;
        try
        {
            if (!Config.LethalGemGlow) { IsLethal = false; return; }

            // --- snapshot everything the solver needs, on the GAME THREAD (plain structs) ---
            if (combatState.CurrentSide != CombatSide.Player) { IsLethal = false; return; }

            var enemies = new List<LethalSolver.SimEnemy>();
            Creature? repTarget = null;   // a representative live enemy for resolving dynamic hit counts
            foreach (var e in combatState.Enemies)
                if (e.CurrentHp > 0)
                {
                    enemies.Add(new LethalSolver.SimEnemy { Hp = e.CurrentHp, Block = e.Block, Vulnerable = HasPower(e, "VulnerablePower") });
                    repTarget ??= e;
                }
            if (enemies.Count == 0) { IsLethal = false; return; }

            Player? me = LocalContext.GetMe(combatState);
            if (me == null) { IsLethal = false; return; }
            var pcs = me.PlayerCombatState;

            Creature? meC = LocalContext.GetMe((IEnumerable<Creature>)combatState.Creatures);
            int startStrength = meC == null ? 0 : PowerAmount(meC, "StrengthPower");

            var cards = new List<LethalSolver.SimCard>();
            foreach (var c in pcs.Hand.Cards)
            {
                try
                {
                    if (c.EnergyCost.CostsX) continue;                 // X-cost: energy-dependent
                    int baseDmg = 0, strGain = 0; bool appliesVuln = false;
                    foreach (var v in c.DynamicVars.Values)
                    {
                        var t = v.GetType();
                        if (t.Name == "DamageVar") baseDmg = (int)v.BaseValue;
                        else if (t.IsGenericType && t.Name.StartsWith("PowerVar"))
                        {
                            string pt = t.GetGenericArguments()[0].Name;
                            if (pt == "StrengthPower") strGain += (int)v.BaseValue;
                            else if (pt == "VulnerablePower") appliesVuln = true;
                        }
                    }
                    bool aoe = c.TargetType == TargetType.AllEnemies;
                    bool attack = baseDmg > 0;
                    if (attack && !aoe && c.TargetType != TargetType.AnyEnemy) continue;   // unaimable attack
                    if (!attack && strGain == 0 && !appliesVuln) continue;                 // no modeled effect
                    int hits = attack ? ReadHits(c, repTarget) : 1;
                    int cost = c.EnergyCost.GetWithModifiers(CostModifiers.All);
                    cards.Add(new LethalSolver.SimCard(cost, baseDmg, hits, aoe, strGain, appliesVuln));
                }
                catch { /* unmodelable card -> excluded (the no-new-information boundary) */ }
            }

            int energy = Config.LethalGlowRespectEnergy ? pcs.Energy : 999;

            // --- solve OFF-THREAD; only the newest snapshot's result is applied ---
            long g = Interlocked.Increment(ref _gen);
            Task.Run(() =>
            {
                bool r;
                try { r = LethalSolver.Solve(cards, energy, startStrength, enemies); }
                catch { r = false; }
                if (Interlocked.Read(ref _gen) != g) return;   // a newer snapshot superseded us
                IsLethal = r;
                if (r && !_logged) { _logged = true; MegaCrit.Sts2.Core.Logging.Log.Info($"[Poka-Yoke] feature9 LETHAL: solver over {cards.Count} cards vs {enemies.Count} enemy(s), str={startStrength}"); }
                if (!r) _logged = false;
            });
        }
        catch { IsLethal = false; }
    });

    /// The card's real hit count. Cards with a dynamic count (e.g. Tear Asunder) store a
    /// "CalculatedHits" CalculatedVar and resolve it via Calculate(target) — the exact value the game
    /// passes to WithHitCount. Resolving it here (on the game thread, from CURRENT visible state) is
    /// deterministic and within the "no new information" boundary; anything else falls back to the
    /// static HitCountRegistry.
    private static int ReadHits(CardModel card, Creature? target)
    {
        try
        {
            if (card.DynamicVars.TryGetValue("CalculatedHits", out var v) && v is CalculatedVar cv)
            {
                int h = (int)cv.Calculate(target);
                return h < 1 ? 1 : h;
            }
        }
        catch { /* owner/state not ready -> fall back */ }
        return HitCountRegistry.HitsOf(card);
    }

    private static bool HasPower(Creature c, string name)
    {
        foreach (var p in c.Powers) if (p.GetType().Name == name) return true;
        return false;
    }
    private static int PowerAmount(Creature c, string name)
    {
        foreach (var p in c.Powers) if (p.GetType().Name == name) return p.Amount;
        return 0;
    }
}

[HarmonyPatch(typeof(NEnergyCounter), "_Process")]
internal static class LethalGemGlowFeature
{
    private static bool _applied;   // did WE tint the orb (so we know to restore it)

    // Reads a cached bool only — NEVER runs the solver. Safe to run every frame.
    private static void Postfix(NEnergyCounter __instance) => Feature.Run("lethal-gem-render", () => true, () =>
    {
        try
        {
            bool lethal = Config.LethalGemGlow && LethalGemGlowState.IsLethal;

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
}
