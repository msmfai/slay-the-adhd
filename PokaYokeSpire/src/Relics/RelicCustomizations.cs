using Godot;

namespace PokaYokeSpire.Relics;

/// <summary>
/// ★ THIS IS WHERE YOU DESIGN PER-RELIC BEHAVIOUR. ★
///
/// Install() runs once at mod init. For any relic you don't touch here, the side-counter
/// shows the obvious default (its DisplayAmount, no max). To customise a relic, look up
/// its class name (see the mod's decompiled/ dir, e.g. the class is "BurningBlood") and
/// assign the fields you care about on RelicRegistry.For("ClassName").
///
/// Examples below are commented placeholders — uncomment/adjust with real relic class
/// names + thresholds. Add as many as you like; each is independent.
/// </summary>
internal static class RelicCustomizations
{
    public static void Install()
    {
        // --- Counter with a max ("current/max") ---
        // A relic that charges up to a threshold: show progress toward it.
        // RelicRegistry.For("SomeChargingRelic").Counter =
        //     r => new CounterDisplay(current: r.DisplayAmount, max: 3);

        // --- Counter with a custom colour ---
        // RelicRegistry.For("SomeRelic").Counter =
        //     r => new CounterDisplay(r.DisplayAmount, max: 10, color: new Color(1f, 0.6f, 0.1f));

        // --- Fully custom current value (not just DisplayAmount) ---
        // RelicRegistry.For("SomeRelic").Counter = r =>
        // {
        //     int current = /* derive from r however you like */ r.StackCount;
        //     return new CounterDisplay(current, max: 5);
        // };
    }
}
