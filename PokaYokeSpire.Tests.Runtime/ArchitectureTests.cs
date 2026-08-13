using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;
using Xunit.Abstractions;

namespace PokaYokeSpire.Tests.Runtime;

/// <summary>
/// META-TESTS that enforce the correctness-by-construction invariants at the SOURCE level, so a
/// future feature that bypasses an abstraction fails the suite instead of shipping a regression
/// (CLAUDE.md § "Architecture"). These scan the mod's feature/guard source files:
///   • every Harmony patch body is routed through the guarded Feature runner (invariants 2, 3, 8);
///   • every file that attaches UI is input-safe — it goes through Overlay/CardDisplay or applies
///     UiSafety.Passthrough (invariant 1);
///   • no feature creates a raw display card — cards go through CardDisplay (invariant 4).
/// </summary>
public class ArchitectureTests
{
    private readonly ITestOutputHelper _o;
    public ArchitectureTests(ITestOutputHelper o) => _o = o;

    private static string SrcDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (; dir != null; dir = dir.Parent)
        {
            var src = Path.Combine(dir.FullName, "PokaYokeSpire", "src");
            if (File.Exists(Path.Combine(dir.FullName, "CLAUDE.md")) && Directory.Exists(src)) return src;
        }
        throw new DirectoryNotFoundException("could not locate PokaYokeSpire/src");
    }

    /// Files that host Harmony patches — the feature + guard sources (NOT the Core abstractions).
    private static IEnumerable<(string name, string text)> FeatureSources()
    {
        var src = SrcDir();
        foreach (var sub in new[] { "Features", "Guards" })
        {
            var dir = Path.Combine(src, sub);
            if (!Directory.Exists(dir)) continue;
            foreach (var f in Directory.GetFiles(dir, "*.cs", SearchOption.AllDirectories))
                yield return (Path.GetFileName(f), File.ReadAllText(f));
        }
    }

    [Fact]
    public void EveryPatchBodyIsRoutedThroughTheGuardedRunner()
    {
        var offenders = new List<string>();
        foreach (var (name, text) in FeatureSources())
        {
            bool hasPatch = text.Contains("static void Postfix") || text.Contains("static bool Prefix") || text.Contains("static void Prefix");
            if (!hasPatch) continue;
            bool guarded = text.Contains("Feature.Run") || text.Contains("Feature.RunUi") || text.Contains("Feature.Prefix");
            if (!guarded) offenders.Add(name);
        }
        Assert.True(offenders.Count == 0,
            "these patch files bypass the Feature runner (invariant 2/3): " + string.Join(", ", offenders));
    }

    [Fact]
    public void EveryUiAttachmentIsInputSafe()
    {
        var offenders = new List<string>();
        foreach (var (name, text) in FeatureSources())
        {
            if (!text.Contains(".AddChild(")) continue;   // only files that attach nodes
            bool safe = text.Contains("Overlay.") || text.Contains("CardDisplay.") || text.Contains("UiSafety.Passthrough");
            if (!safe) offenders.Add(name);
        }
        Assert.True(offenders.Count == 0,
            "these files attach UI without going through Overlay/CardDisplay/UiSafety (invariant 1): " + string.Join(", ", offenders));
    }

    [Fact]
    public void NoFeatureCreatesARawDisplayCard()
    {
        // Cards must be shown via CardDisplay (the game's create→defer→ready path) — never NCard.Create
        // directly, which renders "broken card" when added mid-construction (invariant 4).
        var offenders = FeatureSources().Where(f => f.text.Contains("NCard.Create")).Select(f => f.name).ToList();
        Assert.True(offenders.Count == 0,
            "these features create raw NCards instead of using CardDisplay (invariant 4): " + string.Join(", ", offenders));
    }

    [Fact]
    public void CoreAbstractionsExist()
    {
        var src = SrcDir();
        Assert.True(File.Exists(Path.Combine(src, "Core", "Features.cs")), "Core/Features.cs missing");
        Assert.True(File.Exists(Path.Combine(src, "Core", "Overlay.cs")), "Core/Overlay.cs missing");
        Assert.True(File.Exists(Path.Combine(src, "Core", "CardDisplay.cs")), "Core/CardDisplay.cs missing");
        Assert.True(File.Exists(Path.Combine(src, "UiSafety.cs")), "UiSafety.cs missing");
    }

    [Fact]
    public void CardDisplay_DefersCardCreation()
    {
        // The broken-card bug: adding an NCard during a screen's own _Ready renders "broken card". The
        // fix is CardDisplay building on a DEFERRED call. Pin the mechanism so it can't be removed.
        var text = File.ReadAllText(Path.Combine(SrcDir(), "Core", "CardDisplay.cs"));
        Assert.Contains("CallDeferred", text);
    }

    [Fact]
    public void UiAbstractions_ApplyInputSafety()
    {
        // The end-turn-button-eating-clicks regression: a mod overlay swallowed a click. Overlay and
        // CardDisplay both make their subtree mouse-transparent — pin that they call UiSafety.Passthrough.
        var src = SrcDir();
        Assert.Contains("UiSafety.Passthrough", File.ReadAllText(Path.Combine(src, "Core", "Overlay.cs")));
        Assert.Contains("UiSafety.Passthrough", File.ReadAllText(Path.Combine(src, "Core", "CardDisplay.cs")));
    }

    [Fact]
    public void CoreCatchBlocks_SurfaceErrors_ViaDebugLog()
    {
        // Silent catch blocks in the UI abstractions hid failures (e.g. a gem's art silently dropped).
        // Every Core UI helper must reference DebugLog so a swallowed error is at least recorded.
        var src = SrcDir();
        foreach (var f in new[] { "Core/Overlay.cs", "Core/CardDisplay.cs", "Core/Features.cs" })
            Assert.Contains("DebugLog", File.ReadAllText(Path.Combine(src, f.Replace('/', Path.DirectorySeparatorChar))));
    }
}
