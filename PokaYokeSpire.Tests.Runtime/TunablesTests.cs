using System.Linq;
using Xunit;
using PokaYokeSpire.Core;

namespace PokaYokeSpire.Tests.Runtime;

/// <summary>
/// The live-tuning parser (<see cref="Tunables"/>). Pins that JSON drives the values, that parsing is
/// tolerant (missing keys keep current values; bad JSON never throws or corrupts state), and that every
/// (re)load bumps Revision so features know to rebuild. This is the contract the running-game live
/// editor relies on. (LiveTuning's poller node needs the Godot scene tree, so it isn't unit-tested;
/// the parse+apply logic here is the part that can go wrong.)
/// </summary>
public class TunablesTests
{
    private const string Full = @"{
      ""gem"": { ""fontFrac"": 0.5, ""gap"": 99.0, ""scale"": 0.4,
                 ""blueTint"": [0.1, 0.2, 0.3], ""redTint"": [1.0, 0.5, 0.25],
                 ""incomingTip"": ""IN"", ""offenseTip"": ""OUT"" },
      ""hud"": { ""handRaiseFrac"": 0.7, ""counterRaiseFracOfHand"": 0.2 }
    }";

    [Fact]
    public void Load_AppliesEveryValue_AndBumpsRevision()
    {
        int rev = Tunables.Revision;
        Tunables.Load(Full);

        Assert.True(Tunables.Revision > rev, "a load must bump Revision so features rebuild");
        Assert.Equal(0.5f, Tunables.GemFontFrac, 3);
        Assert.Equal(99f, Tunables.GemGap, 3);
        Assert.Equal(0.4f, Tunables.GemScale, 3);
        Assert.Equal("IN", Tunables.IncomingTip);
        Assert.Equal("OUT", Tunables.OffenseTip);
        Assert.Equal(0.7f, Tunables.HandRaiseFrac, 3);
        Assert.Equal(0.2f, Tunables.CounterRaiseFracOfHand, 3);
        Assert.Equal(0.1f, Tunables.GemBlueTint.R, 3);
        Assert.Equal(0.3f, Tunables.GemBlueTint.B, 3);
        Assert.Equal(1.0f, Tunables.GemRedTint.R, 3);
    }

    [Fact]
    public void Load_MissingKeys_KeepCurrentValues()
    {
        Tunables.Load(Full);              // known baseline
        Tunables.Load("{ \"gem\": { \"gap\": 5.0 } }");   // only gap present
        Assert.Equal(5f, Tunables.GemGap, 3);
        Assert.Equal(0.5f, Tunables.GemFontFrac, 3);      // untouched keys retain prior value
        Assert.Equal("IN", Tunables.IncomingTip);
    }

    [Fact]
    public void Load_BadJson_DoesNotThrow_NorCorruptValues()
    {
        Tunables.Load(Full);
        float gap = Tunables.GemGap;
        var ex = Record.Exception(() => Tunables.Load("this is not json { ["));
        Assert.Null(ex);                                  // never throws into the mod
        Assert.Equal(gap, Tunables.GemGap, 3);            // state intact after a bad edit
    }

    [Fact]
    public void Load_EmptyObject_IsHarmless()
    {
        var ex = Record.Exception(() => Tunables.Load("{}"));
        Assert.Null(ex);
    }

    [Fact]
    public void ToJson_RoundTrips_ThroughLoad()
    {
        Tunables.Load(Full);
        string json = Tunables.ToJson();
        Tunables.Load("{ \"gem\": { \"gap\": 1.0 } }");   // perturb
        Tunables.Load(json);                              // restore from serialized form
        Assert.Equal(99f, Tunables.GemGap, 3);
        Assert.Equal(0.4f, Tunables.GemScale, 3);
        Assert.Equal("IN", Tunables.IncomingTip);          // tooltips survive a save/load
        Assert.Equal(0.1f, Tunables.GemBlueTint.R, 3);
    }

    [Fact]
    public void GemBrightnessKnob_ControlsValue_IndependentOfHue()
    {
        // pin the "I can't control brightness" fix: the brightness knob sets the max channel (value)
        // without changing hue; the hue knob changes hue without collapsing brightness.
        var bright = Tunables.Knobs.First(k => k.Path == "gem.blueBright");
        var hue = Tunables.Knobs.First(k => k.Path == "gem.blueHue");

        hue.Set(0.6f);            // some blue-ish hue
        bright.Set(1.5f);
        Assert.Equal(1.5f, System.MathF.Max(Tunables.GemBlueTint.R, System.MathF.Max(Tunables.GemBlueTint.G, Tunables.GemBlueTint.B)), 2);
        Assert.Equal(0.6f, Tunables.GemBlueTint.H, 2);   // hue preserved

        bright.Set(0.5f);         // dim it — hue still preserved, value drops
        Assert.Equal(0.5f, System.MathF.Max(Tunables.GemBlueTint.R, System.MathF.Max(Tunables.GemBlueTint.G, Tunables.GemBlueTint.B)), 2);
        Assert.Equal(0.6f, Tunables.GemBlueTint.H, 2);
    }

    [Fact]
    public void HudGroupOffset_And_Enemy_RoundTrip()
    {
        Tunables.Load(@"{ ""hud"": { ""groupOffsetX"": 30.0, ""groupOffsetY"": -20.0 },
                          ""enemy"": { ""healthBarOffsetY"": -40.0, ""statusOffsetY"": 12.0, ""intentOffsetY"": -55.0 } }");
        string json = Tunables.ToJson();
        Tunables.Load("{ \"hud\": { \"groupOffsetX\": 0.0 } }");
        Tunables.Load(json);
        Assert.Equal(30f, Tunables.HudGroupOffsetX, 3);
        Assert.Equal(-20f, Tunables.HudGroupOffsetY, 3);
        Assert.Equal(-40f, Tunables.EnemyHealthBarOffsetY, 3);
        Assert.Equal(12f, Tunables.EnemyStatusOffsetY, 3);
        Assert.Equal(-55f, Tunables.EnemyIntentOffsetY, 3);
    }

    [Fact]
    public void AllMigratedSections_RoundTrip_ThroughToJson()
    {
        Tunables.Load(@"{
          ""energy"": { ""heightFrac"": 0.66 },
          ""radial"": { ""radius"": 175.0 },
          ""reward"": { ""rewardRaise"": 80.0, ""deckRaiseFracOfReward"": 0.4 },
          ""deckFan"": { ""gap"": 20.0, ""minVisibleStep"": 30.0 },
          ""sidePanel"": { ""marginFrac"": 0.03 },
          ""cardPreview"": { ""cursorOffsetX"": 40.0, ""cursorOffsetY"": 10.0 }
        }");
        string json = Tunables.ToJson();
        Tunables.Load("{ \"energy\": { \"heightFrac\": 0.99 } }");   // perturb
        Tunables.Load(json);                                         // restore from serialized form

        Assert.Equal(0.66f, Tunables.EnergyHeightFrac, 3);
        Assert.Equal(175f, Tunables.RadialRadius, 3);
        Assert.Equal(80f, Tunables.RewardRaise, 3);
        Assert.Equal(0.4f, Tunables.DeckRaiseFracOfReward, 3);
        Assert.Equal(20f, Tunables.DeckFanGap, 3);
        Assert.Equal(30f, Tunables.DeckMinVisibleStep, 3);
        Assert.Equal(0.03f, Tunables.SidePanelMarginFrac, 3);
        Assert.Equal(40f, Tunables.CardPreviewOffsetX, 3);
        Assert.Equal(10f, Tunables.CardPreviewOffsetY, 3);
    }

    [Fact]
    public void WriteTo_ThenLoad_PreservesValues()
    {
        Tunables.Load(Full);
        string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "poka-tunables-test.json");
        Assert.True(Tunables.WriteTo(path));

        Tunables.Load("{ \"gem\": { \"gap\": 7.0 } }");    // change in memory
        Tunables.Load(System.IO.File.ReadAllText(path));   // reload the saved file
        Assert.Equal(99f, Tunables.GemGap, 3);
        System.IO.File.Delete(path);
    }

    [Fact]
    public void Knobs_CoverKeyPaths_AndSetBumpsRevision()
    {
        var paths = Tunables.Knobs.Select(k => k.Path).ToHashSet();
        foreach (var expected in new[] { "gem.fontFrac", "gem.gap", "gem.scale",
                                         "gem.blueHue", "gem.blueBright", "gem.redHue", "gem.redBright",
                                         "hud.handRaiseFrac", "hud.groupOffsetX", "hud.groupOffsetY",
                                         "energy.heightFrac", "radial.radius", "reward.rewardRaise",
                                         "reward.deckRaiseFracOfReward", "deckFan.gap", "deckFan.minVisibleStep",
                                         "sidePanel.marginFrac", "cardPreview.cursorOffsetX", "cardPreview.cursorOffsetY",
                                         "enemy.healthBarOffsetY", "enemy.statusOffsetY", "enemy.intentOffsetY" })
            Assert.Contains(expected, paths);

        var gap = Tunables.Knobs.First(k => k.Path == "gem.gap");
        int rev = Tunables.Revision;
        gap.Set(33f);
        Assert.Equal(33f, gap.Get(), 3);
        Assert.Equal(33f, Tunables.GemGap, 3);
        Assert.True(Tunables.Revision > rev, "a knob edit must bump Revision so features rebuild");
    }
}
