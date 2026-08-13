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
/// Each gem is a FIXED ⅔-size FULL CLONE of the real energy counter (it spins itself, uses its own
/// MegaLabel), tinted, with the energy number redirected to a hidden sink so it shows "x / y" — so
/// font, centering and the vortex match exactly and it never resizes per round. Driven by a POSTFIX
/// on NEndTurnButton.OnCombatStateChanged. Fail-open + input-safe via Feature.Run / Overlay.
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

    /// A gem is a FIXED ⅔-size FULL CLONE of the live energy counter — so it spins itself and uses its
    /// own MegaLabel (font + centering match) with zero re-implementation. `Duplicate()` doesn't copy
    /// the private `_player`, so we reconstruct it (that's what its _Ready/_EnterTree need — without it
    /// they NRE); then we swap its number label for a hidden sink so the counter's own redraw writes
    /// the ENERGY off-screen while we drive the visible label with "x / y". Falls back to a plain
    /// tinted circle if the clone can't be built. Tinted, guarded + input-safe via Overlay.
    private static Orb BuildOrb(NEnergyCounter counter, string name, bool isLeft, Color tint)
    {
        float d = _orbNatural * (2f / 3f);
        float scale = _orbNatural > 1f ? d / _orbNatural : 0.66f;
        const float gap = 14f;
        float counterW = counter.Size.X > 1f ? counter.Size.X : _orbNatural;
        float counterH = counter.Size.Y > 1f ? counter.Size.Y : _orbNatural;
        Vector2 pos = new Vector2(isLeft ? -(d + gap) : counterW + gap, (counterH - d) * 0.5f);

        try
        {
            if (counter.Duplicate() is NEnergyCounter clone)
            {
                // reconstruct the one field the lifecycle needs (Duplicate skips the private _player)
                Traverse.Create(clone).Field("_player").SetValue(Traverse.Create(counter).Field("_player").GetValue());
                clone.Scale = new Vector2(scale, scale);
                clone.Position = pos;
                if (Overlay.Attach(counter, name, () => clone) != null)   // AddChild -> _Ready/_EnterTree run
                {
                    var layers = Traverse.Create(clone).Field("_layers").GetValue<Control>();
                    if (layers != null) layers.Modulate = tint;
                    var rot = Traverse.Create(clone).Field("_rotationLayers").GetValue<Control>();
                    if (rot != null) rot.Modulate = tint;

                    System.Action<string> setText = _ => { };
                    var visible = clone.GetNodeOrNull<MegaLabel>("Label");
                    if (visible != null)
                    {
                        // redirect the counter's own energy redraw to a hidden sink; we own the label
                        if (visible.Duplicate() is MegaLabel sink)
                        {
                            sink.Name = "PokaYokeEnergySink"; sink.Visible = false;
                            sink.MouseFilter = Control.MouseFilterEnum.Ignore;
                            clone.AddChild(sink);
                            Traverse.Create(clone).Field("_label").SetValue(sink);
                        }
                        visible.SelfModulate = Colors.White;
                        var v = visible;
                        setText = txt => { try { v.SetTextAutoSize(txt); } catch { } };
                    }
                    return new Orb { Gem = clone, SetText = setText, IsLeft = isLeft };
                }
            }
        }
        catch { }

        // Fallback: a plain tinted circle + label — never crashes.
        var gem = new Control { Name = name, Size = new Vector2(d, d), MouseFilter = Control.MouseFilterEnum.Ignore, Position = pos };
        var bg = new Panel { Size = new Vector2(d, d), MouseFilter = Control.MouseFilterEnum.Ignore };
        var sb = new StyleBoxFlat
        {
            BgColor = new Color(0.09f, 0.11f, 0.16f, 0.9f),
            BorderColor = new Color(Mathf.Min(tint.R, 1f), Mathf.Min(tint.G, 1f), Mathf.Min(tint.B, 1f), 1f),
            BorderWidthLeft = 3, BorderWidthTop = 3, BorderWidthRight = 3, BorderWidthBottom = 3,
        };
        int r = (int)(d * 0.5f);
        sb.CornerRadiusTopLeft = r; sb.CornerRadiusTopRight = r; sb.CornerRadiusBottomLeft = r; sb.CornerRadiusBottomRight = r;
        bg.AddThemeStyleboxOverride("panel", sb); gem.AddChild(bg);
        var lbl = new Label { Size = new Vector2(d, d), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, MouseFilter = Control.MouseFilterEnum.Ignore };
        var ff = ThemeDB.Singleton?.FallbackFont; if (ff != null) lbl.AddThemeFontOverride("font", ff);
        lbl.AddThemeFontSizeOverride("font_size", (int)(d * 0.3f));
        lbl.AddThemeColorOverride("font_color", Colors.White);
        gem.AddChild(lbl);
        Overlay.Attach(counter, name, () => gem);
        return new Orb { Gem = gem, SetText = txt => { try { lbl.Text = txt; } catch { } }, IsLeft = isLeft };
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
