using System;
using System.Collections.Generic;
using Log = MegaCrit.Sts2.Core.Logging.Log;

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
        }
        catch (Exception e)
        {
            int n = _failCount.TryGetValue(feature, out var c) ? c + 1 : 1;
            _failCount[feature] = n;
            try { Log.Info($"[Poka-Yoke] {feature} error ({n}/{DisableAfter}): {(e.InnerException ?? e).Message}"); } catch { }
            if (n >= DisableAfter)
            {
                _disabled.Add(feature);
                try { Log.Info($"[Poka-Yoke] {feature} AUTO-DISABLED after {n} failures — vanilla behaviour restored for it"); } catch { }
            }
        }
    }

    /// A UI/overlay feature: additionally fail-closed on the master kill switch (invariant 3).
    public static void RunUi(string feature, Func<bool> gate, Action body)
        => Run(feature, () => !Config.DisableAllOverlays && gate(), body);
}
