using System.Collections.Generic;
using PokaYokeSpire.Spatial;
using Xunit;

namespace PokaYokeSpire.Tests.Runtime;

/// <summary>
/// Pins the formation-agnostic combat-HUD geometry that the per-encounter occlusion sweep is built
/// on (enemy rectangles are inputs here; the sweep supplies real ones). See CLAUDE.md § "Spatial".
/// </summary>
public class CombatHudLayoutTests
{
    const float VpW = 1920f, VpH = 1080f;
    const float MeterW = 150f, MeterH = 150f;

    [Fact]
    public void EnergyMeter_IsHorizontallyCentred()
    {
        var m = CombatHudLayout.EnergyMeterRect(VpW, VpH, 0.85f, MeterW, MeterH);
        Assert.Equal(VpW / 2f, m.CenterX, 3);
        Assert.Equal(VpH * 0.85f, m.CenterY, 3);
    }

    [Fact]
    public void EnergyOcclusion_FlagsOverlappingEnemyOnly()
    {
        var meter = CombatHudLayout.EnergyMeterRect(VpW, VpH, 0.5f, MeterW, MeterH); // centre of screen
        var enemies = new List<UiRect>
        {
            new("onTop",  VpW / 2f - 60f, VpH / 2f - 60f, 120f, 120f), // sits on the meter
            new("faaway", 100f, 100f, 120f, 120f),                     // top-left, clear
        };
        var hit = CombatHudLayout.EnergyOcclusions(meter, enemies);
        Assert.Contains("onTop", hit);
        Assert.DoesNotContain("faaway", hit);
    }

    [Fact]
    public void EnergyMeter_AtVeryBottom_DoesNotHitUpperEnemies()
    {
        // heightFrac 1.03 (our code default) parks the meter at the bottom edge; upper enemies clear.
        var meter = CombatHudLayout.EnergyMeterRect(VpW, VpH, 1.03f, MeterW, MeterH);
        var enemy = new UiRect("enemy", VpW / 2f - 100f, VpH * 0.30f, 200f, 260f);
        Assert.Empty(CombatHudLayout.EnergyOcclusions(meter, new[] { enemy }));
    }

    [Fact]
    public void ClipToViewport_TrimsOffscreenPortion()
    {
        var r = new UiRect("r", VpW - 50f, VpH - 50f, 200f, 200f); // pokes off the bottom-right
        var c = CombatHudLayout.ClipToViewport(r, VpW, VpH);
        Assert.Equal(50f, c.W, 3);
        Assert.Equal(50f, c.H, 3);
    }

    [Fact]
    public void ClipToViewport_FullyOffscreenIsZeroArea()
    {
        var r = new UiRect("gone", VpW + 10f, 0f, 100f, 100f);
        Assert.Equal(0f, CombatHudLayout.ClipToViewport(r, VpW, VpH).Area, 3);
    }

    // ── the HUD raise offsets (multiples of energy-text height) ──

    [Theory]
    [InlineData(40f)][InlineData(64f)][InlineData(96f)]
    public void HudRaise_HandHalfTextHeight_CounterAThirdOfThat(float textH)
    {
        Assert.Equal(textH * 0.5f, CombatHudLower.HandRaise(textH), 3);          // hand ↑ half text height
        Assert.Equal(textH * 0.5f / 3f, CombatHudLower.CounterRaise(textH), 3);  // counter ↑ a third of that
        Assert.Equal(CombatHudLower.HandRaise(textH) / 3f, CombatHudLower.CounterRaise(textH), 3);
        Assert.True(CombatHudLower.HandRaise(textH) > CombatHudLower.CounterRaise(textH), "hand rises further than the counter");
    }
}
