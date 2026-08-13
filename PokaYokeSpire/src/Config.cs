using BaseLib.Config;

namespace PokaYokeSpire;

/// <summary>
/// BaseLib-backed config so each guard is toggleable in the in-game mod-config menu.
/// Pattern mirrors MintySpire2: a SimpleModConfig subclass with static properties;
/// bools render as tickboxes. Registered via ModConfigRegistry.Register in Plugin.Init.
/// </summary>
public class Config : SimpleModConfig
{
    [ConfigSection("safety")]
    /// PANIC SWITCH — turn this ON to instantly disable every overlay this mod draws (deck view,
    /// stat panels, combat orbs, radial relics, etc.) and restore vanilla combat/reward screens.
    /// Use it if anything ever looks wrong or stops responding. Guards still function.
    public static bool DisableAllOverlays { get; set; } = false;

    /// DEBUG LOGGING — write a detailed, leveled log (errors, warnings, feature activity) to
    /// "pokayoke-debug.log" next to the mod, so problems from a normal playthrough are easy to read
    /// afterwards. Off by default (no overhead). Turn on, play, then share the log.
    public static bool DebugLogging { get; set; } = false;

    [ConfigSection("guards")]
    /// Confirm when ending turn with unspent energy AND a playable card in hand.
    public static bool GuardEndTurnEnergy { get; set; } = true;

    /// In elite/boss fights, confirm the first non-potion action of the turn while
    /// holding an unused potion ("are you sure you don't want to use a potion?").
    public static bool GuardElitePotion { get; set; } = true;

    /// Block taking a card from a reward until you've opened your deck at least once.
    public static bool GuardCardRewardDeckCheck { get; set; } = true;

    [ConfigSection("layout")]
    /// Move the energy counter to the center of the screen, just above your hand.
    public static bool CenterEnergyCounter { get; set; } = true;

    /// Vertical position of the recentered energy counter, as a fraction of screen
    /// height (0 = top, 1 = bottom). ~0.85 sits near the original bottom spot.
    [ConfigSlider(0.40, 1.20, 0.01, Format = "{0:0.00}")]
    public static double EnergyCounterHeight { get; set; } = 1.03;

    /// Left-click a top-bar relic to fan a copy of it radially around the energy
    /// counter (left-click again to remove). Suppresses the inspect screen on
    /// left-click while enabled. (v1 — positioning is being tuned.)
    public static bool RadialRelics { get; set; } = false;

    /// Distance (px) of the fanned relic copies from the energy counter.
    [ConfigSlider(60.0, 320.0, 5.0)]
    public static double RadialRelicRadius { get; set; } = 140.0;

    /// Right-click a relic that has a counter to show a small blue counter with its
    /// number, in a row to the left of the energy counter. Right-click again to remove.
    public static bool BlueCounters { get; set; } = false;

    /// Ctrl (or Cmd) + click-drag any UI element to move it around.
    public static bool EnableUiDragging { get; set; } = true;

    [ConfigSection("end_turn_readout")]
    /// Show the HP you'll lose if you end your turn now (after block + end-of-turn
    /// triggers), floating just above the end-turn button.
    public static bool ShowEndTurnDamage { get; set; } = true;

    /// Also show the raw incoming attack total (⚔) beside the button.
    public static bool ShowIncomingTotal { get; set; } = true;

    /// Also show the block you'll have when the enemies attack (🛡), i.e. current block
    /// plus end-of-turn block gains (Plating, Metallicize, Orichalcum, ...).
    public static bool ShowBlockAtEnemyTurn { get; set; } = true;

    [ConfigSection("card_target_preview")]
    /// While targeting an enemy with a card, show its actual effects on that enemy near the
    /// cursor — the game's exact effect text (damage adjusted for Vulnerable, statuses) plus
    /// how much HP gets through its block.
    public static bool ShowCardTargetPreview { get; set; } = true;

    [ConfigSection("lethal_glow")]
    /// Turn the energy gem green + glowing when a deterministic line of card plays could
    /// finish the fight THIS turn (chains Strength/Vulnerable/multi-hit/AOE across enemies).
    /// A deliberate assist — toggle off if you don't want it.
    public static bool LethalGemGlow { get; set; } = true;

    /// How the lethal check adds up your damage. ON (default): only count damage cards you
    /// can actually afford with your current energy — honest. OFF: "just add up all the
    /// damage cards in hand" regardless of energy.
    public static bool LethalGlowRespectEnergy { get; set; } = true;

    [ConfigSection("act_intro")]
    /// At the start of each act, pop a notification naming the act (Underdocks / Overgrowth /
    /// ...) and the boss you'll be fighting.
    public static bool ShowActStartNotification { get; set; } = true;

    [ConfigSection("deck_under_reward")]
    /// On the card-reward screen, show your whole current deck underneath the choices (fanned,
    /// overlapping when crowded; hover a card to bring it to the front) so you can see what
    /// you have while picking — instead of the old "check your deck" nudge.
    public static bool ShowDeckUnderReward { get; set; } = true;

    [ConfigSection("deck_stats")]
    /// On the card-reward screen, flank the choices with aggregate deck metrics (size, avg cost,
    /// draw & energy per card, attack/skill/power mix, avg damage & block, and per-energy
    /// efficiency) so you can judge a pick against your deck's shape at a glance.
    public static bool ShowDeckStats { get; set; } = true;

    [ConfigSection("hud_realestate")]
    /// A blue gem beside the energy counter (⅔ its radius) showing "x / y": the HP you'll actually
    /// take this turn (after block) over the raw damage the enemies are outputting. Always visible.
    public static bool ShowIncomingGem { get; set; } = true;

    /// Raise the hand by half the energy number's text height, and raise the energy counter by a
    /// third as much, to free a little vertical space.
    public static bool RaiseCombatHud { get; set; } = true;
}
