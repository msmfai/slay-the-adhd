using System;
using System.IO;
using System.Reflection;
using GameLog = MegaCrit.Sts2.Core.Logging.Log;

namespace PokaYokeSpire.Core;

/// <summary>
/// A dedicated, grep-able log for the mod, gated on <see cref="Config.DebugLogging"/>. When on, every
/// line is timestamped and level-tagged and appended to "pokayoke-debug.log" NEXT TO THE MOD DLL —
/// so after a normal playthrough the whole story of the mod (and every error/warning it hit) is in one
/// file, isolated from the game's own noisy log. Filter it with: grep -E "\[ERROR\]|\[WARN\]".
///
/// ERROR/WARN lines are also mirrored into the game's own log. Writing is fully fail-open: logging can
/// never throw into the mod. Off by default => zero overhead.
/// </summary>
public static class DebugLog
{
    private static readonly object _lock = new();
    private static string? _path;
    private static bool _resolved;

    public static bool Enabled => Config.DebugLogging;

    /// The resolved log-file path (for diagnostics / telling the user where to find it). May be null.
    public static string? Path { get { Resolve(); return _path; } }

    private static void Resolve()
    {
        if (_resolved) return;
        _resolved = true;
        try
        {
            var loc = Assembly.GetExecutingAssembly().Location;
            var dir = string.IsNullOrEmpty(loc) ? null : System.IO.Path.GetDirectoryName(loc);
            if (dir == null || !Directory.Exists(dir))
            {
                var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                dir = System.IO.Path.Combine(home, "Library", "Application Support", "SlayTheSpire2");
            }
            _path = System.IO.Path.Combine(dir, "pokayoke-debug.log");
            // fresh log each session; header names when it started + the current feature toggles
            File.WriteAllText(_path,
                $"=== PokaYokeSpire debug log — session start {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===\n" +
                $"overlays-disabled={Config.DisableAllOverlays}  " +
                $"deck-under-reward={Config.ShowDeckUnderReward} deck-stats={Config.ShowDeckStats} " +
                $"combat-orbs={Config.ShowIncomingGem} lethal-gem={Config.LethalGemGlow} " +
                $"raise-hud={Config.RaiseCombatHud} card-preview={Config.ShowCardTargetPreview}\n\n");
        }
        catch { _path = null; }
    }

    private static void Write(string level, string msg)
    {
        if (!Config.DebugLogging) return;
        try
        {
            Resolve();
            if (_path == null) return;
            string line = $"{DateTime.Now:HH:mm:ss.fff} [{level}] {msg}\n";
            lock (_lock) File.AppendAllText(_path, line);
            if (level == "ERROR" || level == "WARN")
                try { GameLog.Info($"[Poka-Yoke:{level}] {msg}"); } catch { }
        }
        catch { /* logging must never break the mod */ }
    }

    public static void Error(string msg) => Write("ERROR", msg);
    public static void Warn(string msg) => Write("WARN", msg);
    public static void Info(string msg) => Write("INFO", msg);
    public static void Debug(string msg) { if (Config.DebugLogging) Write("DEBUG", msg); }

    public static void Error(string context, Exception e)
    {
        var x = e.InnerException ?? e;
        Write("ERROR", $"{context} :: {x.GetType().Name}: {x.Message}\n    {x.StackTrace?.Replace("\n", "\n    ")}");
    }
}
