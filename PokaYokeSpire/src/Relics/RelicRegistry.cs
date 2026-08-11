using System;
using System.Collections.Generic;
using MegaCrit.Sts2.Core.Models; // RelicModel

namespace PokaYokeSpire.Relics;

/// <summary>
/// What a blue side-counter shows for a relic. Current, an optional Max (shown as
/// "current/max"), and an optional colour override.
/// </summary>
public sealed class CounterDisplay
{
    public int Current;
    public int? Max;              // null -> show just the current number
    public Godot.Color? Color;    // null -> default blue

    public CounterDisplay(int current, int? max = null, Godot.Color? color = null)
    {
        Current = current; Max = max; Color = color;
    }

    /// The obvious default for any relic with no custom behaviour: its DisplayAmount.
    public static CounterDisplay Default(RelicModel relic) => new(relic.DisplayAmount);

    public string Text => Max.HasValue ? $"{Current}/{Max.Value}" : Current.ToString();
}

/// <summary>
/// Per-relic custom behaviour. Add more fields here as new per-relic features are
/// designed (colours, click actions, etc.) — each is optional and falls back to a
/// sensible default when unset.
/// </summary>
public sealed class RelicBehavior
{
    /// How to compute the side-counter display for this relic. Null -> Default().
    public Func<RelicModel, CounterDisplay>? Counter;
}

/// <summary>
/// The plumbing: register custom behaviour per relic, keyed by the relic's class name
/// (e.g. "BurningBlood"). Anything not registered gets the obvious default. Register in
/// RelicCustomizations.Install (called once at mod init).
/// </summary>
public static class RelicRegistry
{
    private static readonly Dictionary<string, RelicBehavior> _behaviors = new();

    /// The registry key for a relic — its class name (stable, unique per relic).
    public static string KeyOf(RelicModel relic) => relic.GetType().Name;

    /// Get (creating if needed) the behaviour bag for a relic id, to assign fields on.
    /// e.g. RelicRegistry.For("BurningBlood").Counter = r => new CounterDisplay(r.DisplayAmount, 6);
    public static RelicBehavior For(string relicId)
        => _behaviors.TryGetValue(relicId, out var b) ? b : (_behaviors[relicId] = new RelicBehavior());

    /// Resolve the counter display for a live relic (custom if registered, else default).
    public static CounterDisplay Counter(RelicModel relic)
    {
        if (_behaviors.TryGetValue(KeyOf(relic), out var b) && b.Counter != null)
        {
            try { return b.Counter(relic); }
            catch { /* a bad custom fn must never break the counter */ }
        }
        return CounterDisplay.Default(relic);
    }
}
