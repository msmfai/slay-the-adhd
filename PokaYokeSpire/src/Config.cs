using BaseLib.Config;

namespace PokaYokeSpire;

/// <summary>
/// BaseLib-backed config so each guard is toggleable in the in-game mod-config menu.
/// Pattern mirrors MintySpire2: a SimpleModConfig subclass with static properties;
/// bools render as tickboxes. Registered via ModConfigRegistry.Register in Plugin.Init.
/// </summary>
public class Config : SimpleModConfig
{
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
}
