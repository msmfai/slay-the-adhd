using System.Collections.Generic;
using PokaYokeSpire.Combat;
using Xunit;
using SC = PokaYokeSpire.Combat.LethalSolver.SimCard;

namespace PokaYokeSpire.Tests.Runtime;

public class LethalSolverTests
{
    private static LethalSolver.SimEnemy E(int hp, int block = 0, bool vuln = false)
        => new() { Hp = hp, Block = block, Vulnerable = vuln };
    private static List<LethalSolver.SimEnemy> Es(params LethalSolver.SimEnemy[] e) => new(e);
    private static List<SC> H(params SC[] c) => new(c);
    // helpers to build cards
    private static SC Atk(int cost, int dmg, int hits = 1, bool aoe = false, bool vuln = false) => new(cost, dmg, hits, aoe, 0, vuln);
    private static SC Str(int cost, int strength) => new(cost, 0, 0, false, strength, false);

    [Fact] public void PlainDamage_Kills()
        => Assert.True(LethalSolver.Solve(H(Atk(1, 6), Atk(1, 6)), energy: 2, startStrength: 0, Es(E(11))));

    [Fact] public void RespectsEnergy()
        => Assert.False(LethalSolver.Solve(H(Atk(1, 6), Atk(1, 6), Atk(1, 6)), energy: 2, startStrength: 0, Es(E(15))));

    [Fact] public void StrengthBuff_ThenAttacks_ChainsToLethal()
    {
        // Inflame (+2 str) then two 6-dmg Strikes = 8+8 = 16 >= 15 ; without the buff it's 12 < 15
        var hand = H(Str(1, 2), Atk(1, 6), Atk(1, 6));
        Assert.True(LethalSolver.Solve(hand, energy: 3, startStrength: 0, Es(E(15))));
        Assert.False(LethalSolver.Solve(H(Atk(1, 6), Atk(1, 6)), energy: 2, startStrength: 0, Es(E(15))));
    }

    [Fact] public void CurrentStrength_IsCounted()
        => Assert.True(LethalSolver.Solve(H(Atk(1, 6), Atk(1, 6)), energy: 2, startStrength: 3, Es(E(17)))); // (6+3)*2=18

    // ── max-damage (the offense orb's x value) ──

    [Fact] public void MaxDamage_SumsAffordableAttacks()
    {
        var r = LethalSolver.MaxDamage(H(Atk(1, 6), Atk(1, 6)), energy: 2, startStrength: 0, Es(E(20)));
        Assert.Equal(12, r.Total);
        Assert.Equal(12, r.PerEnemy[0]);
    }

    [Fact] public void MaxDamage_OverkillCappedAtHp()
    {
        var r = LethalSolver.MaxDamage(H(Atk(1, 10)), energy: 1, startStrength: 0, Es(E(5)));
        Assert.Equal(5, r.Total);           // can't "do" more than the 5 HP present
        Assert.Equal(5, r.PerEnemy[0]);
    }

    [Fact] public void MaxDamage_BlockAbsorbs()
    {
        var r = LethalSolver.MaxDamage(H(Atk(1, 8)), energy: 1, startStrength: 0, Es(E(20, block: 5)));
        Assert.Equal(3, r.Total);           // 8 - 5 block = 3 through
    }

    [Fact]
    public void MaxDamage_TwoEnemies_TotalSpreadsButPerEnemyFocuses()
    {
        // one 8-dmg single-target card, two enemies: total is 8 (one target), but EITHER enemy could
        // be the 8 — so each per-enemy max is 8.
        var r = LethalSolver.MaxDamage(H(Atk(1, 8)), energy: 1, startStrength: 0, Es(E(30), E(30)));
        Assert.Equal(8, r.Total);
        Assert.Equal(8, r.PerEnemy[0]);
        Assert.Equal(8, r.PerEnemy[1]);
    }

    [Fact]
    public void MaxDamage_Aoe_HitsAll_NaiveSum()
    {
        var r = LethalSolver.MaxDamage(H(Atk(1, 6, aoe: true)), energy: 1, startStrength: 0, Es(E(10), E(10), E(10)));
        Assert.Equal(18, r.Total);          // 6 to each of 3 = naive sum 18
        Assert.Equal(6, r.PerEnemy[1]);
    }

    [Fact]
    public void MaxDamage_BuffThenAttack_FindsBestOrder()
    {
        // +2 str then 6-dmg = 8 to the single enemy (buff must be counted before the attack).
        var r = LethalSolver.MaxDamage(H(Str(1, 2), Atk(1, 6)), energy: 2, startStrength: 0, Es(E(30)));
        Assert.Equal(8, r.PerEnemy[0]);
    }

    [Fact]
    public void PlayerWeak_ReducesOutgoingDamage()
    {
        // Weak: 8-dmg attack -> floor(8*0.75) = 6.
        var weak = LethalSolver.MaxDamage(H(Atk(1, 8)), energy: 1, startStrength: 0, Es(E(30)), playerWeak: true);
        Assert.Equal(6, weak.PerEnemy[0]);
        var normal = LethalSolver.MaxDamage(H(Atk(1, 8)), energy: 1, startStrength: 0, Es(E(30)), playerWeak: false);
        Assert.Equal(8, normal.PerEnemy[0]);
        // and it can flip a lethal to non-lethal
        Assert.True(LethalSolver.Solve(H(Atk(1, 8)), energy: 1, startStrength: 0, Es(E(8))));
        Assert.False(LethalSolver.Solve(H(Atk(1, 8)), energy: 1, startStrength: 0, Es(E(8)), playerWeak: true));
    }

    [Fact] public void Vulnerable_BoostsLaterAttacks()
    {
        // Bash (8 dmg, applies vuln) then Strike(6) -> 8 + floor(6*1.5)=9 = 17 vs 16
        var hand = H(Atk(2, 8, vuln: true), Atk(1, 6));
        Assert.True(LethalSolver.Solve(hand, energy: 3, startStrength: 0, Es(E(16))));
        // order matters: Strike first (6) then Bash(8) = 14 -> not lethal; solver finds the good order
        Assert.False(LethalSolver.Solve(H(Atk(1, 6), Atk(1, 6)), energy: 2, startStrength: 0, Es(E(16))));
    }

    [Fact] public void PreExistingVulnerable_Counts()
        => Assert.True(LethalSolver.Solve(H(Atk(1, 6)), energy: 1, startStrength: 0, Es(E(9, vuln: true)))); // floor(6*1.5)=9

    [Fact] public void Aoe_HitsAll()
        => Assert.True(LethalSolver.Solve(H(Atk(1, 8, aoe: true)), 1, 0, Es(E(7), E(7), E(7))));

    [Fact] public void MultiHit_Total()
        => Assert.True(LethalSolver.Solve(H(Atk(1, 5, hits: 2)), 1, 0, Es(E(10)))); // 5x2=10

    [Fact] public void SingleTarget_CannotSplit()
        => Assert.False(LethalSolver.Solve(H(Atk(1, 20)), 1, 0, Es(E(6), E(6))));

    [Fact] public void Block_InEffectiveHp()
    {
        Assert.False(LethalSolver.Solve(H(Atk(1, 9)), 1, 0, Es(E(6, block: 4))));  // 9 vs 10
        Assert.True(LethalSolver.Solve(H(Atk(1, 10)), 1, 0, Es(E(6, block: 4))));
    }
}
