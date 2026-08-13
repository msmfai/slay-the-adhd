using System.Collections.Generic;
using MegaCrit.Sts2.Core.Combat;             // CombatState, CombatSide
using MegaCrit.Sts2.Core.Context;            // LocalContext
using MegaCrit.Sts2.Core.Entities.Cards;     // CostModifiers, TargetType
using MegaCrit.Sts2.Core.Entities.Creatures; // Creature
using MegaCrit.Sts2.Core.Entities.Players;   // Player
using MegaCrit.Sts2.Core.Localization.DynamicVars; // CalculatedVar
using MegaCrit.Sts2.Core.Models;             // CardModel

namespace PokaYokeSpire.Combat;

/// <summary>
/// Reads the current combat into the plain value-structs the <see cref="LethalSolver"/> works on —
/// hand cards, live enemies, energy, the player's Strength/Weak — all on the GAME THREAD (the only
/// place these game objects are safe to touch). Also hands back the live enemy <see cref="Creature"/>
/// refs so scheduled-damage (which reads powers) and hover can align to the same enemy order.
/// Everything is read structurally from card DynamicVars — the "no new information" boundary.
/// </summary>
public static class CombatSnapshot
{
    public sealed class Result
    {
        public List<LethalSolver.SimCard> Cards = new();
        public List<DefenseCalc.BlockCard> BlockCards = new();  // block-granting hand cards (for the defense gem)
        public List<LethalSolver.SimEnemy> Enemies = new();
        public List<Creature> EnemyRefs = new();   // aligned index-for-index with Enemies
        public int Energy;
        public int StartStrength;
        public int Dexterity;      // player Dexterity (raises block gained)
        public bool PlayerWeak;
        public bool PlayerFrail;   // Frail: 25% less block gained
    }

    public static Result? Build(CombatState combatState)
    {
        if (combatState.CurrentSide != CombatSide.Player) return null;

        var r = new Result();
        Creature? repTarget = null;
        foreach (var e in combatState.Enemies)
            if (e.CurrentHp > 0)
            {
                r.Enemies.Add(new LethalSolver.SimEnemy { Hp = e.CurrentHp, Block = e.Block, Vulnerable = HasPower(e, "VulnerablePower") });
                r.EnemyRefs.Add(e);
                repTarget ??= e;
            }
        if (r.Enemies.Count == 0) return null;

        Player? me = LocalContext.GetMe(combatState);
        if (me == null) return null;
        var pcs = me.PlayerCombatState;
        r.Energy = pcs.Energy;

        Creature? meC = LocalContext.GetMe((IEnumerable<Creature>)combatState.Creatures);
        r.StartStrength = meC == null ? 0 : PowerAmount(meC, "StrengthPower");
        r.Dexterity = meC == null ? 0 : PowerAmount(meC, "DexterityPower");
        r.PlayerWeak = meC != null && HasPower(meC, "WeakPower");
        r.PlayerFrail = meC != null && HasPower(meC, "FrailPower");

        foreach (var c in pcs.Hand.Cards)
        {
            try
            {
                if (c.EnergyCost.CostsX) continue;                 // X-cost: energy-dependent
                int cost = c.EnergyCost.GetWithModifiers(CostModifiers.All);
                int baseDmg = 0, strGain = 0, baseBlock = 0; bool appliesVuln = false;
                foreach (var v in c.DynamicVars.Values)
                {
                    var t = v.GetType();
                    if (t.Name == "DamageVar") baseDmg = (int)v.BaseValue;
                    else if (t.Name == "BlockVar") baseBlock = (int)v.BaseValue;
                    else if (t.IsGenericType && t.Name.StartsWith("PowerVar"))
                    {
                        string pt = t.GetGenericArguments()[0].Name;
                        if (pt == "StrengthPower") strGain += (int)v.BaseValue;
                        else if (pt == "VulnerablePower") appliesVuln = true;
                    }
                }
                if (baseBlock > 0) r.BlockCards.Add(new DefenseCalc.BlockCard(cost, baseBlock));   // defense gem

                bool aoe = c.TargetType == TargetType.AllEnemies;
                bool attack = baseDmg > 0;
                if (attack && !aoe && c.TargetType != TargetType.AnyEnemy) continue;   // unaimable attack
                if (!attack && strGain == 0 && !appliesVuln) continue;                 // no modeled OFFENSE
                int hits = attack ? ReadHits(c, repTarget) : 1;
                r.Cards.Add(new LethalSolver.SimCard(cost, baseDmg, hits, aoe, strGain, appliesVuln));
            }
            catch { /* unmodelable card -> excluded (the no-new-information boundary) */ }
        }
        return r;
    }

    /// Dynamic hit count (Tear Asunder etc.) resolved the way the game does; falls back to the static
    /// registry. See also LethalGemGlowState.ReadHits.
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
        catch { }
        return HitCountRegistry.HitsOf(card);
    }

    internal static bool HasPower(Creature c, string name)
    {
        foreach (var p in c.Powers) if (p.GetType().Name == name) return true;
        return false;
    }
    internal static int PowerAmount(Creature c, string name)
    {
        foreach (var p in c.Powers) if (p.GetType().Name == name) return p.Amount;
        return 0;
    }
}
