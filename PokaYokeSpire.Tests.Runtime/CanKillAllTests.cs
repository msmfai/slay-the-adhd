using System.Collections.Generic;
using PokaYokeSpire.Combat;
using Xunit;
using AC = PokaYokeSpire.Combat.LethalCalc.AtkCard;

namespace PokaYokeSpire.Tests.Runtime;

public class CanKillAllTests
{
    private static List<AC> Cards(params AC[] c) => new(c);
    private static List<int> Hps(params int[] h) => new(h);

    [Fact] public void MultiHit_TotalDamageKills()
    {
        // Twin Strike = 5 dmg x2 = TotalDamage 10; enemy 9 hp -> lethal
        Assert.True(LethalCalc.CanKillAll(Cards(new AC(1, 10, false)), energy: 1, Hps(9), respectEnergy: true));
        // as single 5 it would NOT be lethal
        Assert.False(LethalCalc.CanKillAll(Cards(new AC(1, 5, false)), energy: 1, Hps(9), respectEnergy: true));
    }

    [Fact] public void Aoe_HitsEveryEnemy()
    {
        // one AOE for 8 vs three 7-hp enemies -> kills all
        Assert.True(LethalCalc.CanKillAll(Cards(new AC(1, 8, true)), 1, Hps(7, 7, 7), true));
        // AOE 6 vs 7-hp enemies -> not enough
        Assert.False(LethalCalc.CanKillAll(Cards(new AC(1, 6, true)), 1, Hps(7, 7, 7), true));
    }

    [Fact] public void Mixed_AoeSoftensThenSingleTargetFinishes()
    {
        // AOE 4 to both (2 enemies at 10) leaves 6 each; two Strikes(6) finish them, 3 energy
        var cards = Cards(new AC(1, 4, true), new AC(1, 6, false), new AC(1, 6, false));
        Assert.True(LethalCalc.CanKillAll(cards, energy: 3, Hps(10, 10), respectEnergy: true));
        // only 2 energy -> can't play all three -> not lethal
        Assert.False(LethalCalc.CanKillAll(cards, energy: 2, Hps(10, 10), respectEnergy: true));
    }

    [Fact] public void SingleTarget_CannotSplitAcrossEnemies()
    {
        // one big single-target (20) can't cover two separate 6-hp enemies
        Assert.False(LethalCalc.CanKillAll(Cards(new AC(1, 20, false)), 1, Hps(6, 6), true));
        // but two 6s can
        Assert.True(LethalCalc.CanKillAll(Cards(new AC(1, 6, false), new AC(1, 6, false)), 2, Hps(6, 6), true));
    }

    [Fact] public void Block_CountedInEffectiveHp()
    {
        // enemy 6 hp + 4 block = 10 effective; 9 dmg not lethal, 10 is
        Assert.False(LethalCalc.CanKillAll(Cards(new AC(1, 9, false)), 1, Hps(10), true));
        Assert.True(LethalCalc.CanKillAll(Cards(new AC(1, 10, false)), 1, Hps(10), true));
    }

    [Fact] public void NaiveMode_IgnoresEnergy()
    {
        // three 6-dmg cards vs 18 hp, only 1 energy -> lethal in naive mode, not in energy mode
        var cards = Cards(new AC(1, 6, false), new AC(1, 6, false), new AC(1, 6, false));
        Assert.True(LethalCalc.CanKillAll(cards, energy: 1, Hps(18), respectEnergy: false));
        Assert.False(LethalCalc.CanKillAll(cards, energy: 1, Hps(18), respectEnergy: true));
    }
}
