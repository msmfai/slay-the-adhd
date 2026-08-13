using System.Collections.Generic;
using Godot;
using HarmonyLib;
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
/// The gems are SIZED TO THEIR TEXT — measured each update, sized to contain the wider string, and
/// both forced to the same diameter — so digits always sit inside the orb. Driven by a POSTFIX on
/// NEndTurnButton.OnCombatStateChanged (offense solve runs synchronously). Fail-open.
/// </summary>
[HarmonyPatch(typeof(NEndTurnButton), "OnCombatStateChanged")]
internal static class EndTurnDamageFeature
{
    private sealed class Orb { public Control Gem = null!; public Control Content = null!; public Label Label = null!; public bool IsLeft; }

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
            ApplyLayout(counter);
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

    /// Measure both strings, size the gem to contain the wider one (text box inside a circle), and
    /// apply that SAME diameter to both orbs.
    private static void ApplyLayout(NEnergyCounter counter)
    {
        if (_left == null || _right == null) return;
        _left.Label.Text = _leftText;
        _right.Label.Text = _rightText;

        var font = ThemeDB.Singleton?.FallbackFont;
        float f = Mathf.Clamp(_orbNatural * 0.30f, 16f, 64f);
        float wMax = Mathf.Max(MeasureWidth(font, _leftText, f), MeasureWidth(font, _rightText, f));
        float h = f * 1.2f;
        float d = Mathf.Sqrt(wMax * wMax + h * h) * 1.12f;   // text box's diagonal fits inside the circle
        d = Mathf.Clamp(d, _orbNatural * 0.45f, _orbNatural * 1.4f); // floor (not a dot) + cap (never balloons)

        float counterW = counter.Size.X > 1f ? counter.Size.X : _orbNatural;
        float counterH = counter.Size.Y > 1f ? counter.Size.Y : _orbNatural;
        ApplyOrb(_left, d, f, counterW, counterH);
        ApplyOrb(_right, d, f, counterW, counterH);
    }

    private static void ApplyOrb(Orb o, float d, float f, float counterW, float counterH)
    {
        o.Gem.Size = new Vector2(d, d);
        o.Content.Position = Vector2.Zero;
        o.Content.Scale = new Vector2(d / _orbNatural, d / _orbNatural);
        o.Label.Size = new Vector2(d, d);
        o.Label.Position = Vector2.Zero;
        o.Label.AddThemeFontSizeOverride("font_size", (int)f);
        const float gap = 14f;
        float x = o.IsLeft ? -(d + gap) : counterW + gap;
        o.Gem.Position = new Vector2(x, (counterH - d) * 0.5f);
    }

    private static float MeasureWidth(Font? font, string text, float fontSize)
    {
        try { if (font != null) return font.GetStringSize(text, HorizontalAlignment.Left, -1f, (int)fontSize).X; }
        catch { }
        return text.Length * fontSize * 0.55f;
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

    private static Orb BuildOrb(NEnergyCounter counter, string name, bool isLeft, Color tint)
    {
        var gem = new Control { Name = name, MouseFilter = Control.MouseFilterEnum.Ignore };

        Control content;
        Control? orb = null;
        try
        {
            var srcLayers = Traverse.Create(counter).Field("_layers").GetValue<Control>();
            if (srcLayers != null && GodotObject.IsInstanceValid(srcLayers) && srcLayers.Duplicate() is Control dup)
            {
                dup.Name = "Orb"; dup.Modulate = tint; dup.MouseFilter = Control.MouseFilterEnum.Ignore;
                orb = dup;
            }
        }
        catch { }

        if (orb != null) content = orb;
        else
        {
            var bg = new Panel { Size = new Vector2(_orbNatural, _orbNatural), MouseFilter = Control.MouseFilterEnum.Ignore };
            var sb = new StyleBoxFlat
            {
                BgColor = new Color(0.09f, 0.11f, 0.16f, 0.9f),
                BorderColor = new Color(Mathf.Min(tint.R, 1f), Mathf.Min(tint.G, 1f), Mathf.Min(tint.B, 1f), 1f),
                BorderWidthLeft = 3, BorderWidthTop = 3, BorderWidthRight = 3, BorderWidthBottom = 3,
            };
            int r = (int)(_orbNatural * 0.5f);
            sb.CornerRadiusTopLeft = r; sb.CornerRadiusTopRight = r; sb.CornerRadiusBottomLeft = r; sb.CornerRadiusBottomRight = r;
            bg.AddThemeStyleboxOverride("panel", sb);
            content = bg;
        }
        gem.AddChild(content);

        var label = new Label
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        var font = ThemeDB.Singleton?.FallbackFont;
        if (font != null) label.AddThemeFontOverride("font", font);
        label.AddThemeColorOverride("font_color", Colors.White);
        label.AddThemeColorOverride("font_outline_color", new Color(0f, 0f, 0f));
        label.AddThemeConstantOverride("outline_size", 5);
        gem.AddChild(label);

        Overlay.Attach(counter, name, () => gem);   // input-safe + idempotent by construction
        return new Orb { Gem = gem, Content = content, Label = label, IsLeft = isLeft };
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
        try { _hoveredEnemy = c?.Entity; if (_gemFor != null && GodotObject.IsInstanceValid(_gemFor)) { _rightText = RightText(); ApplyLayout(_gemFor); } } catch { }
    }

    private static void OnCreatureUnhovered(NCreature c)
    {
        try { _hoveredEnemy = null; if (_gemFor != null && GodotObject.IsInstanceValid(_gemFor)) { _rightText = RightText(); ApplyLayout(_gemFor); } } catch { }
    }
}
