using System;
using Xunit;
using PokaYokeSpire;
using PokaYokeSpire.Core;

namespace PokaYokeSpire.Tests.Runtime;

/// <summary>
/// Behavioural tests for the ONE guarded runner every patch body goes through (<see cref="Feature"/>).
/// These pin the correctness-by-construction invariants that caused real regressions when violated:
///   • fail-open (inv. 2)     — a throwing body never propagates into the game;
///   • auto-disable (inv. 2)  — a body that throws repeatedly is switched off, not left crashing;
///   • fail-closed gate (inv. 3) — a body runs only when its gate (and, for UI, the kill switch) allows;
///   • guard never traps (inv. 8) — a Prefix guard returns "let the original run" on gate-off /
///     disable / ANY error, so it can never crash or block a core action like ending a turn.
/// Each test uses a UNIQUE feature name so the runner's process-wide disable/fail state can't bleed
/// between tests.
/// </summary>
[Collection("config-mutating")]   // toggles Config.DisableAllOverlays; serialize with other config-mutators
public class FeatureRunnerTests
{
    // DisableAfter is a private const (5); exceed it to trip auto-disable.
    private const int OverThreshold = 8;

    [Fact]
    public void Run_ExecutesBody_WhenGateTrue()
    {
        bool ran = false;
        Feature.Run(nameof(Run_ExecutesBody_WhenGateTrue), () => true, () => ran = true);
        Assert.True(ran);
    }

    [Fact]
    public void Run_SkipsBody_WhenGateFalse()
    {
        bool ran = false;
        Feature.Run(nameof(Run_SkipsBody_WhenGateFalse), () => false, () => ran = true);
        Assert.False(ran);
    }

    [Fact]
    public void Run_SwallowsBodyException_NeverThrows()
    {
        var ex = Record.Exception(() =>
            Feature.Run(nameof(Run_SwallowsBodyException_NeverThrows), () => true, () => throw new InvalidOperationException("boom")));
        Assert.Null(ex);   // the game must never see the exception
    }

    [Fact]
    public void Run_ThrowingGate_TreatedAsFalse_NeverThrows()
    {
        bool ran = false;
        var ex = Record.Exception(() =>
            Feature.Run(nameof(Run_ThrowingGate_TreatedAsFalse_NeverThrows), () => throw new Exception("gate"), () => ran = true));
        Assert.Null(ex);
        Assert.False(ran);   // fail-closed: a gate that throws does NOT run the body
    }

    [Fact]
    public void Run_AutoDisables_AfterRepeatedFailures()
    {
        string name = nameof(Run_AutoDisables_AfterRepeatedFailures);
        Assert.False(Feature.IsDisabled(name));
        for (int i = 0; i < OverThreshold; i++)
            Feature.Run(name, () => true, () => throw new Exception("always"));
        Assert.True(Feature.IsDisabled(name));

        // once disabled, the body is not invoked again (vanilla behaviour restored)
        bool ranAfter = false;
        Feature.Run(name, () => true, () => ranAfter = true);
        Assert.False(ranAfter);
    }

    [Fact]
    public void RunUi_Skips_WhenMasterKillSwitchOn()
    {
        bool before = Config.DisableAllOverlays;
        try
        {
            Config.DisableAllOverlays = true;
            bool ran = false;
            Feature.RunUi(nameof(RunUi_Skips_WhenMasterKillSwitchOn), () => true, () => ran = true);
            Assert.False(ran);   // kill switch wins even when the feature's own gate is true
        }
        finally { Config.DisableAllOverlays = before; }
    }

    [Fact]
    public void RunUi_Runs_WhenKillSwitchOff_AndGateTrue()
    {
        bool before = Config.DisableAllOverlays;
        try
        {
            Config.DisableAllOverlays = false;
            bool ran = false;
            Feature.RunUi(nameof(RunUi_Runs_WhenKillSwitchOff_AndGateTrue), () => true, () => ran = true);
            Assert.True(ran);
        }
        finally { Config.DisableAllOverlays = before; }
    }

    [Fact]
    public void Prefix_ReturnsBodyDecision_WhenGateTrue()
    {
        Assert.False(Feature.Prefix(nameof(Prefix_ReturnsBodyDecision_WhenGateTrue) + "_a", () => true, () => false));
        Assert.True(Feature.Prefix(nameof(Prefix_ReturnsBodyDecision_WhenGateTrue) + "_b", () => true, () => true));
    }

    [Fact]
    public void Prefix_PassesThrough_WhenGateFalse()
    {
        // gate off => original must run (return passThrough=true) even though the body would say false
        bool result = Feature.Prefix(nameof(Prefix_PassesThrough_WhenGateFalse), () => false, () => false, passThrough: true);
        Assert.True(result);
    }

    [Fact]
    public void Prefix_PassesThrough_OnBodyException()
    {
        bool result = true;
        var ex = Record.Exception(() =>
            result = Feature.Prefix(nameof(Prefix_PassesThrough_OnBodyException), () => true,
                                    () => throw new Exception("guard blew up"), passThrough: true));
        Assert.Null(ex);        // never propagate
        Assert.True(result);    // never trap the core action
    }

    [Fact]
    public void Prefix_PassesThrough_WhenDisabled()
    {
        string name = nameof(Prefix_PassesThrough_WhenDisabled);
        for (int i = 0; i < OverThreshold; i++)
            Feature.Prefix(name, () => true, () => throw new Exception("always"), passThrough: true);
        Assert.True(Feature.IsDisabled(name));
        // even asking it to block (passThrough:false would be its intent), a disabled guard lets the
        // original run — a broken guard can never trap the game.
        Assert.True(Feature.Prefix(name, () => true, () => false, passThrough: true));
    }
}
