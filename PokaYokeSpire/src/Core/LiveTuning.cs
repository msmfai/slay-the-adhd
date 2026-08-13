using System;
using Godot;

namespace PokaYokeSpire.Core;

/// <summary>
/// The live-tuning pump. When <see cref="Config.LiveTuning"/> is ON, ONE throttled poller node checks
/// tunables.json a few times a second and, on change, reloads <see cref="Tunables"/> and raises
/// <see cref="Reloaded"/> so features rebuild themselves with the new values — the running game becomes
/// a live editor for the mod. When live tuning is OFF, no poller exists and nothing is read from disk.
/// </summary>
public static class LiveTuning
{
    /// Raised on the game thread after tunables.json is hot-reloaded. Features subscribe to rebuild.
    public static event Action? Reloaded;

    internal static void RaiseReloaded() { try { Reloaded?.Invoke(); } catch (Exception e) { DebugLog.Error("LiveTuning.Reloaded", e); } }

    private static bool _ensured;

    /// Attach the poller once (idempotent), under the scene root, if live tuning is enabled.
    public static void Ensure(Node anyNodeInTree)
    {
        try
        {
            if (_ensured || !Config.LiveTuning) return;
            if (anyNodeInTree == null || !GodotObject.IsInstanceValid(anyNodeInTree)) return;
            var root = anyNodeInTree.GetTree()?.Root;
            if (root == null) return;
            if (root.GetNodeOrNull("PokaYokeLiveTuner") != null) { _ensured = true; return; }
            root.AddChild(new LiveTuner { Name = "PokaYokeLiveTuner" });
            _ensured = true;
            DebugLog.Info($"LiveTuner attached — watching {Tunables.DiskPath()}");
        }
        catch (Exception e) { DebugLog.Error("LiveTuning.Ensure", e); }
    }
}

/// The poller node itself: throttled to ~4 Hz, does nothing unless Config.LiveTuning is on.
public partial class LiveTuner : Node
{
    private double _accum;

    public override void _Process(double delta)
    {
        try
        {
            if (!Config.LiveTuning) return;
            _accum += delta;
            if (_accum < 0.25) return;   // ~4 checks/second — mtime compare, negligible
            _accum = 0;
            if (Tunables.TryReloadFromDisk()) LiveTuning.RaiseReloaded();
        }
        catch { /* never let the pump break the game */ }
    }
}
