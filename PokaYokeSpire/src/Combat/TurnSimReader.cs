using System.Collections.Generic;
using MegaCrit.Sts2.Core.Combat;                 // CombatState, CombatSide
using MegaCrit.Sts2.Core.Context;                // LocalContext
using MegaCrit.Sts2.Core.Entities.Cards;         // CostModifiers, TargetType, CardType, CardKeyword
using MegaCrit.Sts2.Core.Entities.Creatures;     // Creature
using MegaCrit.Sts2.Core.Entities.Players;       // Player
using MegaCrit.Sts2.Core.Localization.DynamicVars; // CalculatedVar
using MegaCrit.Sts2.Core.Models;                 // CardModel
using MegaCrit.Sts2.Core.MonsterMoves.Intents;   // AttackIntent
using PokaYokeSpire.Core;                         // DebugLog

namespace PokaYokeSpire.Combat;

/// <summary>
/// Snapshots the live combat into <see cref="TurnSim"/>'s plain structs, on the GAME THREAD. This is the
/// "encoded so far" boundary: every card is read structurally (DynamicVars → damage/block/apply-power/
/// energy) with a few bespoke cases (Body Slam, Second Wind) by class name; anything with no modeled
/// effect is logged (DebugLogging) so coverage closes from real play. Player/enemy powers that bend the
/// damage/block math are read by class name; enemy queued attacks are read as base-per-hit × repeats so
/// the sim can apply Weak/Vulnerable itself.
/// </summary>
public static class TurnSimReader
{
    public sealed class Snapshot
    {
        public TurnSim.Player Player;
        public TurnSim.Enemy[] Enemies = System.Array.Empty<TurnSim.Enemy>();
        public List<Creature> EnemyRefs = new();   // index-aligned with Enemies (for hover/lethal mapping)
        public List<TurnSim.Card> Hand = new();
    }

    public static Snapshot? Read(CombatState state)
    {
        if (state.CurrentSide != CombatSide.Player) return null;

        Player? mePlayer = LocalContext.GetMe(state);
        Creature? meC = LocalContext.GetMe((IEnumerable<Creature>)state.Creatures);
        if (mePlayer == null || meC == null) return null;
        var pcs = mePlayer.PlayerCombatState;

        var snap = new Snapshot();

        // ── player ──
        int endOfTurnBlock = 0;
        try { endOfTurnBlock = EndOfTurnBlockRegistry.Predict(meC, meC.Block); } catch { }
        snap.Player = new TurnSim.Player
        {
            Energy = pcs.Energy,
            Strength = PowerAmount(meC, "StrengthPower"),
            Dexterity = PowerAmount(meC, "DexterityPower"),
            Block = meC.Block + endOfTurnBlock,
            Weak = Has(meC, "WeakPower") ? 1 : 0,
            Frail = Has(meC, "FrailPower") ? 1 : 0,
            Vulnerable = Has(meC, "VulnerablePower") ? 1 : 0,
            Shrink = Has(meC, "ShrinkPower") ? 1 : 0,
            Intangible = Has(meC, "IntangiblePower"),   // caps each hit you take to 1
        };

        // ── enemies ──
        var enemies = new List<TurnSim.Enemy>();
        foreach (var e in state.Enemies)
        {
            if (e.CurrentHp <= 0) continue;
            var (dmg, hits) = ReadIntent(e);
            var hardShell = FindPower(e, "HardenedShellPower");   // caps HP damage taken this turn
            enemies.Add(new TurnSim.Enemy
            {
                Hp = e.CurrentHp,
                Block = e.Block,
                Vulnerable = Has(e, "VulnerablePower") ? 1 : 0,
                Weak = Has(e, "WeakPower") ? 1 : 0,
                Strength = PowerAmount(e, "StrengthPower"),
                IntentDamage = dmg,
                IntentHits = hits,
                Capped = hardShell != null,
                CapRemaining = hardShell?.DisplayAmount ?? 0,   // remaining allowance this turn
            });
            snap.EnemyRefs.Add(e);
        }
        if (enemies.Count == 0) return null;
        snap.Enemies = enemies.ToArray();

        // ── hand ──
        int endTurnSelf = 0;
        foreach (var cm in pcs.Hand.Cards)
        {
            try
            {
                // status/curse cards that hurt you at end of turn while in hand (Burn 2, Toxic 5,
                // Infection 3, BadLuck 13, Beckon 6, Decay 2, Regret = hand size, …).
                if (cm.HasTurnEndInHandEffect)
                    foreach (var v in cm.DynamicVars.Values) if (v.GetType().Name == "DamageVar") { endTurnSelf += (int)v.BaseValue; break; }

                var card = ReadCard(cm);
                if (card != null) snap.Hand.Add(card);
            }
            catch { /* unreadable card -> excluded (worst case) */ }
        }
        snap.Player.EndTurnSelfDamage = endTurnSelf;
        return snap;
    }

    private static (int dmg, int hits) ReadIntent(Creature enemy)
    {
        try
        {
            var move = enemy.Monster?.NextMove;
            if (move == null) return (0, 0);
            int baseDmg = 0, hits = 0;
            foreach (var intent in move.Intents)
                if (intent is AttackIntent atk && atk.DamageCalc != null)
                {
                    // collapse the enemy's attack(s) to base-per-hit × repeats (common: one intent)
                    baseDmg += (int)atk.DamageCalc();
                    hits = atk.Repeats < 1 ? 1 : atk.Repeats;
                }
            return (baseDmg, hits < 1 ? 1 : hits);
        }
        catch { return (0, 0); }
    }

    private static TurnSim.Card? ReadCard(CardModel cm)
    {
        string name = cm.GetType().Name;

        // Unplayable cards (Burn/Wound/curses/statuses) are NOT plays — their DamageVar hits YOU at
        // end of turn (handled separately), it is not a card you can deal with. Never treat as an attack.
        if (cm.Keywords.Contains(CardKeyword.Unplayable)) return null;

        // X-cost attacks (Whirlwind: deal D to all enemies X times, X = energy). Model the attack; the
        // sim spends all energy and multiplies hits by X at play time. Non-attack X-cost isn't modeled.
        if (cm.EnergyCost.CostsX)
        {
            int xdmg = 0;
            foreach (var v in cm.DynamicVars.Values) if (v.GetType().Name == "DamageVar") { xdmg = (int)v.BaseValue; break; }
            if (xdmg <= 0)
            {
                if (DebugLog.Enabled) DebugLog.Debug($"turnsim: X-cost non-attack '{name}' unmodeled — excluded");
                return null;
            }
            return new TurnSim.Card { Name = name, Cost = 0, XCost = true, Damage = xdmg, Hits = 1, AttackTarget = EnemyTgt(cm.TargetType) };
        }

        int cost = cm.EnergyCost.GetWithModifiers(CostModifiers.All);

        var card = new TurnSim.Card { Name = name, Cost = cost, Hits = 1 };

        foreach (var v in cm.DynamicVars.Values)
        {
            var t = v.GetType();
            switch (t.Name)
            {
                case "DamageVar": card.Damage = (int)v.BaseValue; break;
                case "BlockVar": card.Block = (int)v.BaseValue; break;
                case "EnergyVar": card.EnergyGain = (int)v.BaseValue; break;
                default:
                    if (t.IsGenericType && t.Name.StartsWith("PowerVar"))
                    {
                        string pt = t.GetGenericArguments()[0].Name;
                        if (pt == "StrengthPower") card.StrengthGain += (int)v.BaseValue;
                        else if (pt == "VulnerablePower") { card.ApplyVulnerable = (int)v.BaseValue; card.VulnTarget = EnemyTgt(cm.TargetType); }
                        else if (pt == "WeakPower") { card.ApplyWeak = (int)v.BaseValue; card.WeakTarget = EnemyTgt(cm.TargetType); }
                    }
                    break;
            }
        }

        if (card.Damage > 0)
        {
            card.AttackTarget = EnemyTgt(cm.TargetType);
            card.Hits = ReadHits(cm);
        }
        card.Exhausts = cm.Keywords.Contains(CardKeyword.Exhaust);

        // bespoke, state-dependent cards (behaviour lives in OnPlay; detect by class name)
        if (name == "BodySlam") { card.Dynamic = TurnSim.Dyn.BodySlam; card.AttackTarget = TurnSim.Tgt.OneEnemy; }
        else if (name == "SecondWind") { card.Dynamic = TurnSim.Dyn.SecondWind; card.DynParam = card.Block; card.Block = 0; }

        bool modeled = card.Damage > 0 || card.Block > 0 || card.StrengthGain != 0 || card.ApplyVulnerable > 0
                       || card.ApplyWeak > 0 || card.EnergyGain != 0 || card.Dynamic != TurnSim.Dyn.None;
        if (!modeled)
        {
            if (DebugLog.Enabled) DebugLog.Debug($"turnsim: no modeled effect for card '{name}' (type {cm.Type}) — excluded (worst case)");
            return null;   // worst case: contributes nothing
        }
        return card;
    }

    private static int ReadHits(CardModel card)
    {
        try
        {
            if (card.DynamicVars.TryGetValue("CalculatedHits", out var v) && v is CalculatedVar cv)
            {
                int h = (int)cv.Calculate(null);
                return h < 1 ? 1 : h;
            }
        }
        catch { }
        return HitCountRegistry.HitsOf(card);
    }

    private static TurnSim.Tgt EnemyTgt(TargetType tt) => tt == TargetType.AllEnemies ? TurnSim.Tgt.AllEnemies : TurnSim.Tgt.OneEnemy;

    private static bool Has(Creature c, string power) { foreach (var p in c.Powers) if (p.GetType().Name == power) return true; return false; }
    private static int PowerAmount(Creature c, string power) { foreach (var p in c.Powers) if (p.GetType().Name == power) return p.Amount; return 0; }
    private static PowerModel? FindPower(Creature c, string power) { foreach (var p in c.Powers) if (p.GetType().Name == power) return p; return null; }
}
