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
    public void Defense_KillingTheAttacker_RemovesItsDamage()
    {
        // one enemy attacks for 20 but has only 6 HP; killing it (Strike 6) drops incoming to 0.
        var r = TurnSim.Solve(P(1), new[] { E(6, intent: 20), E(100) }, new List<Card> { Strike(6) });
        Assert.Equal(0, r.MinHpLost);
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
    public void DoNothing_IsAlwaysAnOption()
    {
        // with no useful cards, offense is 0 and you take the full hit.
        var r = TurnSim.Solve(P(0), new[] { E(100, intent: 12) }, new List<Card> { Strike(6) /* unaffordable */ });
        Assert.Equal(0, r.MaxDamage);
        Assert.Equal(12, r.MinHpLost);
    }
}
