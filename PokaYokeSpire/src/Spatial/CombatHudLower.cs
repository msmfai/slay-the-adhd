namespace PokaYokeSpire.Spatial;

/// <summary>
/// Vertical raise offsets for the bottom combat cluster, expressed as multiples of the MEASURED
/// energy-number text height (never eyeballed px — CLAUDE.md § "Spatial placement"):
///   • the HAND is raised by HALF the text height;
///   • the ENERGY COUNTER is raised by a THIRD as much (a sixth of the text height).
/// "Raise" means upward, so callers apply these as a NEGATIVE Y offset.
/// </summary>
public static class CombatHudLower
{
    public const float CardRaiseFrac = 0.5f;                 // hand ↑ half the text height
    public const float CounterRaiseFrac = CardRaiseFrac / 3f; // counter ↑ a third of that

    /// Px to raise the resting hand by, given the energy-text height.
    public static float HandRaise(float textHeight) => textHeight * CardRaiseFrac;

    /// Px to raise the energy counter by (a third of the hand raise).
    public static float CounterRaise(float textHeight) => textHeight * CounterRaiseFrac;
}
