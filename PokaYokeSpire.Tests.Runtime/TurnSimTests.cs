using System.Collections.Generic;
using Xunit;
using PokaYokeSpire.Combat;
using Card = PokaYokeSpire.Combat.TurnSim.Card;
using Tgt = PokaYokeSpire.Combat.TurnSim.Tgt;
using Dyn = PokaYokeSpire.Combat.TurnSim.Dyn;

namespace PokaYokeSpire.Tests.Runtime;

/// <summary>
/// The forward turn-simulator — the cases that broke the old ad-hoc solvers: Vulnerable a card applies
/// boosts LATER attacks (Bash→Strike), anti-synergy where a card consumes a shared resource (Second
/// Wind after Defend), block-scaled damage (Body Slam), your own Weak lowering your damage, and defense
/// crediting killing/Weakening an attacker — not just block.
/// </summary>
public class TurnSimTests
{
    private static TurnSim.Player P(int energy, int str = 0, int weak = 0, int dex = 0, bool frail = false)
        => new() { Energy = energy, Strength = str, Weak = weak, Dexterity = dex, Frail = frail ? 1 : 0 };

    private static TurnSim.Enemy E(int hp, int intent = 0, int hits = 1, int vuln = 0, int weak = 0)
        => new() { Hp = hp, IntentDamage = intent, IntentHits = hits, Vulnerable = vuln, Weak = weak };

    private static Card Strike(int dmg = 6) => new() { Name = "Strike", Cost = 1, Damage = dmg, AttackTarget = Tgt.OneEnemy };
    private static Card Defend(int blk = 5) => new() { Name = "Defend", Cost = 1, Block = blk };
    private static Card Bash() => new() { Name = "Bash", Cost = 2, Damage = 8, AttackTarget = Tgt.OneEnemy, ApplyVulnerable = 2, VulnTarget = Tgt.OneEnemy };

    [Fact]
    public void Bash_Then_Strike_CreditsVulnerable_OrderMatters()
    {
        // Bash 8 (enemy not yet Vulnerable), then Strike 6 ×1.5 = 9  →  17. Strike-first would be 14.
        var r = TurnSim.Solve(P(3), new[] { E(100) }, new List<Card> { Bash(), Strike() });
        Assert.Equal(17, r.MaxDamage);
    }

    [Fact]
    public void SecondWind_AfterDefend_HasNothingToExhaust_NoDoubleCount()
    {
        // Defend(5) + Second Wind(5/card). Playing both can only reach 5 block, never 10 — whichever you
        // play first denies the other. So the enemy's 20 leaves 15 through (not 10).
        var secondWind = new Card { Name = "SecondWind", Cost = 1, Dynamic = Dyn.SecondWind, DynParam = 5 };
        var r = TurnSim.Solve(P(3), new[] { E(100, intent: 20) }, new List<Card> { Defend(5), secondWind });
        Assert.Equal(15, r.MinHpLost);        // 20 incoming − 5 best block
        Assert.NotEqual(10, r.MinHpLost);     // the old additive knapsack's wrong answer
    }

    [Fact]
    public void BodySlam_ScalesWithBlockGainedThisTurn()
    {
        var bodySlam = new Card { Name = "BodySlam", Cost = 1, Dynamic = Dyn.BodySlam, AttackTarget = Tgt.OneEnemy };
        var r = TurnSim.Solve(P(2), new[] { E(100) }, new List<Card> { Defend(5), bodySlam });
        Assert.Equal(5, r.MaxDamage);         // Defend → 5 block → Body Slam deals 5
    }

    [Fact]
    public void PlayerWeak_LowersYourDamage()
    {
        var r = TurnSim.Solve(P(1, weak: 1), new[] { E(100) }, new List<Card> { Strike(6) });
        Assert.Equal(4, r.MaxDamage);         // 6 × 0.75 = 4
    }

    [Fact]
    public void PlayerStrength_AddsPerHit()
    {
        var twin = new Card { Name = "TwinStrike", Cost = 1, Damage = 5, Hits = 2, AttackTarget = Tgt.OneEnemy };
        var r = TurnSim.Solve(P(1, str: 3), new[] { E(100) }, new List<Card> { twin });
        Assert.Equal(16, r.MaxDamage);        // (5+3) × 2 hits
    }

    [Fact]
    public void Defense_KillingTheAttacker_IsNotCredited()
    {
        // defense is pure MITIGATION: even though Strike 6 would kill the 6-HP attacker, that's offense,
        // not defense — so the defense min still counts its full 20 incoming (a Strike isn't defense).
        var r = TurnSim.Solve(P(1), new[] { E(6, intent: 20), E(100) }, new List<Card> { Strike(6) });
        Assert.Equal(20, r.MinHpLost);
    }

    [Fact]
    public void Defense_WeakeningTheAttacker_ReducesIncoming()
    {
        var legSweep = new Card { Name = "LegSweep", Cost = 2, ApplyWeak = 1, WeakTarget = Tgt.OneEnemy, Block = 0 };
        var r = TurnSim.Solve(P(2), new[] { E(100, intent: 20) }, new List<Card> { legSweep });
        Assert.Equal(15, r.MinHpLost);        // 20 × 0.75 = 15
    }

    [Fact]
    public void Defense_BlockPlusEndOfTurn_AlreadyInPlayerBlock()
    {
        // player already carries 6 block (e.g. Plating precomputed by the reader); Defend adds 5 → 11.
        var r = TurnSim.Solve(new TurnSim.Player { Energy = 1, Block = 6 }, new[] { E(100, intent: 20) },
                              new List<Card> { Defend(5) });
        Assert.Equal(9, r.MinHpLost);         // 20 − 11
    }

    [Fact]
    public void IdenticalEnemies_PerEnemyMax_IsSymmetric_AfterPruning()
    {
        // 5 Strength-gains + 5 damage-2 strikes vs three identical 100-HP enemies. +5 Str ⇒ each hit
        // does 7, five hits = 35 focusable on ANY single enemy. The target-symmetry prune + root-group
        // copy must still give all three the same (correct) per-enemy max.
        var hand = new List<Card>();
        for (int i = 0; i < 5; i++) hand.Add(new Card { Cost = 0, StrengthGain = 1 });
        for (int i = 0; i < 5; i++) hand.Add(new Card { Cost = 0, Damage = 2, AttackTarget = Tgt.OneEnemy });
        var r = TurnSim.Solve(new TurnSim.Player { Energy = 0 }, new[] { E(100), E(100), E(100) }, hand);
        Assert.Equal(35, r.MaxPerEnemy[0]);
        Assert.Equal(35, r.MaxPerEnemy[1]);
        Assert.Equal(35, r.MaxPerEnemy[2]);
        Assert.Equal(35, r.MaxDamage);   // only 35 total damage exists this turn
    }

    [Fact]
    public void CanKillAll_SpreadsAcrossIdenticalMinions_DespiteTargetPrune()
    {
        // three 6-HP minions, three 6-damage strikes → clear the room by SPREADING; the symmetric-target
        // prune must not prevent finding the spread (hit enemies diverge and become targetable again).
        var r = TurnSim.Solve(P(3), new[] { E(6), E(6), E(6) }, new List<Card> { Strike(6), Strike(6), Strike(6) });
        Assert.True(r.CanKillAll);
    }

    [Fact]
    public void HardenedShell_CapsDamageThisTurn()
    {
        // boss takes at most 20 HP damage this turn no matter what you throw at it.
        var e = new TurnSim.Enemy { Hp = 1000, Capped = true, CapRemaining = 20 };
        var r = TurnSim.Solve(P(2), new[] { e }, new List<Card> { Strike(30), Strike(30) });
        Assert.Equal(20, r.MaxDamage);
        Assert.Equal(20, r.MaxPerEnemy[0]);
    }

    [Fact]
    public void HardToKill_CapsEachHit_NotPerTurnTotal()
    {
        // Exoskeleton's Hard to Kill 9: each hit is capped at 9 (no depletion). Two big Strikes → 9+9=18,
        // NOT capped to a single 9 (that's Hardened Shell). A multi-hit under 9 per hit is unaffected.
        var exo = new TurnSim.Enemy { Hp = 100, PerHitCap = 9 };
        var big = TurnSim.Solve(P(2), new[] { exo }, new List<Card> { Strike(30), Strike(30) });
        Assert.Equal(18, big.MaxDamage);

        var small = new TurnSim.Enemy { Hp = 100, PerHitCap = 9 };
        var twin = new Card { Name = "TwinStrike", Cost = 1, Damage = 5, Hits = 2, AttackTarget = Tgt.OneEnemy };
        var r = TurnSim.Solve(P(1), new[] { small }, new List<Card> { twin });
        Assert.Equal(10, r.MaxDamage);   // 5 and 5 both under the 9 cap
    }

    [Fact]
    public void DamageTakenPct_HalvesDamage_AndStacksAfterVulnerable()
    {
        // Soar/Flutter/Guarded: enemy takes 50%. Strike 10 → 5.
        var flier = new TurnSim.Enemy { Hp = 100, DamageTakenPct = 50 };
        var r = TurnSim.Solve(P(1), new[] { flier }, new List<Card> { Strike(10) });
        Assert.Equal(5, r.MaxDamage);

        // With Vulnerable the ×1.5 applies first (in Atk), then the ×0.5: 10 → 15 → 7.
        var vf = new TurnSim.Enemy { Hp = 100, Vulnerable = 1, DamageTakenPct = 50 };
        var r2 = TurnSim.Solve(P(1), new[] { vf }, new List<Card> { Strike(10) });
        Assert.Equal(7, r2.MaxDamage);
    }

    [Fact]
    public void Whirlwind_XCost_HitsAllEnemiesEnergyTimes()
    {
        var whirl = new Card { Name = "Whirlwind", Cost = 0, XCost = true, Damage = 5, AttackTarget = Tgt.AllEnemies };
        var r = TurnSim.Solve(P(3), new[] { E(100), E(100) }, new List<Card> { whirl });
        Assert.Equal(15, r.MaxPerEnemy[0]);   // 3 energy → 3 hits × 5
        Assert.Equal(15, r.MaxPerEnemy[1]);
        Assert.Equal(30, r.MaxDamage);
    }

    [Fact]
    public void EndTurnSelfDamage_Burn_AddsUnblockableToDefense()
    {
        // a Burn in hand adds 2 unblockable HP at end of turn, on top of the enemy attack (after block).
        var p = new TurnSim.Player { Energy = 1, EndTurnSelfDamage = 2 };
        var r = TurnSim.Solve(p, new[] { E(100, intent: 10) }, new List<Card> { Defend(5) });
        Assert.Equal(7, r.MinHpLost);   // (10 − 5 block) + 2 burn
    }

    [Fact]
    public void Intangible_CapsEachHitToOne()
    {
        // two enemies attacking 20 (×1 hit) and 8 (×2 hits); Intangible → each hit is 1 → 1 + 2 = 3.
        var p = new TurnSim.Player { Energy = 0, Intangible = true };
        var r = TurnSim.Solve(p, new[] { E(100, intent: 20, hits: 1), E(100, intent: 8, hits: 2) }, new List<Card>());
        Assert.Equal(3, r.MinHpLost);
    }

    [Fact]
    public void PlatingFromCard_ReducesIncomingThisTurn()
    {
        // Eternal Armor applies Plating 7 → 7 block at end of THIS turn (Unpowered, no Dex/Frail).
        var eternalArmor = new Card { Name = "EternalArmor", Cost = 3, FlatBlock = 7 };
        var p = new TurnSim.Player { Energy = 3, Dexterity = 3, Frail = 1 };   // Dex/Frail must NOT touch it
        var r = TurnSim.Solve(p, new[] { E(100, intent: 20) }, new List<Card> { eternalArmor });
        Assert.Equal(13, r.MinHpLost);   // 20 − 7 flat plating
    }

    [Fact]
    public void Vigor_BoostsOnlyYourNextAttack_ThenConsumed()
    {
        // Vigor 3: first Strike does 6+3=9, Vigor is spent, second Strike does 6 → 15 (not 18).
        var p = new TurnSim.Player { Energy = 2, Vigor = 3 };
        var r = TurnSim.Solve(p, new[] { E(100) }, new List<Card> { Strike(6), Strike(6) });
        Assert.Equal(15, r.MaxDamage);
    }

    [Fact]
    public void EnemyStrengthLoss_SoftensIncoming()
    {
        // Piercing Wail-style: −6 Strength on the attacker → its 10 intent lands as 4.
        var wail = new Card { Name = "PiercingWail", Cost = 1, EnemyStrengthLoss = 6, EStrTarget = Tgt.OneEnemy };
        var r = TurnSim.Solve(P(1), new[] { E(100, intent: 10) }, new List<Card> { wail });
        Assert.Equal(4, r.MinHpLost);
    }

    [Fact]
    public void DexterityGain_BoostsLaterBlockCardsThisTurn()
    {
        // Footwork (+2 Dex) then Defend(5) → 7 block. 20 incoming leaves 13.
        var footwork = new Card { Name = "Footwork", Cost = 1, DexterityGain = 2 };
        var r = TurnSim.Solve(P(2), new[] { E(100, intent: 20) }, new List<Card> { footwork, Defend(5) });
        Assert.Equal(13, r.MinHpLost);
    }

    [Fact]
    public void Entrench_DoublesCurrentBlock()
    {
        // Defend(5) → 5, Entrench → 10. Playing Entrench first (doubling 0) is dominated; min = 10.
        var entrench = new Card { Name = "Entrench", Cost = 1, Dynamic = Dyn.Entrench };
        var r = TurnSim.Solve(P(2), new[] { E(100, intent: 20) }, new List<Card> { Defend(5), entrench });
        Assert.Equal(10, r.MinHpLost);
    }

    [Fact]
    public void Rage_ActivePower_GainsBlockPerAttackPlayed()
    {
        // Rage already active (3 block per attack): two Strikes → 6 block. 20 incoming leaves 14.
        var p = new TurnSim.Player { Energy = 2, BlockPerAttack = 3 };
        var r = TurnSim.Solve(p, new[] { E(100, intent: 20) }, new List<Card> { Strike(6), Strike(6) });
        Assert.Equal(14, r.MinHpLost);
    }

    [Fact]
    public void Rage_CardThenAttacks_ArmsBlockPerAttack()
    {
        // Play Rage (0-cost skill, arms 3 block/attack) then two Strikes → 6 block; 20 incoming leaves 14.
        var rage = new Card { Name = "Rage", Cost = 0, GrantBlockPerAttack = 3 };
        var r = TurnSim.Solve(P(2), new[] { E(100, intent: 20) }, new List<Card> { rage, Strike(6), Strike(6) });
        Assert.Equal(14, r.MinHpLost);
    }

    [Fact]
    public void EvilEye_NoExhaust_GivesBaseBlock()
    {
        var evilEye = new Card { Name = "EvilEye", Cost = 1, Block = 8, DoubleBlockIfExhausted = true };
        var r = TurnSim.Solve(P(1), new[] { E(100, intent: 20) }, new List<Card> { evilEye });
        Assert.Equal(12, r.MinHpLost);   // 20 − 8
    }

    [Fact]
    public void EvilEye_DoublesWhenAnExhaustAttackIsPlayedFirst()
    {
        // The exhaust trigger is an ATTACK, so Evil Eye (a non-attack) must be played AFTER it — the case
        // the T3 setup-before-payload prune would normally forbid. Relaxing it lets the double be found.
        var exStrike = new Card { Name = "ExhaustStrike", Cost = 1, Damage = 6, AttackTarget = Tgt.OneEnemy, Exhausts = true };
        var evilEye = new Card { Name = "EvilEye", Cost = 1, Block = 8, DoubleBlockIfExhausted = true };
        var r = TurnSim.Solve(P(2), new[] { E(100, intent: 20) }, new List<Card> { exStrike, evilEye });
        Assert.Equal(4, r.MinHpLost);    // 20 − 16 (doubled)
    }

    [Fact]
    public void EvilEye_DoublesWhenExhaustAlreadyHappenedThisTurn()
    {
        var p = new TurnSim.Player { Energy = 1, ExhaustedThisTurn = true };   // seeded from history
        var evilEye = new Card { Name = "EvilEye", Cost = 1, Block = 8, DoubleBlockIfExhausted = true };
        var r = TurnSim.Solve(p, new[] { E(100, intent: 20) }, new List<Card> { evilEye });
        Assert.Equal(4, r.MinHpLost);    // 20 − 16
    }

    [Fact]
    public void TungstenRod_ReducesEachHitByOne()
    {
        // 8 damage ×2 hits, −1 per hit → 7 ×2 = 14 (not 16).
        var p = new TurnSim.Player { Energy = 0, HpLossReductionPerHit = 1 };
        var r = TurnSim.Solve(p, new[] { E(100, intent: 8, hits: 2) }, new List<Card>());
        Assert.Equal(14, r.MinHpLost);
    }

    [Fact]
    public void BeatingRemnant_CapsTurnHpLoss()
    {
        var p = new TurnSim.Player { Energy = 0, MaxHpLossThisTurn = 20 };
        var r = TurnSim.Solve(p, new[] { E(100, intent: 50) }, new List<Card>());
        Assert.Equal(20, r.MinHpLost);
    }

    [Fact]
    public void SelfDamageOnPlay_AddsUnblockableHpLoss()
    {
        // Blood Wall: −2 HP (unblockable) but +16 block. vs 20: 20−16 = 4, +2 self = 6 — still better than 20.
        var bloodWall = new Card { Name = "BloodWall", Cost = 1, Block = 16, SelfDamageOnPlay = 2 };
        var r = TurnSim.Solve(P(1), new[] { E(100, intent: 20) }, new List<Card> { bloodWall });
        Assert.Equal(6, r.MinHpLost);
    }

    [Fact]
    public void Doom_ExecutesEnemyAtOrBelowThreshold()
    {
        // Oblivion applies Doom 25 to a 20-HP enemy → it dies at end of turn (counts as killed), no damage dealt.
        var oblivion = new Card { Name = "Oblivion", Cost = 1, ApplyDoom = 25, DoomTarget = Tgt.OneEnemy };
        var r = TurnSim.Solve(P(1), new[] { E(20) }, new List<Card> { oblivion });
        Assert.True(r.CanKillAll);
        Assert.Equal(20, r.MaxPerEnemy[0]);
    }

    [Fact]
    public void Expose_RemovesEnemyBlock_SoAttacksLand()
    {
        // Enemy has 10 block; a lone Strike(6) would be fully absorbed. Expose strips the block first → 6 lands.
        var enemy = new TurnSim.Enemy { Hp = 100, Block = 10 };
        var expose = new Card { Name = "Expose", Cost = 1, RemoveEnemyBlock = true };
        var r = TurnSim.Solve(P(2), new[] { enemy }, new List<Card> { expose, Strike(6) });
        Assert.Equal(6, r.MaxDamage);
    }

    [Fact]
    public void Dismantle_HitsTwiceWhenTargetVulnerable()
    {
        var dismantle = new Card { Name = "Dismantle", Cost = 1, Damage = 8, Hits = 1, AttackTarget = Tgt.OneEnemy, DoubleHitsIfTargetVulnerable = true };
        var enemy = new TurnSim.Enemy { Hp = 100, Vulnerable = 1 };
        var r = TurnSim.Solve(P(1), new[] { enemy }, new List<Card> { dismantle });
        Assert.Equal(24, r.MaxDamage);   // 2 hits × floor(8 × 1.5) = 2 × 12
    }

    [Fact]
    public void Apparition_GrantsIntangible_CapsIncomingToOne()
    {
        var apparition = new Card { Name = "Apparition", Cost = 1, GrantIntangible = true };
        var r = TurnSim.Solve(P(1), new[] { E(100, intent: 20, hits: 1) }, new List<Card> { apparition });
        Assert.Equal(1, r.MinHpLost);
    }

    [Fact]
    public void Resonance_LowersAllEnemyStrength_SofteningIncoming()
    {
        var resonance = new Card { Name = "Resonance", Cost = 1, EnemyStrengthLoss = 1, EStrTarget = Tgt.AllEnemies };
        var r = TurnSim.Solve(P(1), new[] { E(100, intent: 10) }, new List<Card> { resonance });
        Assert.Equal(9, r.MinHpLost);   // 10 − 1 Strength
    }

    [Fact]
    public void TrivialFastPath_MatchesDfs()
    {
        // The lean analyzer routes order-independent single-enemy hands to the closed-form knapsack path.
        // It MUST agree with the exhaustive DFS on every metric — this is what licenses skipping the search.
        var rnd = new System.Random(1234567);
        for (int iter = 0; iter < 20000; iter++)
        {
            var p = new TurnSim.Player
            {
                Energy = rnd.Next(0, 8), Strength = rnd.Next(-2, 6), Dexterity = rnd.Next(-1, 4),
                Weak = rnd.Next(2), Frail = rnd.Next(2), Shrink = rnd.Next(2), Vulnerable = rnd.Next(2),
                Intangible = rnd.Next(6) == 0, HpLossReductionPerHit = rnd.Next(3) == 0 ? 1 : 0,
                MaxHpLossThisTurn = rnd.Next(4) == 0 ? rnd.Next(5, 25) : 0, EndTurnSelfDamage = rnd.Next(4) == 0 ? rnd.Next(1, 8) : 0,
            };
            var enemy = new TurnSim.Enemy
            {
                Hp = rnd.Next(1, 70), Block = rnd.Next(0, 14), Vulnerable = rnd.Next(2),
                IntentDamage = rnd.Next(0, 30), IntentHits = rnd.Next(1, 4),
            };
            var hand = new List<Card>();
            int cards = rnd.Next(0, 7);
            for (int k = 0; k < cards; k++)
                hand.Add(rnd.Next(2) == 0
                    ? new Card { Name = "A", Cost = rnd.Next(0, 4), Damage = rnd.Next(1, 15), Hits = rnd.Next(1, 4), AttackTarget = Tgt.OneEnemy }
                    : new Card { Name = "B", Cost = rnd.Next(0, 4), Block = rnd.Next(1, 15) });

            var fast = TurnSim.Solve(p, new[] { enemy }, hand);
            var dfs = TurnSim.SolveDfs(p, new[] { enemy }, hand);
            Assert.Equal(dfs.MaxDamage, fast.MaxDamage);
            Assert.Equal(dfs.MinHpLost, fast.MinHpLost);
            Assert.Equal(dfs.CanKillAll, fast.CanKillAll);
            Assert.Equal(dfs.MaxPerEnemy[0], fast.MaxPerEnemy[0]);
            Assert.Equal(0, fast.Nodes);   // proves the fast path (no search) was actually taken
        }
    }

    [Fact]
    public void OrderDependentHand_FallsBackToDfs()
    {
        // A hand with a scaling buff (Strength gain) is NOT trivial → must use the DFS (Nodes > 0),
        // and must get the order right (buff before attacks): +2 Str, then Strike 6 → 8.
        var buff = new Card { Name = "Flex", Cost = 0, StrengthGain = 2 };
        var r = TurnSim.Solve(P(1), new[] { E(100) }, new List<Card> { buff, Strike(6) });
        Assert.Equal(8, r.MaxDamage);
        Assert.True(r.Nodes > 0);
    }

    [Fact]
    public void DoNothing_IsAlwaysAnOption()
    {
        // with no useful cards, offense is 0 and you take the full hit.
        var r = TurnSim.Solve(P(0), new[] { E(100, intent: 12) }, new List<Card> { Strike(6) /* unaffordable */ });
        Assert.Equal(0, r.MaxDamage);
        Assert.Equal(12, r.MinHpLost);
    }
}
