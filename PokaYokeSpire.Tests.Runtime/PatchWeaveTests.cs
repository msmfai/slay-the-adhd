using System;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Xunit;

namespace PokaYokeSpire.Tests.Runtime;

/// Loads the BUILT mod assembly and applies its Harmony patches in-process, asserting
/// every target method weaves without throwing (the "Bad IL" failure mode that bricked
/// the game reproduced here, headless, as a regression guard). Verifies the patch count
/// too, so a silently-dropped patch is caught.
public class PatchWeaveTests
{
    private static string? FindModDll()
    {
        // Prefer the fresh build output; fall back to the installed copy.
        string[] candidates =
        {
            "../PokaYokeSpire/bin/Release/PokaYokeSpire.dll",
            "../PokaYokeSpire/.godot/mono/temp/bin/Release/PokaYokeSpire.dll",
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) +
              "/Library/Application Support/Steam/steamapps/common/Slay the Spire 2/SlayTheSpire2.app/Contents/MacOS/mods/PokaYokeSpire/PokaYokeSpire.dll",
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    [Fact]
    public void AllPatches_Weave_NoBadIL()
    {
        var dll = FindModDll();
        Assert.True(dll != null, "built PokaYokeSpire.dll not found — build the mod first");

        var modAsm = Assembly.LoadFrom(dll!);
        var harmony = new Harmony("PokaYokeSpire.Tests.Runtime.weave");
        // Throws (BadImageFormatException / "Bad IL range") here if any patch can't weave.
        harmony.PatchAll(modAsm);

        var patched = harmony.GetPatchedMethods().Select(m => m.DeclaringType?.Name + "." + m.Name).ToArray();
        // The mod applies 8 patches (3 guards + reward/deck plumbing + 3 HUD hooks).
        Assert.True(patched.Length >= 8, $"expected >=8 woven methods, got {patched.Length}: {string.Join(", ", patched)}");
        harmony.UnpatchAll(harmony.Id);
    }
}
