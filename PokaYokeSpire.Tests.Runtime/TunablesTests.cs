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
}
