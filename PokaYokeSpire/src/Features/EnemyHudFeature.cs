using System;
using System.Collections.Generic;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Creatures;   // Creature
using MegaCrit.Sts2.Core.Nodes.Combat;         // NCreature
using PokaYokeSpire.Core;

namespace PokaYokeSpire.Features;

/// <summary>
/// Enemy-HUD repositioning, driven live off the enemy.* tunables (F9 → "Combat · enemies") — no separate
/// toggle; all-zero offsets leave enemies untouched. Applies to each enemy's:
///   • %HealthBar  (NCreatureStateDisplay — the whole health/status/nameplate unit)  → healthBarOffsetX/Y
///   • %PowerContainer (the status/power icons, nested in the health bar)             → statusOffsetX/Y
///   • %Intents (IntentContainer — the intent icons)                                 → intentOffsetX/Y
///   • anchorFrac (0..1) slides the health unit + intents up a fraction of the sprite height (→ above head)
///
/// Correct by construction against how the game positions these (established statically): every UpdateBounds
/// it re-anchors X to the sprite bounds but PRESERVES Y, while intents alone reset BOTH axes from a marker.
/// So: X is read fresh each frame and offset (bars keep tracking a moving sprite); the Y-preserved nodes get
/// a stable captured-base + offset (no drift); intents get fresh + offset (also no drift). Guarded +
/// fail-open. A one-time geometry dump (DebugLogging) exposes the real positions to calibrate the anchor.
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
    private struct Bases { public bool Has; public float HbY, PcY; }
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

        // Capture each Y-preserved node's natural Y ONCE (first sighting, before we touch it). The game
        // re-anchors X to the sprite every UpdateBounds but PRESERVES Y — so a stable offset must be built on
        // a captured baseY, while X is read fresh each frame (so bars still track a moving sprite).
        ulong id = c.GetInstanceId();
        if (!_bases.TryGetValue(id, out var b) || !b.Has)
        {
            b = new Bases { Has = true, HbY = PosY(hb), PcY = PosY(pc) };
            _bases[id] = b;
        }

        // Slide the whole health unit + intents up the sprite by a fraction of its height (0 = default).
        float anchorY = -BoundsHeight(c) * Tunables.EnemyAnchorFrac;

        // Health bar: X = fresh game X + offsetX (tracks sprite);  Y = captured base + offsetY + anchor.
        SetPos(hb, new Vector2(PosX(hb) + Tunables.EnemyHealthBarOffsetX, b.HbY + Tunables.EnemyHealthBarOffsetY + anchorY));
        // Power container is nested UNDER the health bar, so it already rode the anchor — only its own nudge.
        SetPos(pc, new Vector2(PosX(pc) + Tunables.EnemyStatusOffsetX, b.PcY + Tunables.EnemyStatusOffsetY));
        // Intents: the game reset BOTH axes from a marker this UpdateBounds, so add offsets to the fresh value
        // (non-accumulating) — anchor included so intents rise with the bar.
        if (intents != null)
            SetPos(intents, new Vector2(PosX(intents) + Tunables.EnemyIntentOffsetX, PosY(intents) + Tunables.EnemyIntentOffsetY + anchorY));
    }

    /// Sprite height proxy for the anchor slider — the clickable hitbox bounds the enemy art closely enough.
    private static float BoundsHeight(NCreature c)
    {
        try { return c.Hitbox is Control h ? h.Size.Y : 0f; }
        catch { return 0f; }
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
    private static float PosX(Node? n) => PosOf(n).X;
    private static float PosY(Node? n) => PosOf(n).Y;
    private static void SetPos(Node? n, Vector2 p) { if (n is Control c) c.Position = p; else if (n is Node2D n2) n2.Position = p; }

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
