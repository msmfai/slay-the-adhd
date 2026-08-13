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
/// Each gem is a FIXED ⅔-size copy of the real energy counter's own visual pieces (orb + spinning
/// vortex + MegaLabel), tinted — so font, centering and the vortex match, and it never resizes per
/// round. Driven by a POSTFIX on NEndTurnButton.OnCombatStateChanged (offense solve runs
/// synchronously). Fail-open + input-safe via Feature.Run / Overlay.
/// </summary>
[HarmonyPatch(typeof(NEndTurnButton), "OnCombatStateChanged")]
internal static partial class EndTurnDamageFeature
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

    private static Control? Dup(NEnergyCounter counter, string field)
    {
        try
        {
            var src = Traverse.Create(counter).Field(field).GetValue<Control>();
            if (src != null && GodotObject.IsInstanceValid(src) && src.Duplicate() is Control dup) return dup;
        }
        catch { }
        return null;
    }

    /// A gem is a FIXED ⅔-size copy of the real energy counter's own visual pieces: the orb body
    /// (_layers), the spinning vortex (_rotationLayers, re-spun by VortexSpinner exactly like the
    /// counter's _Process), and its MegaLabel (so font + centering match), tinted, showing "x / y".
    private static Orb BuildOrb(NEnergyCounter counter, string name, bool isLeft, Color tint)
    {
        float d = _orbNatural * (2f / 3f);                       // fixed diameter — never resizes
        float scale = _orbNatural > 1f ? d / _orbNatural : 0.66f;
        var gem = new Control { Name = name, Size = new Vector2(d, d), MouseFilter = Control.MouseFilterEnum.Ignore };

        var layers = Dup(counter, "_layers");
        if (layers != null) { layers.Modulate = tint; layers.Position = Vector2.Zero; layers.Scale = new Vector2(scale, scale); gem.AddChild(layers); }

        var rot = Dup(counter, "_rotationLayers");
        if (rot != null)
        {
            rot.Modulate = tint; rot.Position = Vector2.Zero; rot.Scale = new Vector2(scale, scale);
            gem.AddChild(rot);
            gem.AddChild(new VortexSpinner { Layers = rot });   // replicate the counter's spin
        }

        System.Action<string> setText;
        var srcLabel = counter.GetNodeOrNull<Control>("Label");
        if (srcLabel != null && srcLabel.Duplicate() is MegaLabel ml)
        {
            ml.Position = Vector2.Zero; ml.Scale = new Vector2(scale, scale); ml.SelfModulate = Colors.White;
            gem.AddChild(ml);
            setText = txt => { try { ml.SetTextAutoSize(txt); } catch { } };
        }
        else   // fallback if the MegaLabel can't be cloned
        {
            var lbl = new Label { Size = new Vector2(d, d), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, MouseFilter = Control.MouseFilterEnum.Ignore };
            var f = ThemeDB.Singleton?.FallbackFont; if (f != null) lbl.AddThemeFontOverride("font", f);
            lbl.AddThemeFontSizeOverride("font_size", (int)(d * 0.3f));
            lbl.AddThemeColorOverride("font_color", Colors.White);
            lbl.AddThemeColorOverride("font_outline_color", new Color(0f, 0f, 0f));
            lbl.AddThemeConstantOverride("outline_size", 5);
            gem.AddChild(lbl);
            setText = txt => { try { lbl.Text = txt; } catch { } };
        }

        const float gap = 14f;
        float counterW = counter.Size.X > 1f ? counter.Size.X : _orbNatural;
        float counterH = counter.Size.Y > 1f ? counter.Size.Y : _orbNatural;
        gem.Position = new Vector2(isLeft ? -(d + gap) : counterW + gap, (counterH - d) * 0.5f);

        Overlay.Attach(counter, name, () => gem);   // input-safe + idempotent by construction
        return new Orb { Gem = gem, SetText = setText, IsLeft = isLeft };
    }

    /// Spins a cloned _rotationLayers exactly like NEnergyCounter._Process (each child faster than the
    /// last), so the gem gets the real vortex without cloning the counter's crash-prone script.
    private partial class VortexSpinner : Node
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
