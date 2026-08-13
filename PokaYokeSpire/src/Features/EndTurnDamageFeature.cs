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
    private static bool _logged;

    private static string _leftText = "0 / 0", _rightText = "0 / 0";

    // per-enemy offense data, cached so hover can scope the right orb; aligned to _enemyRefs.
    private static int[] _xPerEnemy = System.Array.Empty<int>();
    private static int[] _schedPerEnemy = System.Array.Empty<int>();
    private static List<Creature> _enemyRefs = new();
    private static int _xTotal, _yTotal;
    private static Creature? _hoveredEnemy;
    private static NTargetManager? _wiredMgr;

    private static readonly Color BlueTint = new Color(0.18f, 0.42f, 1.8f);
    private static readonly Color RedTint = new Color(1.9f, 0.5f, 0.28f);

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

            if (_left?.Gem == null || !GodotObject.IsInstanceValid(_left.Gem) || !ReferenceEquals(_gemFor, counter))
            { BuildOrbs(counter); _gemFor = counter; }
            if (_left == null || _right == null) return;

            // LEFT — incoming
            var p = IncomingDamage.Compute(combatState);
            int take = p.Valid ? p.NetHpLoss : 0;
            int output = p.Valid ? p.Incoming : 0;
            _leftText = $"{take} / {output}";

            // RIGHT — offense totals + per-enemy cache
            var snap = CombatSnapshot.Build(combatState);
            if (snap != null)
            {
                var dmg = LethalSolver.MaxDamage(snap.Cards, snap.Energy, snap.StartStrength, snap.Enemies, snap.PlayerWeak);
                var sched = ScheduledDamage.PerEnemy(combatState, snap.EnemyRefs);
                int schedTotal = 0;
                foreach (var s in sched) schedTotal += s;
                _xTotal = dmg.Total;
                _yTotal = _xTotal + schedTotal;
                _xPerEnemy = dmg.PerEnemy;
                _schedPerEnemy = sched;
                _enemyRefs = snap.EnemyRefs;
            }
            else { _xTotal = 0; _yTotal = 0; _xPerEnemy = System.Array.Empty<int>(); _schedPerEnemy = System.Array.Empty<int>(); _enemyRefs = new(); }

            WireHover();
            _rightText = RightText();
            UpdateOrbs();
            _left.Gem.Visible = _right.Gem.Visible = true;

            if (!_logged) { _logged = true; MegaCrit.Sts2.Core.Logging.Log.Info($"[Poka-Yoke] combat orbs: incoming {_leftText}, offense {_rightText}"); }
        }
    }

    /// The right orb's text: hovered enemy's numbers if one is hovered, else the global totals.
    private static string RightText()
    {
        int x = _xTotal, y = _yTotal;
        if (_hoveredEnemy != null)
            for (int i = 0; i < _enemyRefs.Count && i < _xPerEnemy.Length && i < _schedPerEnemy.Length; i++)
                if (ReferenceEquals(_enemyRefs[i], _hoveredEnemy))
                {
                    int hp = TryHp(_enemyRefs[i]);
                    x = _xPerEnemy[i];
                    y = hp > 0 ? System.Math.Min(hp, x + _schedPerEnemy[i]) : x + _schedPerEnemy[i];
                    break;
                }
        return $"{x} / {y}";
    }

    /// Update both gems' text (fixed size; the MegaLabel auto-sizes its font exactly like the real
    /// energy counter). Positions were set once at build — the gems NEVER resize per round.
    private static void UpdateOrbs()
    {
        _left?.SetText?.Invoke(_leftText);
        _right?.SetText?.Invoke(_rightText);
    }

    private static void BuildOrbs(NEnergyCounter counter)
    {
        if (_left?.Gem != null && GodotObject.IsInstanceValid(_left.Gem)) _left.Gem.QueueFree();
        if (_right?.Gem != null && GodotObject.IsInstanceValid(_right.Gem)) _right.Gem.QueueFree();
        _logged = false;
        _orbNatural = counter.Size.X > 1f ? counter.Size.X : 100f;
        _left = BuildOrb(counter, "PokaYokeIncomingGem", isLeft: true, BlueTint);
        _right = BuildOrb(counter, "PokaYokeOffenseGem", isLeft: false, RedTint);
    }

    /// A gem is a FIXED ⅔-size copy of the energy counter's VISUAL PIECES — its orb body (_layers), its
    /// swirl (_rotationLayers, spun by our own VortexSpinner), and its MegaLabel (font + centering) —
    /// tinted, showing "x / y" at a FIXED font size. We deliberately do NOT clone the live
    /// NEnergyCounter script: it's subscribed to your combat/energy events, so a clone reacts when you
    /// attack (vanishes), auto-fits the font per digit-count, and its %-unique-name spin lookups break
    /// on Duplicate(). Copying the pieces gets the same look with none of that. Guarded + input-safe.
    private static Orb BuildOrb(NEnergyCounter counter, string name, bool isLeft, Color tint)
    {
        const float scale = 2f / 3f;
        const float gap = 14f;
        float ch = counter.Size.Y > 1f ? counter.Size.Y : _orbNatural;

        var gem = new Control { Name = name, MouseFilter = Control.MouseFilterEnum.Ignore };

        // orb body — keep each piece's original position so the layout matches the counter exactly.
        if (Traverse.Create(counter).Field("_layers").GetValue<Control>() is { } srcLayers
            && GodotObject.IsInstanceValid(srcLayers) && srcLayers.Duplicate() is Control layers)
        { layers.Modulate = tint; gem.AddChild(layers); }

        // swirl — spun by our OWN spinner (no dependency on the counter's %-name lookups or live script)
        if (Traverse.Create(counter).Field("_rotationLayers").GetValue<Control>() is { } srcRot
            && GodotObject.IsInstanceValid(srcRot) && srcRot.Duplicate() is Control rot)
        { rot.Modulate = tint; gem.AddChild(rot); gem.AddChild(new VortexSpinner { Layers = rot }); }

        // number — the real MegaLabel (font + centering), FIXED size so it never resizes per digit-count.
        System.Action<string> setText;
        if (Traverse.Create(counter).Field("_label").GetValue<Control>() is { } srcLabel
            && srcLabel.Duplicate() is MegaLabel ml)
        {
            ml.AutoSizeEnabled = false;
            ml.AddThemeFontSizeOverride("font_size", (int)(ch * 0.28f));
            ml.SelfModulate = Colors.White;
            gem.AddChild(ml);
            setText = txt => { try { ml.Text = txt; } catch { } };
        }
        else
        {
            var lbl = new Label { Size = new Vector2(ch, ch), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, MouseFilter = Control.MouseFilterEnum.Ignore };
            var ff = ThemeDB.Singleton?.FallbackFont; if (ff != null) lbl.AddThemeFontOverride("font", ff);
            lbl.AddThemeFontSizeOverride("font_size", (int)(ch * 0.28f));
            lbl.AddThemeColorOverride("font_color", Colors.White);
            gem.AddChild(lbl);
            setText = txt => { try { lbl.Text = txt; } catch { } };
        }

        // Position by the copied content's ACTUAL bounding box: that box IS the counter's real orb
        // (the counter's local origin is not its centre), so the gems sit symmetric about the orb's
        // centre and scale ⅔ around it. Robust against wherever the counter draws its pieces.
        Vector2 min = new(float.MaxValue, float.MaxValue), max = new(float.MinValue, float.MinValue);
        foreach (var child in gem.GetChildren())
            if (child is Control c) { min = min.Min(c.Position); max = max.Max(c.Position + c.Size); }
        if (max.X > min.X)
        {
            gem.PivotOffset = (min + max) * 0.5f;                     // scale around the content centre
            gem.Scale = new Vector2(scale, scale);
            float offX = (max.X - min.X) * (0.5f + scale * 0.5f) + gap;  // orb half + gap + scaled orb half
            gem.Position = new Vector2(isLeft ? -offX : offX, 0f);
        }
        else
        {
            gem.Scale = new Vector2(scale, scale);
            gem.Position = new Vector2(isLeft ? -(ch + gap) : ch + gap, 0f);
        }

        Overlay.Attach(counter, name, () => gem);
        return new Orb { Gem = gem, SetText = setText, IsLeft = isLeft };
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

    private static void OnCreatureHovered(NCreature c)
    {
        try { _hoveredEnemy = c?.Entity; if (_gemFor != null && GodotObject.IsInstanceValid(_gemFor)) { _rightText = RightText(); UpdateOrbs(); } } catch { }
    }

    private static void OnCreatureUnhovered(NCreature c)
    {
        try { _hoveredEnemy = null; if (_gemFor != null && GodotObject.IsInstanceValid(_gemFor)) { _rightText = RightText(); UpdateOrbs(); } } catch { }
    }
}

/// Spins a cloned _rotationLayers exactly like NEnergyCounter._Process (each child faster than the
/// last) so the gem swirls — without running the counter's live, combat-reactive script.
internal partial class VortexSpinner : Node
{
    public Control? Layers;
    public override void _Process(double delta)
    {
        try
        {
            if (Layers == null || !GodotObject.IsInstanceValid(Layers)) return;
            for (int i = 0; i < Layers.GetChildCount(); i++)
                if (Layers.GetChild(i) is Control c) c.RotationDegrees += (float)delta * 30f * (i + 1);
        }
        catch { }
    }
}
