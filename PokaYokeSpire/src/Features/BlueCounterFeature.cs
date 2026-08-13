using System.Collections.Generic;
using System.Linq;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Models;                // RelicModel
using MegaCrit.Sts2.Core.Nodes.Combat;          // NEnergyCounter
using MegaCrit.Sts2.Core.Nodes.GodotExtensions; // NClickableControl
using MegaCrit.Sts2.Core.Nodes.Relics;          // NRelicInventoryHolder
using PokaYokeSpire.Core;
using PokaYokeSpire.Relics;

namespace PokaYokeSpire.Features;

/// <summary>
/// FEATURE 6 — right-click a relic that has a counter to show a small blue counter (the
/// energy orb texture, tinted blue) in a row to the LEFT of the energy counter, showing
/// current (or current/max) from the per-relic registry. Right-click again removes it.
///
/// The counters are CHILDREN of the energy counter, so they hide when it hides and draw
/// on its layer. The pinned set persists across combats; visuals are rebuilt under the
/// current energy counter whenever it changes.
/// </summary>
[HarmonyPatch(typeof(NClickableControl), "_GuiInput")]
internal static class BlueCounterRightClick
{
    private static void Postfix(NClickableControl __instance, InputEvent inputEvent)
        => Feature.Run("blue-counter-click", () => Config.BlueCounters, () =>
    {
        {
            if (inputEvent is not InputEventMouseButton mb) return;
            if (mb.ButtonIndex != MouseButton.Right || !mb.Pressed) return;
            if (__instance is not NRelicInventoryHolder holder) return;

            RelicModel? model = holder.Relic?.Model;
            if (model == null || !model.ShowCounter) return;
            BlueCounterManager.Toggle(model);
        }
    });
}

internal static class BlueCounterManager
{
    private const float Scale = 0.55f;
    private const float Gap = 96f;

    // Source of truth (persists across combats).
    private static readonly HashSet<RelicModel> _active = new();
    // Current visuals, children of _builtFor.
    private static readonly Dictionary<RelicModel, Label> _labels = new();
    private static NEnergyCounter? _builtFor;

    internal static void Toggle(RelicModel model)
    {
        if (!_active.Remove(model)) _active.Add(model);
        MegaCrit.Sts2.Core.Logging.Log.Info($"[Poka-Yoke] feature6 FIRED: relic counter toggle ({_active.Count} shown)");
        // Force a rebuild next frame so visuals match _active.
        _builtFor = null;
    }

    /// Each frame from EnergyCounterFeature: (re)build children under the current energy
    /// counter if it changed, then refresh numbers + lay them out.
    internal static void UpdateAll(NEnergyCounter energy)
    {
        if (!ReferenceEquals(energy, _builtFor))
        {
            Rebuild(energy);
            _builtFor = energy;
        }
        if (_labels.Count == 0) return;

        Vector2 center = energy.Size * 0.5f; // local coords (children of the counter)
        int i = 0;
        foreach (var kv in _labels.Where(k => GodotObject.IsInstanceValid(k.Value)).ToList())
        {
            var info = RelicRegistry.Counter(kv.Key);
            kv.Value.Text = info.Text;
            if (info.Color is { } c) kv.Value.AddThemeColorOverride("font_color", c);
            if (kv.Value.GetParent() is Control wrapper) // label -> wrapper
            {
                var (ox, oy) = Layout.RowCenterOffset(i, Gap);
                wrapper.Position = center + new Vector2(ox, oy) - wrapper.Size * 0.5f;
            }
            i++;
        }
    }

    private static void Rebuild(NEnergyCounter energy)
    {
        foreach (var l in _labels.Values)
            if (GodotObject.IsInstanceValid(l) && l.GetParent() is Node w) w.QueueFree();
        _labels.Clear();

        var layers = Traverse.Create(energy).Field("_layers").GetValue<Control>();
        if (layers == null || !GodotObject.IsInstanceValid(layers)) return;

        Vector2 orbSize = layers.Size.X > 1f ? layers.Size : new Vector2(110, 110);
        foreach (var relic in _active.ToList())
        {
            var (wrapper, label) = MakeCounter(layers, orbSize);
            energy.AddChild(wrapper);        // child of the energy counter
            UiSafety.Passthrough(wrapper);   // input-safe by construction (invariant 1)
            _labels[relic] = label;
        }
    }

    private static (Control wrapper, Label label) MakeCounter(Control orbSource, Vector2 orbSize)
    {
        Vector2 wrapSize = orbSize * Scale;
        var wrapper = new Control { CustomMinimumSize = wrapSize, Size = wrapSize };

        var orb = (Control)orbSource.Duplicate();
        orb.Modulate = new Color(0.35f, 0.6f, 1f);
        orb.Scale = new Vector2(Scale, Scale);
        orb.Position = Vector2.Zero;
        wrapper.AddChild(orb); // wrapper -> orb (scaled)

        var label = new Label
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        label.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        label.AddThemeColorOverride("font_color", new Color(1f, 1f, 1f));
        label.AddThemeColorOverride("font_outline_color", new Color(0f, 0.1f, 0.3f));
        label.AddThemeConstantOverride("outline_size", 4);
        wrapper.AddChild(label); // wrapper -> label (full-size text over the orb)

        return (wrapper, label);
    }
}
