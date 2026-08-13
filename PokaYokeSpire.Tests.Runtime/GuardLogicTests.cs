using PokaYokeSpire;
using Xunit;

namespace PokaYokeSpire.Tests.Runtime;

/// Unit + metamorphic tests for the extracted guard decision logic. These run with zero
/// game/Godot dependency — they pin the exact conditions under which each guard fires.
public class GuardLogicTests
{
    // ---- GUARD 1: end turn with unspent energy ----
    [Theory]
    [InlineData(0, false, false)]
    [InlineData(0, true, false)]   // no energy -> nothing to spend
    [InlineData(3, false, false)]  // energy but no playable card -> nothing to do
    [InlineData(1, true, true)]    // both -> confirm
    [InlineData(99, true, true)]
    public void EndTurn_FiresOnlyWithEnergyAndPlayableCard(int energy, bool hasCard, bool expect)
        => Assert.Equal(expect, GuardLogic.ShouldConfirmEndTurn(energy, hasCard));

    [Fact]
    public void EndTurn_Metamorphic_NegativeEnergyNeverFires()
    {
        // Energy can't sensibly be negative, but the guard must never prompt if it is.
        for (int e = -5; e <= 0; e++)
            Assert.False(GuardLogic.ShouldConfirmEndTurn(e, true));
    }

    [Fact]
    public void EndTurn_Metamorphic_MonotoneInPlayableCard()
    {
        // For fixed positive energy, gaining a playable card can only ever ENABLE the
        // prompt, never disable it.
        for (int e = 1; e < 10; e++)
            Assert.True(!GuardLogic.ShouldConfirmEndTurn(e, false) && GuardLogic.ShouldConfirmEndTurn(e, true));
    }

    // ---- GUARD 2: potion reminder ----
    [Theory]
    [InlineData(true,  true,  false, true)]   // elite/boss, holding potion, first time -> remind
    [InlineData(true,  true,  true,  false)]  // already reminded this fight
    [InlineData(true,  false, false, false)]  // no potion
    [InlineData(false, true,  false, false)]  // not elite/boss
    public void Potion_FiresOnlyEliteBossWithPotionOncePerFight(bool eb, bool pot, bool done, bool expect)
        => Assert.Equal(expect, GuardLogic.ShouldRemindPotion(eb, pot, done));

    [Fact]
    public void Potion_Metamorphic_OncePerFightIdempotent()
    {
        // Whatever the room/potion state, once reminded (done=true) it must never fire again.
        foreach (var eb in new[]{true,false})
        foreach (var pot in new[]{true,false})
            Assert.False(GuardLogic.ShouldRemindPotion(eb, pot, alreadyReminded: true));
    }

    [Fact]
    public void Potion_BodyText_SwitchesOnAllSlotsFull()
    {
        Assert.Equal(GuardLogic.PotionBodyNormal, GuardLogic.PotionBody(allSlotsFull: false));
        Assert.Equal(GuardLogic.PotionBodyAllSlotsFull, GuardLogic.PotionBody(allSlotsFull: true));
        // the urgent variant must be the red BBCode one
        Assert.Contains("[color=#ff2d2d]", GuardLogic.PotionBody(true));
        Assert.DoesNotContain("[color", GuardLogic.PotionBody(false));
    }

    // ---- GUARD 3: deck-check speed bump ----
    [Theory]
    [InlineData(false, false, true)]   // not viewed, not re-entry -> nudge
    [InlineData(true,  false, false)]  // already viewed -> take it
    [InlineData(false, true,  false)]  // bypass (re-entry after OK) -> take it
    [InlineData(true,  true,  false)]
    public void DeckCheck_NudgesOnlyWhenUnviewedAndNotBypass(bool viewed, bool bypass, bool expect)
        => Assert.Equal(expect, GuardLogic.ShouldNudgeDeckCheck(viewed, bypass));

    [Fact]
    public void DeckCheck_Metamorphic_BypassAlwaysProceeds()
    {
        // The OK re-entry (bypass) can NEVER re-nudge -> guarantees no soft-lock.
        foreach (var viewed in new[]{true,false})
            Assert.False(GuardLogic.ShouldNudgeDeckCheck(viewed, bypass: true));
    }
}
