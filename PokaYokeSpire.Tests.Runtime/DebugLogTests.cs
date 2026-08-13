using System;
using System.IO;
using Xunit;
using PokaYokeSpire;
using PokaYokeSpire.Core;

namespace PokaYokeSpire.Tests.Runtime;

/// <summary>
/// Tests for the mod's debug log — the tool that makes "read the errors after a normal playthrough"
/// trivial. Pins: it is gated on <see cref="Config.DebugLogging"/> (off = zero overhead), it never
/// throws into the mod, and an error is captured with its exception type AND a stack trace so a real
/// crash is diagnosable from the file alone.
///
/// The class is [NotParallel] via a shared collection because it toggles the process-wide
/// <see cref="Config.DebugLogging"/> flag; every test restores it in a finally.
/// </summary>
[Collection("config-mutating")]
public class DebugLogTests
{
    [Fact]
    public void Enabled_TracksConfigFlag()
    {
        bool before = Config.DebugLogging;
        try
        {
            Config.DebugLogging = true; Assert.True(DebugLog.Enabled);
            Config.DebugLogging = false; Assert.False(DebugLog.Enabled);
        }
        finally { Config.DebugLogging = before; }
    }

    [Fact]
    public void AllLevels_NeverThrow_WhenEnabledOrDisabled()
    {
        bool before = Config.DebugLogging;
        try
        {
            foreach (var on in new[] { true, false })
            {
                Config.DebugLogging = on;
                var ex = Record.Exception(() =>
                {
                    DebugLog.Info("info-line");
                    DebugLog.Warn("warn-line");
                    DebugLog.Debug("debug-line");
                    DebugLog.Error("error-line");
                    DebugLog.Error("with-exception", new InvalidOperationException("nested"));
                });
                Assert.Null(ex);
            }
        }
        finally { Config.DebugLogging = before; }
    }

    [Fact]
    public void Error_WritesFile_WithLevelMessageAndStack()
    {
        bool before = Config.DebugLogging;
        try
        {
            Config.DebugLogging = true;
            string marker = "MARKER_" + Guid.NewGuid().ToString("N");
            Exception thrown;
            try { throw new InvalidOperationException(marker); } catch (Exception e) { thrown = e; }

            DebugLog.Error("ctx", thrown);

            Assert.NotNull(DebugLog.Path);
            Assert.True(File.Exists(DebugLog.Path), $"log file should exist at {DebugLog.Path}");
            string text = File.ReadAllText(DebugLog.Path!);
            Assert.Contains("[ERROR]", text);
            Assert.Contains(marker, text);                       // the message
            Assert.Contains(nameof(InvalidOperationException), text); // the exception type
            Assert.Contains("at ", text);                        // a stack frame ("   at Namespace.Method")
        }
        finally { Config.DebugLogging = before; }
    }

    [Fact]
    public void Disabled_DoesNotWrite()
    {
        bool before = Config.DebugLogging;
        try
        {
            // Make sure the file exists first (resolve happens on first enabled write).
            Config.DebugLogging = true;
            DebugLog.Info("prime");
            string? path = DebugLog.Path;
            Assert.NotNull(path);

            long lenBefore = new FileInfo(path!).Length;
            Config.DebugLogging = false;
            string marker = "SHOULDNOTAPPEAR_" + Guid.NewGuid().ToString("N");
            DebugLog.Error(marker);
            DebugLog.Warn(marker);

            long lenAfter = new FileInfo(path!).Length;
            Assert.Equal(lenBefore, lenAfter);                        // nothing appended while disabled
            Assert.DoesNotContain(marker, File.ReadAllText(path!));
        }
        finally { Config.DebugLogging = before; }
    }
}

/// Serializes the config-mutating test classes so their toggles of the process-wide Config flags
/// don't race across xUnit's parallel class execution.
[CollectionDefinition("config-mutating", DisableParallelization = true)]
public class ConfigMutatingCollection { }
