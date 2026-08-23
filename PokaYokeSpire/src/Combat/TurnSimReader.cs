using System.Collections.Generic;
using System.Linq;
using MegaCrit.Sts2.Core.Combat;                 // CombatState, CombatSide, CombatManager
using MegaCrit.Sts2.Core.Combat.History.Entries; // CardExhaustedEntry
using MegaCrit.Sts2.Core.Context;                // LocalContext
using MegaCrit.Sts2.Core.Entities.Cards;         // CostModifiers, TargetType, CardType, CardKeyword
using MegaCrit.Sts2.Core.Entities.Creatures;     // Creature
using MegaCrit.Sts2.Core.Entities.Players;       // Player
using MegaCrit.Sts2.Core.Localization.DynamicVars; // CalculatedVar
using MegaCrit.Sts2.Core.Models;                 // CardModel
using MegaCrit.Sts2.Core.MonsterMoves.Intents;   // AttackIntent
using MegaCrit.Sts2.Core.ValueProps;             // ValueProp (enchant hooks)
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
            Vigor = PowerAmount(meC, "VigorPower"),      // Akabeko/Patter: flat bonus to your next attack
            BlockPerAttack = PowerAmount(meC, "RagePower"),   // Rage: block gained per Attack played this turn
            BlockPerCardPlayed = PowerAmount(meC, "AfterimagePower"),   // block on every card played
            BlockPerExhaust = PowerAmount(meC, "FeelNoPainPower"),      // block per card exhausted
            FirstAttackBonusPct = PowerAmount(meC, "LethalityPower"),   // first attack each turn +this %
            IncomingMultPct = Has(meC, "TankPower") ? 200 : 0,          // Tank: incoming ×2
            WeakTargetMult = PowerAmount(meC, "TrackingPower"),         // Tracking: ×vs Weak enemies
            VulnBonusPct = PowerAmount(meC, "CrueltyPower"),            // Cruelty: +Vulnerable %
            DamageOnBlockGain = PowerAmount(meC, "JuggernautPower"),    // Juggernaut
            DamagePerCard = PowerAmount(meC, "SerpentFormPower"),       // Serpent Form
            StrOnHpLoss = PowerAmount(meC, "RupturePower"),             // Rupture
            BlockOnExpensiveCard = PowerAmount(meC, "DanseMacabrePower"),  // Danse Macabre
            BufferHits = PowerAmount(meC, "BufferPower"),               // Buffer
            EnemyStrDownOnHit = PowerAmount(meC, "MonarchsGazePower"),  // Monarch's Gaze
            PanacheDmg = PowerAmount(meC, "PanachePower"),              // Panache
            DamageOnDebuff = PowerAmount(meC, "SleightOfFleshPower"),   // Sleight of Flesh
            EchoCards = PowerAmount(meC, "EchoFormPower"),              // Echo Form
            DefendBlockBonus = PowerAmount(meC, "FastenPower"),         // Fasten
            ShivDamageBonus = PowerAmount(meC, "AccuracyPower"),        // Accuracy
            FirstShivBonus = PowerAmount(meC, "PhantomBladesPower"),    // Phantom Blades
            StrOnColorless = PowerAmount(meC, "ArsenalPower"),          // Arsenal
            BlockOnEthereal = PowerAmount(meC, "SpiritOfAshPower"),     // Spirit of Ash
            BlockOnDoomApplied = PowerAmount(meC, "ShroudPower"),       // Shroud
            HalveVulnerableEnemyDamage = Has(meC, "ColossusPower"),     // Colossus: half from Vulnerable attackers
        };
        ReadMitigationRelics(mePlayer, ref snap.Player);
        // Seed "a card was exhausted this turn" (Evil Eye) and Unmovable's remaining block-doubles from combat
        // history — the sim also updates both as it plays, but events BEFORE this snapshot only show here.
        try
        {
            var hist = CombatManager.Instance?.History;
            if (hist != null)
            {
                snap.Player.ExhaustedThisTurn = hist.Entries.OfType<CardExhaustedEntry>().Any(e => e.HappenedThisTurn(state) && e.Actor == meC);
                int unmovable = PowerAmount(meC, "UnmovablePower");   // doubles your first N block-gaining cards each turn
                if (unmovable > 0)
                {
                    int blockCardsThisTurn = hist.Entries.OfType<BlockGainedEntry>().Count(e => e.HappenedThisTurn(state) && e.Actor == meC && e.Props.IsCardOrMonsterMove());
                    snap.Player.DoubleNextBlockCards = System.Math.Max(0, unmovable - blockCardsThisTurn);
                }
            }
        }
        catch { }
        FlagUnmodeledPowers(meC, "player");

        // ── enemies ──
        var enemies = new List<TurnSim.Enemy>();
        foreach (var e in state.Enemies)
        {
            if (e.CurrentHp <= 0) continue;
            var (dmg, hits) = ReadIntent(e);
            var hardShell = FindPower(e, "HardenedShellPower");   // caps HP damage taken this turn (per-turn total)
            var hardToKill = FindPower(e, "HardToKillPower");     // Exoskeleton: caps EACH hit (per-hit)
            var curlUp = FindPower(e, "CurlUpPower");             // gains block after first hit
            var enemyBuffer = FindPower(e, "BufferPower");        // negates your next N hits

            // Per-hit cap: Hard to Kill (Amount), Slippery / Intangible (→1). Take the tightest.
            int perHitCap = hardToKill?.Amount ?? 0;
            if (Has(e, "SlipperyPower") || Has(e, "IntangiblePower"))
                perHitCap = perHitCap == 0 ? 1 : System.Math.Min(perHitCap, 1);

            // Damage-taken multiplier: Soar/Flutter/Guarded halve (×0.5); Colossus halves only vs a Vulnerable
            // player. Stack multiplicatively. 100% ⇒ leave as 0 ("none").
            int dtPct = 100;
            if (Has(e, "SoarPower")) dtPct = dtPct * 50 / 100;
            if (Has(e, "FlutterPower")) dtPct = dtPct * 50 / 100;
            if (Has(e, "GuardedPower")) dtPct = dtPct * 50 / 100;

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
                PerHitCap = perHitCap,                          // each hit capped to this
                DamageTakenPct = dtPct == 100 ? 0 : dtPct,      // % of damage this enemy takes
                BlockOnFirstHit = curlUp?.Amount ?? 0,          // Curl Up
                CurlUpArmed = curlUp != null,
                BufferHits = enemyBuffer?.Amount ?? 0,          // enemy Buffer
            });
            snap.EnemyRefs.Add(e);
            FlagUnmodeledPowers(e, "enemy");
        }
        if (enemies.Count == 0) return null;
        snap.Enemies = enemies.ToArray();

        // Sneaky: gain block per enemy that attacks this turn — deterministic given their intents. This
        // mitigation is gained during the enemy turn, so it only reduces incoming (ReactiveBlock).
        int sneaky = PowerAmount(meC, "SneakyPower");
        if (sneaky > 0)
        {
            int attackers = 0;
            foreach (var en in snap.Enemies) if (en.IntentDamage > 0) attackers++;
            snap.Player.ReactiveBlock = sneaky * attackers;
        }

        // ── hand ──
        int endTurnUnblockable = 0, endTurnBlockable = 0;
        bool corruption = Has(meC, "CorruptionPower");   // Skills cost 0 and exhaust
        foreach (var cm in pcs.Hand.Cards)
        {
            try
            {
                // status/curse cards that hurt you at end of turn while in hand (Burn 2, Toxic 5,
                // Infection 3, BadLuck 13, Beckon 6, Decay 2, Regret = hand size, …). DamageVar is
                // blockable and HpLossVar is unblockable, but both are modeled conservatively as HP lost.
                if (cm.HasTurnEndInHandEffect)
                {
                    if (cm.GetType().Name == "Regret") endTurnUnblockable += pcs.Hand.Cards.Count;   // lose HP = cards in hand (unblockable)
                    else foreach (var v in cm.DynamicVars.Values)
                    {
                        var vn = v.GetType().Name;
                        if (vn == "DamageVar") { endTurnBlockable += (int)v.BaseValue; break; }     // Burn/Decay/Toxic — blockable
                        if (vn == "HpLossVar") { endTurnUnblockable += (int)v.BaseValue; break; }   // BadLuck/Beckon — unblockable
                    }
                }

                var card = ReadCard(cm);
                if (card != null)
                {
                    if (corruption && cm.Type == CardType.Skill) { card.Cost = 0; card.Exhausts = true; }   // Corruption
                    snap.Hand.Add(card);
                }
            }
            catch { /* unreadable card -> excluded (worst case) */ }
        }
        snap.Player.EndTurnSelfDamage = endTurnUnblockable;
        snap.Player.EndTurnSelfDamageBlockable = endTurnBlockable;
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

    /// Read a single live card into the sim's value model. Exposed so the self-audit can convert cards that
    /// are DRAWN mid-turn and fold them into an end-of-turn re-solve (the union of everything revealed).
    internal static TurnSim.Card? ReadCard(CardModel cm)
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
            foreach (var v in cm.DynamicVars.Values) if (v is DamageVar dv) { xdmg = ApplyDamageEnchant(cm, dv.BaseValue, dv.Props); break; }
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
                case "DamageVar": card.Damage = ApplyDamageEnchant(cm, ((DamageVar)v).BaseValue, ((DamageVar)v).Props); break;
                case "BlockVar": card.Block = (int)((BlockVar)v).EnchantedValue; break;   // EnchantedValue folds in any block enchant (game-computed)
                case "EnergyVar": card.EnergyGain = (int)v.BaseValue; break;
                case "HpLossVar": card.SelfDamageOnPlay += (int)v.BaseValue; break;   // Hemokinesis/Offering/Bloodletting: unblockable HP cost
                default:
                    if (t.IsGenericType && t.Name.StartsWith("PowerVar"))
                    {
                        string pt = t.GetGenericArguments()[0].Name;
                        if (pt == "StrengthPower") card.StrengthGain += (int)v.BaseValue;
                        else if (pt == "DexterityPower") card.DexterityGain += (int)v.BaseValue;   // Footwork/Prowess
                        else if (pt == "VigorPower") card.VigorGain += (int)v.BaseValue;           // Patter/PrepTime
                        else if (pt == "IntangiblePower") card.GrantIntangible = true;             // Apparition/Wraith Form
                        else if (pt == "VulnerablePower") { card.ApplyVulnerable = (int)v.BaseValue; card.VulnTarget = EnemyTgt(cm.TargetType); }
                        else if (pt == "WeakPower") { card.ApplyWeak = (int)v.BaseValue; card.WeakTarget = EnemyTgt(cm.TargetType); }
                        else if (pt == "DoomPower") { card.ApplyDoom = (int)v.BaseValue; card.DoomTarget = EnemyTgt(cm.TargetType); }   // Oblivion/End of Days execute
                        else if (pt == "PlatingPower" || pt == "MetallicizePower") card.FlatBlock += (int)v.BaseValue;   // block at end of THIS turn
                        else if (DebugLog.Enabled) DebugLog.Debug($"turnsim: card '{name}' applies UNMODELED power {pt} (amount {(int)v.BaseValue}) — its effect on the gems is ignored (worst case)");
                    }
                    break;
            }
        }

        // Enemy Strength loss (Piercing Wail, Crush Under, Dark Shackles, Mangle, …): a plain-keyed var.
        if (cm.DynamicVars.TryGetValue("StrengthLoss", out var slv))
        {
            card.EnemyStrengthLoss = (int)slv.BaseValue;
            card.EStrTarget = EnemyTgt(cm.TargetType);
        }

        // Calculated-damage cards (Perfected Strike, Rend, Conflagration, …) expose no plain DamageVar —
        // read the game's own calculation as a snapshot ("as if played now"; deck-static ones like Perfected
        // Strike are exact, turn-history ones are a conservative floor). Body Slam stays a Dyn (below).
        if (card.Damage == 0 && name != "BodySlam"
            && cm.DynamicVars.TryGetValue("CalculatedDamage", out var cdv) && cdv is CalculatedVar cdc)
        {
            try { decimal d = cdc.Calculate(null); if (d > 0) card.Damage = cdv is DamageVar cdd ? ApplyDamageEnchant(cm, d, cdd.Props) : (int)d; } catch { }
        }

        if (card.Damage > 0)
        {
            card.AttackTarget = EnemyTgt(cm.TargetType);
            card.Hits = ReadHits(cm);
        }
        card.Exhausts = cm.Keywords.Contains(CardKeyword.Exhaust);
        card.IsDefend = cm.Tags.Contains(CardTag.Defend);       // Fasten
        card.IsShiv = cm.Tags.Contains(CardTag.Shiv);           // Accuracy / Phantom Blades
        card.IsEthereal = cm.Keywords.Contains(CardKeyword.Ethereal);   // Spirit of Ash
        try { card.IsColorless = cm.VisualCardPool?.IsColorless ?? false; } catch { }   // Arsenal

        // bespoke, state-dependent cards (behaviour lives in OnPlay; detect by class name)
        if (name == "BodySlam") { card.Dynamic = TurnSim.Dyn.BodySlam; card.AttackTarget = TurnSim.Tgt.OneEnemy; }
        else if (name == "SecondWind") { card.Dynamic = TurnSim.Dyn.SecondWind; card.DynParam = card.Block; card.Block = 0; }
        else if (name == "Entrench") card.Dynamic = TurnSim.Dyn.Entrench;   // double current block
        else if (name == "Rage" && cm.DynamicVars.TryGetValue("Power", out var rageV)) card.GrantBlockPerAttack = (int)rageV.BaseValue;   // block per attack this turn
        else if (name == "EvilEye") card.DoubleBlockIfExhausted = true;   // its 8 block is doubled if a card was exhausted this turn
        else if (name == "DemonicShield") card.Dynamic = TurnSim.Dyn.Entrench;   // gain block = current block (doubles); −1 HP via HpLossVar
        else if (name == "Expose") card.RemoveEnemyBlock = true;                 // sets target's Block to 0 (+ Vulnerable via PowerVar)
        else if (name == "Dismantle") card.DoubleHitsIfTargetVulnerable = true;  // hits twice if the target is Vulnerable
        else if (name == "Resonance") { card.EnemyStrengthLoss = 1; card.EStrTarget = TurnSim.Tgt.AllEnemies; }   // all enemies −1 Strength (self +Str via PowerVar)
        else if (name == "FightMe") { card.EnemyStrengthLoss = -1; card.EStrTarget = TurnSim.Tgt.OneEnemy; }       // target GAINS 1 Strength (a downside)

        // Enchant damage/block is now folded into card.Damage/Block above; other enchant effects
        // (Corrupted's on-play self-damage, replay-count enchants like Clone, on-play hooks) are NOT
        // modeled — log them so the gap is visible from real play.
        if (cm.Enchantment != null && DebugLog.Enabled)
            DebugLog.Debug($"turnsim: card '{name}' enchanted with {cm.Enchantment.GetType().Name} — damage/block enchant applied; any replay/on-play enchant effect is not modeled");

        bool modeled = card.Damage > 0 || card.Block > 0 || card.FlatBlock != 0 || card.StrengthGain != 0
                       || card.DexterityGain != 0 || card.VigorGain != 0 || card.GrantBlockPerAttack != 0 || card.EnemyStrengthLoss != 0
                       || card.ApplyVulnerable > 0 || card.ApplyWeak > 0 || card.EnergyGain != 0 || card.Dynamic != TurnSim.Dyn.None
                       || card.SelfDamageOnPlay != 0 || card.GrantIntangible || card.RemoveEnemyBlock || card.ApplyDoom > 0 || card.DoubleHitsIfTargetVulnerable;
        if (!modeled)
        {
            if (DebugLog.Enabled) DebugLog.Debug($"turnsim: no modeled effect for card '{name}' (type {cm.Type}) — excluded (worst case)");
            return null;   // worst case: contributes nothing
        }
        return card;
    }

    // Enchantments (Favored = ×2 damage, Corrupted = ×1.5, Sharp = +damage, …) modify a card's damage/
    // block MULTIPLICATIVELY/additively at preview time — they live in DamageVar.EnchantedValue, NOT in
    // BaseValue. Apply the game's OWN enchant hooks (with the var's real Props, so IsPoweredAttack-gated
    // enchants like Favored resolve correctly) so the sim sees the true value. My sim still applies
    // Strength/Weak/Vulnerable/Vigor itself, so we deliberately do NOT run the global hooks here.
    private static int ApplyDamageEnchant(CardModel cm, decimal baseNum, ValueProp props)
    {
        try
        {
            var ench = cm.Enchantment;
            if (ench != null)
            {
                baseNum += ench.EnchantDamageAdditive(baseNum, props);
                baseNum *= ench.EnchantDamageMultiplicative(baseNum, props);
            }
        }
        catch { }
        return (int)baseNum;
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

    // Powers whose effect on this-turn damage/block/mitigation the sim already accounts for. Any OTHER
    // power a creature carries is logged (once-ish, via DebugLog) so genuine coverage gaps are visible
    // instead of silently wrong — this is how the card/power/relic model closes from real play.
    private static readonly HashSet<string> ModeledPowers = new()
    {
        "StrengthPower", "DexterityPower", "WeakPower", "FrailPower", "VulnerablePower",
        "ShrinkPower", "IntangiblePower", "HardenedShellPower", "HardToKillPower", "PlatingPower", "MetallicizePower",
        "VigorPower", "RagePower", "SlipperyPower", "SoarPower", "FlutterPower", "GuardedPower", "ColossusPower",
        "UnmovablePower", "AfterimagePower", "FeelNoPainPower", "LethalityPower", "TankPower", "CorruptionPower",
        "TrackingPower", "CrueltyPower", "JuggernautPower", "SerpentFormPower", "RupturePower", "DanseMacabrePower",
        "BufferPower", "MonarchsGazePower", "PanachePower", "SleightOfFleshPower", "EchoFormPower", "FastenPower",
        "AccuracyPower", "PhantomBladesPower", "ArsenalPower", "SpiritOfAshPower", "ShroudPower", "CurlUpPower",
        "SneakyPower", "ThornsPower", "FlameBarrierPower",
    };

    /// Relics that change how much HP you lose to enemy attacks this turn. Auto Strength/Dex/Plating/Vigor
    /// relics need no handling here — they apply a POWER to the player, so the snapshot already reads them.
    private static void ReadMitigationRelics(Player me, ref TurnSim.Player p)
    {
        try
        {
            foreach (var relic in me.Relics)
            {
                switch (relic.GetType().Name)
                {
                    case "TungstenRod":     p.HpLossReductionPerHit += 1; break;   // −1 per instance of HP loss
                    case "BeatingRemnant":  p.MaxHpLossThisTurn = 20; break;        // cap total HP lost this turn
                }
            }
        }
        catch { }
    }

    private static void FlagUnmodeledPowers(Creature c, string who)
    {
        if (!DebugLog.Enabled) return;
        foreach (var p in c.Powers)
        {
            string pn = p.GetType().Name;
            if (!ModeledPowers.Contains(pn))
                DebugLog.Debug($"turnsim: {who} carries UNMODELED power {pn} (amount {p.Amount}) — not reflected in the gems (worst case)");
        }
    }
}
