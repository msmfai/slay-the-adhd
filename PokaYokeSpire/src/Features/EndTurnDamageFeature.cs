using System.Collections.Generic;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.addons.mega_text;        // MegaLabel (the counter's number label)
using MegaCrit.Sts2.Core.Combat;             // CombatState
using MegaCrit.Sts2.Core.Entities.Creatures; // Creature
using MegaCrit.Sts2.Core.Nodes.Combat;       // NEndTurnButton, NEnergyCounter, NCreature, NTargetManager
using PokaYokeSpire.Combat;
using PokaYokeSpire.Core;

namespace PokaYokeSpire.Features;

/// <summary>
/// COMBAT ORBS — two gems flanking the energy counter, each a recolored copy of the real energy orb
/// showing "x / y":
///   LEFT (blue) — incoming: x = HP you'll actually take this turn (after block), y = raw enemy output.
///   RIGHT (red) — offense: x = the most HP damage you can deal this turn (LethalSolver.MaxDamage),
///                 y = x + everything landing on enemies before your next play (ScheduledDamage).
///                 Hovering an enemy scopes both numbers to that enemy.
/// Each gem is a FIXED ⅔-size copy of the energy counter's visual pieces (orb + swirl + MegaLabel),
/// tinted, showing "x / y" at a fixed font size — so font, centering and the vortex match and it
/// never resizes per round. It is NOT a live counter clone (that reacts to your combat events and
/// vanishes on attack). Driven by a POSTFIX on NEndTurnButton.OnCombatStateChanged; fail-open +
/// input-safe via Feature.Run / Overlay.
/// </summary>
[HarmonyPatch(typeof(NEndTurnButton), "OnCombatStateChanged")]
internal static class EndTurnDamageFeature
{
    private sealed class Orb { public Control Gem = null!; public System.Action<string> SetText = null!; public bool IsLeft; }

    private static NEnergyCounter? _gemFor;
    private static Orb? _left, _right;
    private static float _orbNatural = 100f;   // the source orb-art size the content scales from

    private static string _leftText = "0 / 0", _rightText = "0 / 0";

    // per-enemy offense data, cached so hover can scope the right orb; aligned to _enemyRefs.
    private static int[] _xPerEnemy = System.Array.Empty<int>();
    private static int[] _schedPerEnemy = System.Array.Empty<int>();
    private static List<Creature> _enemyRefs = new();
    private static int _xTotal, _yTotal;
    private static Creature? _hoveredEnemy;
    private static NTargetManager? _wiredMgr;

    private static CombatState? _lastCombat;          // last state, so a live-tuning reload can rebuild+refresh
    private static bool _subscribedToLiveTuning;

    private static void Postfix(NEndTurnButton __instance, CombatState combatState)
        => Feature.Run("combat-orbs", () => true, () => Body(combatState));

    private static void Body(CombatState combatState)
    {
        {
            if (Config.DisableAllOverlays || !Config.ShowIncomingGem)
            {
                if (_left?.Gem != null && GodotObject.IsInstanceValid(_left.Gem)) _left.Gem.Visible = false;
                if (_right?.Gem != null && GodotObject.IsInstanceValid(_right.Gem)) _right.Gem.Visible = false;
                return;
            }

            var counter = EnergyCounterFeature.Instance;
            if (counter == null || !GodotObject.IsInstanceValid(counter)) return;

            _lastCombat = combatState;
            LiveTuning.Ensure(counter);   // debug-only: watch tunables.json and hot-rebuild on edit
            if (!_subscribedToLiveTuning) { _subscribedToLiveTuning = true; LiveTuning.Reloaded += OnTunablesReloaded; }

            if (_left?.Gem == null || !GodotObject.IsInstanceValid(_left.Gem) || !ReferenceEquals(_gemFor, counter))
            { BuildOrbs(counter); _gemFor = counter; }
            if (_left == null || _right == null) return;

            WireHover();
            _left.Gem.Visible = _right.Gem.Visible = true;
            // The sim runs OFF-THREAD (TurnSimDriverFeature); ApplyLatest — called each frame from the
            // energy counter's _Process — picks up its cached result and updates the gems' text.
        }
    }

    private static long _appliedGen = long.MinValue;

    /// Game-thread, per-frame: apply the newest off-thread sim result to the gems' text (cheap no-op when
    /// nothing new). Hover changes update the offense text separately via SetHover.
    internal static void ApplyLatest()
    {
        if (_left?.Gem == null || !GodotObject.IsInstanceValid(_left.Gem)) return;
        var o = TurnSimDriverFeature.Latest;
        if (o == null || o.Gen == _appliedGen) return;
        _appliedGen = o.Gen;

        if (!o.HasSim || o.Result.MaxPerEnemy == null || o.EnemyRefs == null)
        {
            _xTotal = 0; _yTotal = 0; _xPerEnemy = System.Array.Empty<int>(); _schedPerEnemy = System.Array.Empty<int>(); _enemyRefs = new();
            _leftText = $"{o.DoNothingIncoming} → {o.DoNothingIncoming}";
        }
        else
        {
            int schedTotal = 0; foreach (var s in o.Scheduled) schedTotal += s;
            _xTotal = o.Result.MaxDamage; _yTotal = _xTotal + schedTotal;
            _xPerEnemy = o.Result.MaxPerEnemy; _schedPerEnemy = o.Scheduled; _enemyRefs = o.EnemyRefs;
            int minTake = System.Math.Min(o.DoNothingIncoming, o.Result.MinHpLost);   // y ≤ x
            _leftText = $"{o.DoNothingIncoming} → {minTake}";
        }
        _rightText = RightText();
        UpdateOrbs();
    }

    /// The enemy currently hovered (null = none). Read by the lethal-gem glow to scope its check.
    internal static Creature? HoveredEnemy => _hoveredEnemy;

    /// The offense orb's text — "x + d" : x = max damage you can do BEFORE the enemies act (cards this
    /// turn), d = the damage that lands on its own before your NEXT turn (current scheduled damage —
    /// existing poison etc., NOT hypothetical extra you could set up). Scoped to the hovered enemy if one
    /// is hovered, else totals across all enemies.
    private static string RightText()
    {
        int x = _xTotal, sched = _yTotal - _xTotal;
        if (_hoveredEnemy != null)
            for (int i = 0; i < _enemyRefs.Count && i < _xPerEnemy.Length && i < _schedPerEnemy.Length; i++)
                if (ReferenceEquals(_enemyRefs[i], _hoveredEnemy))
                {
                    x = _xPerEnemy[i];
                    sched = _schedPerEnemy[i];
                    break;
                }
        return $"{x} + {sched}";
    }

    /// Update both gems' text (fixed size; the MegaLabel auto-sizes its font exactly like the real
    /// energy counter). Positions were set once at build — the gems NEVER resize per round.
    private static void UpdateOrbs()
    {
        _left?.SetText?.Invoke(_leftText);
        _right?.SetText?.Invoke(_rightText);
    }

    private static bool _dumped;

    private static void BuildOrbs(NEnergyCounter counter)
    {
        DumpCounterTree(counter);
        // rename before freeing so the name is released THIS frame — otherwise the idempotent
        // Overlay.Attach below finds the still-alive dying node and returns it (untooltipped),
        // and the hovered gem falls through to the energy counter's own tooltip.
        if (_left?.Gem != null && GodotObject.IsInstanceValid(_left.Gem)) { _left.Gem.Name = "_pokaGemDeadL"; _left.Gem.QueueFree(); }
        if (_right?.Gem != null && GodotObject.IsInstanceValid(_right.Gem)) { _right.Gem.Name = "_pokaGemDeadR"; _right.Gem.QueueFree(); }
        _orbNatural = counter.Size.X > 1f ? counter.Size.X : 100f;
        _left = BuildOrb(counter, "PokaYokeIncomingGem", "gem_incoming", isLeft: true, Tunables.GemBlueTint, () => Tunables.IncomingTip);
        _right = BuildOrb(counter, "PokaYokeOffenseGem", "gem_offense", isLeft: false, Tunables.GemRedTint, () => Tunables.OffenseTip);
        _appliedGen = long.MinValue;   // force ApplyLatest to re-apply the current result to the fresh gems
    }

    /// Live-tuning callback: tunables.json changed — rebuild both gems with the new values and refresh
    /// their text using the last combat state (debug only; no-op if the counter is gone).
    private static void OnTunablesReloaded()
    {
        try
        {
            if (_gemFor == null || !GodotObject.IsInstanceValid(_gemFor)) return;
            BuildOrbs(_gemFor);
            if (_lastCombat != null) Body(_lastCombat);
        }
        catch { }
    }

    /// One-time dump of the energy counter's ENTIRE subtree (node names, types, visibility, transforms)
    /// so the real geometry & structure of the orb art can be read from a log instead of guessed — the
    /// LLM is blind, so this is its eyes. Also reports the concrete runtime types of _layers/_label etc.
    private static void DumpCounterTree(NEnergyCounter counter)
    {
        if (_dumped || !Core.DebugLog.Enabled) return;
        _dumped = true;
        try
        {
            Core.DebugLog.Debug($"=== ENERGY COUNTER === gpos={counter.GlobalPosition} pos={counter.Position} size={counter.Size} scale={counter.Scale} pivot={counter.PivotOffset}");
            foreach (var fn in new[] { "_layers", "_rotationLayers", "_label" })
            {
                var v = Traverse.Create(counter).Field(fn).GetValue();
                Core.DebugLog.Debug($"  field {fn} => {(v == null ? "null" : v.GetType().FullName)}");
            }
            DumpNode(counter, 0);
        }
        catch (System.Exception e) { Core.DebugLog.Error("DumpCounterTree", e); }
    }

    private static void DumpNode(Node n, int depth)
    {
        string pad = new string(' ', depth * 2);
        string info = n switch
        {
            Control c => $"{pad}{n.Name} <{n.GetType().Name}> vis={c.Visible} pos={c.Position} size={c.Size} scale={c.Scale} mod={c.Modulate}",
            Node2D n2 => $"{pad}{n.Name} <{n.GetType().Name}:Node2D> vis={n2.Visible} pos={n2.Position} scale={n2.Scale} mod={n2.Modulate}",
            CanvasItem ci => $"{pad}{n.Name} <{n.GetType().Name}:CanvasItem> vis={ci.Visible}",
            _ => $"{pad}{n.Name} <{n.GetType().Name}>",
        };
        Core.DebugLog.Debug(info);
        if (depth >= 6) return;
        foreach (var child in n.GetChildren()) DumpNode(child, depth + 1);
    }

    /// Clone one of the counter's art fields (orb body / swirl). The field may be a Control OR a Node2D —
    /// grabbing it as Control silently returned null for a Node2D and dropped the art, so we accept any
    /// CanvasItem and LOG (WARN) whenever a piece can't be cloned instead of failing invisibly.
    private static CanvasItem? CloneArtField(NEnergyCounter counter, string field, string gemName, Color tint)
    {
        try
        {
            var v = Traverse.Create(counter).Field(field).GetValue();
            if (v is not CanvasItem src || !GodotObject.IsInstanceValid(src))
            {
                if (Core.DebugLog.Enabled)
                    Core.DebugLog.Warn($"gem '{gemName}': field {field} is {(v == null ? "null" : v.GetType().Name)}, not a live CanvasItem — orb art piece skipped");
                return null;
            }
            if (src.Duplicate() is not CanvasItem dup)
            {
                if (Core.DebugLog.Enabled)
                    Core.DebugLog.Warn($"gem '{gemName}': field {field} ({src.GetType().Name}) did not Duplicate() to a CanvasItem — orb art piece skipped");
                return null;
            }
            dup.Modulate = tint;
            return dup;
        }
        catch (System.Exception e) { Core.DebugLog.Error($"CloneArtField({field})", e); return null; }
    }

    /// A gem is a FIXED ⅔-size copy of the energy counter's VISUAL PIECES — its orb body (_layers), its
    /// swirl (_rotationLayers, spun by our own VortexSpinner), and its MegaLabel (font + centering) —
    /// tinted, showing "x / y" at a FIXED font size. We deliberately do NOT clone the live
    /// NEnergyCounter script: it's subscribed to your combat/energy events, so a clone reacts when you
    /// attack (vanishes), auto-fits the font per digit-count, and its %-unique-name spin lookups break
    /// on Duplicate(). Copying the pieces gets the same look with none of that. Guarded + input-safe.
    private static Orb BuildOrb(NEnergyCounter counter, string name, string tipKey, bool isLeft, Color tint, System.Func<string> tooltip)
    {
        float ch = counter.Size.Y > 1f ? counter.Size.Y : _orbNatural;
        float fontFrac = Tunables.GemFontFrac;   // live-tunable; ⅔ of the previous 0.30

        // The gem must carry the counter's SIZE: the orb art (Layers/RotationLayers) is anchored
        // full-rect, so in a zero-size parent it collapses to 0×0 and draws nothing. Giving the gem the
        // counter's size lets the anchored art fill it exactly like the real counter.
        var gem = new Control { Name = name, MouseFilter = Control.MouseFilterEnum.Ignore, Size = counter.Size };

        // orb body + swirl — clone whatever CanvasItem the counter actually holds (Control OR Node2D;
        // grabbing it as Control silently dropped the art when the field was a Node2D — a whole gem's
        // worth of "no art appears"). Keep each piece's original transform so the layout matches.
        var layers = CloneArtField(counter, "_layers", name, tint);
        if (layers != null) gem.AddChild(layers);

        var rot = CloneArtField(counter, "_rotationLayers", name, tint);
        if (rot != null) { gem.AddChild(rot); gem.AddChild(new VortexSpinner { Layers = rot }); }

        // number — the real MegaLabel (font + centering), FIXED size so it never resizes per digit-count.
        System.Action<string> setText;
        if (Traverse.Create(counter).Field("_label").GetValue<Control>() is { } srcLabel
            && srcLabel.Duplicate() is MegaLabel ml)
        {
            ml.AutoSizeEnabled = false;                                    // fixed font, no per-digit resize
            ml.AddThemeFontSizeOverride("font_size", (int)(ch * fontFrac));
            ml.SelfModulate = Colors.White;
            ml.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);  // fill the gem…
            ml.HorizontalAlignment = HorizontalAlignment.Center;           // …and centre the number on the orb
            ml.VerticalAlignment = VerticalAlignment.Center;
            gem.AddChild(ml);
            setText = txt => { try { ml.Text = txt; } catch { } };
        }
        else
        {
            Core.DebugLog.Warn($"gem '{name}': could not clone the counter's MegaLabel — using a plain label (font may differ)");
            var lbl = new Label { Size = new Vector2(ch, ch), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, MouseFilter = Control.MouseFilterEnum.Ignore };
            var ff = ThemeDB.Singleton?.FallbackFont; if (ff != null) lbl.AddThemeFontOverride("font", ff);
            lbl.AddThemeFontSizeOverride("font_size", (int)(ch * fontFrac));
            lbl.AddThemeColorOverride("font_color", Colors.White);
            gem.AddChild(lbl);
            setText = txt => { try { lbl.Text = txt; } catch { } };
        }

        // Placement is the pure, lint-tested Spatial.GemLayout math (NOT eyeballed): scale/offset about
        // the orb centre (size/2) so each gem flanks the counter at the same height without occluding it.
        var plan = Spatial.GemLayout.For(counter.Size.X, Tunables.GemScale, Tunables.GemGap);
        gem.PivotOffset = counter.Size * 0.5f;
        gem.Scale = new Vector2(plan.Scale, plan.Scale);
        float offX = plan.OffX;
        gem.Position = new Vector2(isLeft ? -offX : offX, 0f);

        if (Core.DebugLog.Enabled)
        {
            var geo = $"gem '{name}': counterSize={counter.Size} orbNatural={_orbNatural} gemPos={gem.Position} offX={offX}; pieces=";
            foreach (var child in gem.GetChildren())
                if (child is Control c) geo += $"[{c.GetType().Name} pos={c.Position} size={c.Size} scale={c.Scale}]";
            Core.DebugLog.Debug(geo);
        }

        var attached = Overlay.Attach(counter, name, () => gem) ?? gem;
        // the game's OWN hover-tip system (NHoverTipSet + a mod loc table for the title); text is read
        // live each hover (so panel edits show at once). Above the gem, same offset the counter uses for
        // its own tip; anchoring at the gem inherits the gem's horizontal offset.
        GameTooltip.Bind(attached, tipKey, tooltip, new Vector2(-70f, -200f));
        return new Orb { Gem = attached, SetText = setText, IsLeft = isLeft };
    }

    private static int TryHp(Creature c) { try { return c.CurrentHp; } catch { return 0; } }

    private static void WireHover()
    {
        try
        {
            var mgr = NTargetManager.Instance;
            if (mgr == null || ReferenceEquals(mgr, _wiredMgr)) return;
            _wiredMgr = mgr;
            mgr.CreatureHovered += OnCreatureHovered;
            mgr.CreatureUnhovered += OnCreatureUnhovered;
        }
        catch { }
    }

    private static void OnCreatureHovered(NCreature c) => SetHover(c?.Entity);

    /// Hover entry point that works with NO card selected (wired to each enemy's hitbox by EnemyHud),
    /// so hovering an enemy scopes the offense orb to it even outside card-targeting mode.
    internal static void OnEnemyHover(Creature? e) => SetHover(e);

    private static void SetHover(Creature? e)
    {
        try
        {
            _hoveredEnemy = e;
            _rightText = RightText();
            if (_gemFor != null && GodotObject.IsInstanceValid(_gemFor)) UpdateOrbs();
        }
        catch { }
    }

    private static void OnCreatureUnhovered(NCreature c) => SetHover(null);
}

/// Per-frame apply of the off-thread sim result to the combat orbs' text (invariant 5: reads a cached
/// value only, never runs the solver). Fail-open + input-safe via the runner.
[HarmonyPatch(typeof(NEnergyCounter), "_Process")]
internal static class CombatOrbsApplyFeature
{
    private static void Postfix()
        => Feature.Run("combat-orbs-apply", () => !Config.DisableAllOverlays && Config.ShowIncomingGem, EndTurnDamageFeature.ApplyLatest);
}

/// Spins a cloned _rotationLayers exactly like NEnergyCounter._Process (each child faster than the
/// last) so the gem swirls — without running the counter's live, combat-reactive script.
internal partial class VortexSpinner : Node
{
    public Node? Layers;   // a Control OR Node2D subtree — spin whichever kind of child it holds
    public override void _Process(double delta)
    {
        try
        {
            if (Layers == null || !GodotObject.IsInstanceValid(Layers)) return;
            float step = (float)delta * 30f;
            for (int i = 0; i < Layers.GetChildCount(); i++)
            {
                var ch = Layers.GetChild(i);
                if (ch is Control c) c.RotationDegrees += step * (i + 1);
                else if (ch is Node2D n2) n2.RotationDegrees += step * (i + 1);
            }
        }
        catch { }
    }
}
