using System;
using System.Collections.Generic;

namespace PokaYokeSpire.Core;

/// <summary>
/// The ONE guarded runner every feature's patch body goes through. Enforces, by construction:
///   • fail-open (invariant 2) — the body's exceptions can never leak into the game;
///   • auto-disable (invariant 2) — a body that throws repeatedly is switched off for the session;
///   • fail-closed activation (invariant 3) — the body runs only when its gate is satisfied.
/// Features register their logic as <c>Features.Run("name", gate, body)</c> instead of writing a raw
/// Harmony postfix body — so a patch that can crash or run when it shouldn't is not expressible. UI
/// features use <see cref="RunUi"/>, which additionally honours the master DisableAllOverlays switch.
/// Runs on the game thread only (Harmony postfixes); off-thread solves never call in here.
/// </summary>
public static class Feature
{
    private const int DisableAfter = 5;
    private static readonly Dictionary<string, int> _failCount = new();
    private static readonly HashSet<string> _disabled = new();
    private static readonly HashSet<string> _activated = new();

    /// True once a feature has auto-disabled itself (exposed for meta-tests / diagnostics).
    public static bool IsDisabled(string feature) => _disabled.Contains(feature);

    public static void Run(string feature, Func<bool> gate, Action body)
    {
        try
        {
            if (_disabled.Contains(feature)) return;
            bool ok;
            try { ok = gate(); } catch { ok = false; }   // a throwing gate = do nothing (fail-closed)
            if (!ok) return;
            body();
            if (_activated.Add(feature)) DebugLog.Info($"feature '{feature}' active");
        }
        catch (Exception e) { Fail(feature, e); }
    }

    /// Records a failure, logs it (with stack trace) to the debug log, and auto-disables after too many.
    private static void Fail(string feature, Exception e)
    {
        int n = _failCount.TryGetValue(feature, out var c) ? c + 1 : 1;
        _failCount[feature] = n;
        DebugLog.Error($"feature '{feature}' (failure {n}/{DisableAfter})", e);
        if (n >= DisableAfter)
        {
            _disabled.Add(feature);
            DebugLog.Warn($"feature '{feature}' AUTO-DISABLED after {n} failures — vanilla behaviour restored for it");
        }
    }

    /// A UI/overlay feature: additionally fail-closed on the master kill switch (invariant 3).
    public static void RunUi(string feature, Func<bool> gate, Action body)
        => Run(feature, () => !Config.DisableAllOverlays && gate(), body);

    /// For a Harmony PREFIX that may block the original (a confirm-then-proceed guard). Returns the
    /// body's decision, but on gate-off / auto-disabled / ANY exception returns <paramref name="passThrough"/>
    /// (default true = let the original method run) — so a guard can never trap or crash a core action
    /// like ending a turn (invariant 8).
    public static bool Prefix(string feature, Func<bool> gate, Func<bool> body, bool passThrough = true)
    {
        try
        {
            if (_disabled.Contains(feature)) return passThrough;
            bool ok; try { ok = gate(); } catch { ok = false; }
            if (!ok) return passThrough;
            return body();
        }
        catch (Exception e)
        {
            Fail(feature, e);
            return passThrough;   // never trap the game
        }
    }
}
