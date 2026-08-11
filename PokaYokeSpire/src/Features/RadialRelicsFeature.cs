using System.Collections.Generic;
using System.Linq;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Models;         // RelicModel
using MegaCrit.Sts2.Core.Nodes.Combat;   // NEnergyCounter
using MegaCrit.Sts2.Core.Nodes.Relics;   // NRelicInventory, NRelic

namespace PokaYokeSpire.Features;

/// <summary>
/// FEATURE 5 — left-click a top-bar relic to fan a copy of it radially around the energy
/// counter (balanced at the top); left-click again to remove it. Suppresses the inspect
/// screen while enabled.
///
/// Copies are CHILDREN of the energy counter (hide/draw with it). The pinned set persists
/// across combats and is rebuilt under the current energy counter when it changes.
/// </summary>
[HarmonyPatch(typeof(NRelicInventory), "OnRelicClicked")]
internal static class RadialRelicsFeature
{
    private static bool Prefix(RelicModel model)
    {
        if (!Config.RadialRelics) return true; // feature off -> normal inspect screen
        RadialRelicsManager.Toggle(model);
        return false;
    }
}

internal static class RadialRelicsManager
{
    private static readonly HashSet<RelicModel> _active = new();
    private static readonly Dictionary<RelicModel, NRelic> _visuals = new();
    private static NEnergyCounter? _builtFor;

    internal static void Toggle(RelicModel model)
    {
        if (!_active.Remove(model)) _active.Add(model);
        _builtFor = null; // force rebuild next frame
    }

    internal static void UpdateAll(NEnergyCounter energy)
    {
        if (!ReferenceEquals(energy, _builtFor))
        {
            Rebuild(energy);
            _builtFor = energy;
        }
        if (_visuals.Count == 0) return;

        Vector2 center = energy.Size * 0.5f; // local coords (children of the counter)
        float radius = (float)Config.RadialRelicRadius;
        var nodes = _visuals.Values.Where(GodotObject.IsInstanceValid).ToList();
        int count = nodes.Count;
        for (int i = 0; i < count; i++)
        {
            var (ox, oy) = Layout.RadialCenterOffset(i, count, radius);
            nodes[i].Position = center + new Vector2(ox, oy) - nodes[i].Size * 0.5f;
        }
    }

    private static void Rebuild(NEnergyCounter energy)
    {
        foreach (var n in _visuals.Values)
            if (GodotObject.IsInstanceValid(n)) n.QueueFree();
        _visuals.Clear();

        foreach (var relic in _active.ToList())
        {
            var copy = NRelic.Create(relic, NRelic.IconSize.Small);
            if (copy == null) continue;
            energy.AddChild(copy); // child of the energy counter
            _visuals[relic] = copy;
        }
    }
}
