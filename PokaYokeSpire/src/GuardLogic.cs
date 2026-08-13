namespace PokaYokeSpire;

/// <summary>
/// Pure decision logic for the three guards, extracted from the Harmony patches so it can
/// be unit- and metamorphic-tested headless (no Godot, no game objects). The patches read
/// state off the live game objects and then defer every decision to these functions, so a
/// test that pins these down pins down the guards' behaviour.
///
/// Nothing here references a Godot or game type on purpose — the guard classes do the
/// (game-coupled) job of READING energy / room / potions, then call in here to DECIDE.
/// </summary>
public static class GuardLogic
{
    /// GUARD 1 — confirm ending the turn only when energy is left AND a card could still
    /// be played. Neither alone should prompt (0 energy = nothing to spend; no playable
    /// card = nothing to do with it).
    public static bool ShouldConfirmEndTurn(int energy, bool hasPlayableCard)
        => energy > 0 && hasPlayableCard;

    /// GUARD 2 — remind about unused potions on the first player turn of an elite/boss.
    /// Only in an elite/boss, only while holding a potion, and only once per fight.
    public static bool ShouldRemindPotion(bool isEliteOrBoss, bool hasPotion, bool alreadyReminded)
        => isEliteOrBoss && hasPotion && !alreadyReminded;

    /// GUARD 3 — nudge to check the deck before taking a reward card. A speed bump, not a
    /// block: nudge only when the deck hasn't been viewed AND this isn't the re-entry after
    /// the player already clicked OK (bypass).
    public static bool ShouldNudgeDeckCheck(bool deckViewedSinceRewardShown, bool bypass)
        => !bypass && !deckViewedSinceRewardShown;

    // GUARD 2 body copy — kept here so the exact wording (incl. the all-caps red variant
    // shown when every potion slot is full) is under test and can't silently drift.
    public const string PotionBodyNormal =
        "You still have unused potions in this elite/boss fight.";
    public const string PotionBodyAllSlotsFull =
        "[color=#ff2d2d]ALL POTION SLOTS ARE FULL — USE A POTION BEFORE YOU LOSE DROPS![/color]";

    public static string PotionBody(bool allSlotsFull)
        => allSlotsFull ? PotionBodyAllSlotsFull : PotionBodyNormal;
}
