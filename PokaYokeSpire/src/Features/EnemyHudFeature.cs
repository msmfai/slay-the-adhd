using System;
using System.Collections.Generic;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Creatures;   // Creature
using MegaCrit.Sts2.Core.Nodes.Combat;         // NCreature
using PokaYokeSpire.Core;

namespace PokaYokeSpire.Features;

/// <summary>
/// EXPERIMENTAL enemy-HUD repositioning (Config.MoveEnemyHud; off by default, so enemies are untouched
/// unless you opt in). Applies the tunable enemy.* Y offsets to each enemy's:
///   • %HealthBar  (NCreatureStateDisplay — the whole health/status/nameplate unit)  → healthBarOffsetY
///   • %PowerContainer (the status/power icons, inside the health bar)                → statusOffsetY
///   • %Intents (IntentContainer — the intent icons)                                 → intentOffsetY
/// so you can e.g. put the health bar above the sprite and the intent icons higher still.
///
/// Drift-free by construction: IntentContainer is repositioned by the game every UpdateBounds, so we
/// add the offset to that fresh value; the health bar / power container are positioned once, so we store
/// their BASE the first time we see them and always set base+offset (never accumulate). Toggling the
/// switch off restores the stored base. Guarded + fail-open. A one-time subtree dump (DebugLogging) lets
/// the real node names/positions be verified.
/// </summary>
[HarmonyPatch(typeof(NCreature), "_Ready")]
internal static class EnemyHudReadyFeature
{
    private static void Postfix(NCreature __instance)
        => Feature.Run("enemy-hud-ready", () => true, () => EnemyHud.OnReady(__instance));
}

/// UpdateBounds(Node) is where the game repositions IntentContainer; re-apply after it runs.
[HarmonyPatch(typeof(NCreature), "UpdateBounds", new[] { typeof(Node) })]
internal static class EnemyHudBoundsFeature
{
    private static void Postfix(NCreature __instance)
        => Feature.Run("enemy-hud-bounds", () => true, () => EnemyHud.Apply(__instance));
}

internal static class EnemyHud
{
    private struct Bases { public bool Has; public Vector2 Hb, Pc; }
    private static readonly Dictionary<ulong, Bases> _bases = new();
    private static readonly HashSet<ulong> _dumped = new();
    private static readonly HashSet<ulong> _geoDumped = new();

    public static void OnReady(NCreature c)
    {
        WireHover(c);   // always: enables hover-to-scope the offense orb (even with no card selected)
        if (!DebugLog.Enabled) return;
        ulong id = c.GetInstanceId();
        if (!_dumped.Add(id) || _dumped.Count > 3) return;   // dump a few, not every creature
        DebugLog.Debug($"=== ENEMY {c.Name} <{c.GetType().Name}> ===");
        DumpNode(c, 0, 5);
    }

    /// Connect the enemy's hitbox hover to the offense orb so it scopes to the hovered enemy even
    /// outside card-targeting (the game's target-manager hover only fires while aiming a card).
    private static void WireHover(NCreature c)
    {
        try
        {
            var hitbox = c.GetNodeOrNull<Control>("%Hitbox");
            Creature? entity = c.Entity;
            if (hitbox == null || entity == null) return;
            hitbox.MouseEntered += () => EndTurnDamageFeature.OnEnemyHover(entity);
            hitbox.MouseExited += () => EndTurnDamageFeature.OnEnemyHover(null);
        }
        catch (Exception e) { DebugLog.Error("EnemyHud.WireHover", e); }
    }

    public static void Apply(NCreature c)
    {
        var hb = c.GetNodeOrNull("%HealthBar");
        var intents = c.IntentContainer;
        var pc = hb?.GetNodeOrNull("%PowerContainer");

        DumpGeometry(c, hb, pc, intents);   // once-per-enemy: the REAL positioned geometry (LLM is blind)

        // capture the untouched base positions once (first sighting, before any offset)
        ulong id = c.GetInstanceId();
        if (!_bases.TryGetValue(id, out var b) || !b.Has)
        {
            b = new Bases { Has = true, Hb = PosOf(hb), Pc = PosOf(pc) };
            _bases[id] = b;
        }

        bool on = Config.MoveEnemyHud;
        // health bar + power container: always set from the stored base (never accumulate)
        SetPos(hb, on ? b.Hb + new Vector2(0, Tunables.EnemyHealthBarOffsetY) : b.Hb);
        SetPos(pc, on ? b.Pc + new Vector2(0, Tunables.EnemyStatusOffsetY) : b.Pc);
        // intents: the game just set a fresh base this UpdateBounds, so add the offset on top
        if (on && intents != null) AddY(intents, Tunables.EnemyIntentOffsetY);
    }

    /// One-time, at UpdateBounds (when the health bar/intents are actually positioned), dump the REAL
    /// geometry so the anchor/mirror controls can be designed from data, not intuition (the LLM is blind).
    private static void DumpGeometry(NCreature c, Node? hb, Node? pc, Node? intents)
    {
        if (!DebugLog.Enabled) return;
        ulong id = c.GetInstanceId();
        if (!_geoDumped.Add(id) || _geoDumped.Count > 4) return;
        try
        {
            DebugLog.Debug($"=== ENEMY GEOMETRY {c.Name} ===");
            DebugLog.Debug($"  Hitbox(bounds): {Geo(c.Hitbox)}");
            DebugLog.Debug($"  %HealthBar: {(hb == null ? "NULL — lookup failed" : Geo(hb))}");
            if (hb != null)
                foreach (var ch in hb.GetChildren())
                {
                    DebugLog.Debug($"    {ch.Name}: {Geo(ch)}");
                    foreach (var gc in ch.GetChildren()) DebugLog.Debug($"      {gc.Name}: {Geo(gc)}");
                }
            DebugLog.Debug($"  %PowerContainer: {(pc == null ? "NULL" : Geo(pc))}");
            DebugLog.Debug($"  %Intents: {(intents == null ? "NULL" : Geo(intents))}");
        }
        catch (Exception e) { DebugLog.Error("EnemyHud.DumpGeometry", e); }
    }

    private static string Geo(Node? n) => n switch
    {
        null => "null",
        Control c => $"<{n.GetType().Name}> gpos={c.GlobalPosition} pos={c.Position} size={c.Size} anchors=({c.AnchorLeft:0.##},{c.AnchorTop:0.##},{c.AnchorRight:0.##},{c.AnchorBottom:0.##}) pivot={c.PivotOffset} vis={c.Visible}",
        Node2D n2 => $"<{n.GetType().Name}:2D> gpos={n2.GlobalPosition} pos={n2.Position} vis={n2.Visible}",
        _ => $"<{n.GetType().Name}>",
    };

    // ── position helpers (nodes may be Control OR Node2D) ──
    private static Vector2 PosOf(Node? n) => n switch { Control c => c.Position, Node2D n2 => n2.Position, _ => Vector2.Zero };
    private static void SetPos(Node? n, Vector2 p) { if (n is Control c) c.Position = p; else if (n is Node2D n2) n2.Position = p; }
    private static void AddY(Node n, float dy) { if (n is Control c) c.Position += new Vector2(0, dy); else if (n is Node2D n2) n2.Position += new Vector2(0, dy); }

    private static void DumpNode(Node n, int depth, int maxDepth)
    {
        string pad = new string(' ', depth * 2);
        string info = n switch
        {
            Control c => $"{pad}{n.Name} <{n.GetType().Name}> pos={c.Position} size={c.Size} vis={c.Visible}",
            Node2D n2 => $"{pad}{n.Name} <{n.GetType().Name}:Node2D> pos={n2.Position} vis={n2.Visible}",
            _ => $"{pad}{n.Name} <{n.GetType().Name}>",
        };
        DebugLog.Debug(info);
        if (depth >= maxDepth) return;
        foreach (var child in n.GetChildren()) DumpNode(child, depth + 1, maxDepth);
    }
}
