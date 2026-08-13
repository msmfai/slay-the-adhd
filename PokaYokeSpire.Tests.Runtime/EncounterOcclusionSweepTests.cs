using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using PokaYokeSpire.Spatial;
using Xunit;
using Xunit.Abstractions;

namespace PokaYokeSpire.Tests.Runtime;

/// <summary>
/// Runs the spatial occlusion lints across EVERY encounter in the game and reports how many have the
/// re-centred energy meter overlapping an enemy. Per the goal, occlusion is a WARNING, never a hard
/// failure ("it might be unavoidable"). The suite still asserts COVERAGE (the sweep visited every
/// encounter and the formation port behaves), so a real regression — a crash, or the port drifting
/// from the game's math — still fails loud.
///
/// HONESTY (CLAUDE.md § "Spatial placement"): only NON-scene encounters have code-derived positions
/// (NCombatRoom.PositionEnemies, ported in EnemyFormation). Scene-based encounters keep their enemy
/// coordinates as Marker2D nodes inside packed .tscn files, unreadable from the DLL — those are
/// reported as "un-checkable", not silently counted as clean. Enemy footprints aren't in the DLL
/// either, so the sweep reports the count at several NOMINAL enemy sizes to show the sensitivity.
/// </summary>
public class EncounterOcclusionSweepTests
{
    private readonly ITestOutputHelper _out;
    public EncounterOcclusionSweepTests(ITestOutputHelper o) => _out = o;

    // Config.EnergyCounterHeight default (fraction of viewport height for the meter's centre).
    const float EnergyHeightDefault = 1.03f;
    // Energy-counter widget footprint is scene-defined (not in the DLL) — nominal, documented.
    const float MeterW = 200f, MeterH = 200f;

    // Nominal enemy footprints (w,h) — real bounds live in packed spine data. Small/typical/large
    // bracket the true range so the reader sees how the count moves with enemy size.
    static readonly (string tag, float w, float h)[] NominalEnemies =
    {
        ("small",   200f, 260f),
        ("typical", 300f, 360f),
        ("large",   460f, 520f),
    };

    static int CountOccluding(IReadOnlyList<GameEncounters.Info> encs, float w, float h, float heightFrac,
                              out List<string> names)
    {
        names = new List<string>();
        var meter = CombatHudLayout.EnergyMeterRect(EnemyFormation.DevW, EnemyFormation.DevH, heightFrac, MeterW, MeterH);
        foreach (var e in encs)
        {
            if (e.HasCustomScene || e.EnemyCount <= 0) continue;   // only code-positioned fights
            var bx = Enumerable.Repeat(w, e.EnemyCount).ToArray();
            var by = Enumerable.Repeat(h, e.EnemyCount).ToArray();
            var rects = EnemyFormation.ScreenRects(bx, by);
            if (CombatHudLayout.EnergyOcclusions(meter, rects).Count > 0) names.Add(e.Name);
        }
        return names.Count;
    }

    [Fact]
    public void Sweep_EveryEncounter_ReportEnergyMeterOcclusion()
    {
        var encs = GameEncounters.All();
        Assert.True(encs.Count >= 90, $"expected the full encounter roster, enumerated only {encs.Count}");

        int scene = encs.Count(e => e.HasCustomScene);
        int codePositioned = encs.Count(e => !e.HasCustomScene && e.EnemyCount > 0);
        int unknown = encs.Count(e => !e.HasCustomScene && e.EnemyCount <= 0);
        var errored = encs.Where(e => e.Error != null).ToList();

        var sb = new StringBuilder();
        sb.AppendLine("# Combat occlusion sweep — energy meter vs enemies");
        sb.AppendLine();
        sb.AppendLine($"- Encounters enumerated: **{encs.Count}**");
        sb.AppendLine($"- Code-positioned (checkable, non-scene): **{codePositioned}**");
        sb.AppendLine($"- Scene-based (coordinates in packed .tscn — **un-checkable from the DLL**): **{scene}**");
        if (unknown > 0) sb.AppendLine($"- Enemy count undetermined (skipped): **{unknown}**");
        if (errored.Count > 0) sb.AppendLine($"- Errored while reflecting: **{errored.Count}** ({string.Join(", ", errored.Take(6).Select(e => e.Name))})");
        sb.AppendLine();
        sb.AppendLine($"Energy meter: centred, {MeterW:0}×{MeterH:0}px nominal, vertical centre at height-fraction " +
                      $"**{EnergyHeightDefault:0.00}** (Config.EnergyCounterHeight default). Design space 1920×1080.");
        sb.AppendLine();

        // ── headline: occlusion count at the DEFAULT meter height, per nominal enemy size ──
        sb.AppendLine("## At the default meter height");
        sb.AppendLine("| nominal enemy | encounters where the meter occludes an enemy |");
        sb.AppendLine("|---|---|");
        var headline = new Dictionary<string, (int, List<string>)>();
        foreach (var (tag, w, h) in NominalEnemies)
        {
            int c = CountOccluding(encs, w, h, EnergyHeightDefault, out var names);
            headline[tag] = (c, names);
            sb.AppendLine($"| {tag} ({w:0}×{h:0}) | **{c}** / {codePositioned} |");
        }
        sb.AppendLine();

        // ── how the count grows as the meter is raised toward the enemies ──
        sb.AppendLine("## Sensitivity to meter height (typical enemy)");
        sb.AppendLine("| height fraction | occluding encounters |");
        sb.AppendLine("|---|---|");
        foreach (var hf in new[] { 0.55f, 0.60f, 0.65f, 0.70f, 0.75f, 0.80f, 0.85f, EnergyHeightDefault })
        {
            int c = CountOccluding(encs, 300f, 360f, hf, out _);
            sb.AppendLine($"| {hf:0.00} | {c} / {codePositioned} |");
        }
        sb.AppendLine();

        // ── the specific fights that occlude (typical enemy, default height) ──
        var (tc, tnames) = headline["typical"];
        sb.AppendLine($"## Occluding encounters at the DEFAULT meter height (typical enemy): {tc}");
        sb.AppendLine(tnames.Count == 0 ? "_none — at the default bottom position the meter clears every checkable enemy._" : string.Join(", ", tnames.OrderBy(n => n)));
        sb.AppendLine();

        // ── and the fights that occlude if the meter is raised INTO the enemy band ──
        const float raised = 0.65f;
        int rc = CountOccluding(encs, 300f, 360f, raised, out var rnames);
        sb.AppendLine($"## Occluding encounters if the meter is raised to height {raised:0.00} (typical enemy): {rc}");
        sb.AppendLine("_(these are the crowded, multi-enemy fights whose enemies reach the screen centre; occlusion here is the \"unavoidable\" case)_");
        sb.AppendLine();
        sb.AppendLine(rnames.Count == 0 ? "_none_" : string.Join(", ", rnames.OrderBy(n => n)));
        sb.AppendLine();

        // ── the un-checkable (scene-based) encounters, listed honestly ──
        sb.AppendLine($"## Scene-based encounters not checkable from the DLL: {scene}");
        sb.AppendLine("_(their enemy positions are Marker2D nodes inside packed .tscn files; verifying these needs scene extraction)_");
        sb.AppendLine();
        sb.AppendLine(string.Join(", ", encs.Where(e => e.HasCustomScene).Select(e => e.Name).OrderBy(n => n)));
        sb.AppendLine();

        var report = sb.ToString();
        _out.WriteLine(report);
        try
        {
            var path = Path.Combine(FindRepoRoot(), "combat-occlusion-sweep.md");
            File.WriteAllText(path, report);
            _out.WriteLine($"\n(report written to {path})");
        }
        catch (Exception e) { _out.WriteLine($"(could not write report file: {e.Message})"); }

        // COVERAGE assertions (occlusion itself is only a warning, never asserted):
        Assert.True(codePositioned + scene + unknown == encs.Count, "every encounter must be accounted for");
        Assert.True(codePositioned > 0, "expected at least some code-positioned encounters to check");
    }

    // ─────────────── faithful-port sanity: pins EnemyFormation to the game's math ───────────────

    [Fact]
    public void Formation_SingleEnemy_CentredInRightHalf()
    {
        // val=(960-200)/2=380, feet-centre local x = 380+100 = 480 -> screen 960+480 = 1440.
        var r = EnemyFormation.ScreenRects(new[] { 200f }, new[] { 260f });
        Assert.Single(r);
        Assert.Equal(1440f, r[0].CenterX, 1);
        Assert.True(r[0].Left > EnemyFormation.DevW / 2f, "a single enemy sits right of screen centre");
    }

    [Fact]
    public void Formation_TwoEnemies_LeftmostRespectsCenterSafeZone()
    {
        // With two 200px enemies, span=470, val=(960-470)/2=245 (>150) -> leftmost left edge local
        // = 245 -> screen 1205, clear of a centred meter.
        var r = EnemyFormation.ScreenRects(new[] { 200f, 200f }, new[] { 260f, 260f });
        Assert.True(r.Min(x => x.Left) > EnemyFormation.DevW / 2f + 100f, "enemies stay clear of centre for small groups");
    }

    [Fact]
    public void Formation_CrowdedGroup_PushesEnemiesTowardCentre()
    {
        // Four wide (400px) enemies overflow the half-width -> the crowded branch drops val below the
        // 150 safe zone, so an enemy edge crosses toward centre (this is the "unavoidable" case).
        var wide = Enumerable.Repeat(400f, 4).ToArray();
        var tall = Enumerable.Repeat(360f, 4).ToArray();
        var r = EnemyFormation.ScreenRects(wide, tall);
        Assert.Equal(4, r.Length);
        Assert.True(r.Min(x => x.Left) < EnemyFormation.DevW / 2f + EnemyFormation.CenterSafeZone,
            "a crowded formation should pull an enemy inside the centre-safe-zone margin");
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (; dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "CLAUDE.md")) && Directory.Exists(Path.Combine(dir.FullName, "PokaYokeSpire")))
                return dir.FullName;
        return AppContext.BaseDirectory;
    }
}
