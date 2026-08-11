# StS2 QOL mod feasibility (separate from the RL project)

Assessed 2026-08-10 from the decompiled `MegaCrit.Sts2.Core.Modding.*` source. **Verdict: very easy — first-class official mod support.** This is among the most mod-friendly setups possible.

## Why it's easy

- **Official mod loader + in-game manager.** `ModManager` scans a mods directory and Steam Workshop (`ModSource.ModsDirectory` / `ModSource.SteamWorkshop`); there's a full in-game modding screen (`NModdingScreen`) to enable/disable and view mod info. A user-consent gate (`PlayerAgreedToModLoading`) shows a warning once before mods load. Mods load at startup only (no hot-reload).
- **Clean C# entry point.** A mod is: a `.pck` in the mods dir, an optional same-named `.dll` beside it, and a `res://mod_manifest.json` inside the pck. The DLL is loaded via `AssemblyLoadContext`; any class marked `[ModInitializer("MethodName")]` has that method invoked at load. That's the whole hook.
- **Official content API.** `ModHelper.AddModelToPool<TPool, TModel>()` adds cards/relics/etc. to game pools without touching game code — content mods need no patching.
- **Behavior patching is built in.** The game bundles `0Harmony.dll` + `MonoMod.*`, so QOL tweaks (which usually patch *existing* behavior) use Harmony prefix/postfix from inside the same `ModInitializer`.
- **Un-obfuscated + documented.** Full symbol names + the shipped `sts2.xml` doc comments + the decompiled source already in hand (`rlkit/sts2-rl-agent/decompiled/`) → you can find the exact method to patch by grep.
- **Paved road exists.** Template: [jiegec/STS2FirstMod](https://github.com/jiegec/STS2FirstMod). Tutorials: [tutorials.sts2modding.com](https://tutorials.sts2modding.com/en/), [GlitchedReme/SlayTheSpire2ModdingTutorials](https://github.com/GlitchedReme/SlayTheSpire2ModdingTutorials). Distribution: Thunderstore + Steam Workshop.

This aligns with your usual QOL approach ([[qol-release-v1-progress]] etc.): a proper Harmony code mod is the robust/declarative path here — no keystroke-automation overlays needed, because you patch the game's own logic directly.

## `mod_manifest.json` shape
```json
{ "pck_name": "MyQolMod", "name": "My QOL Mod", "author": "you",
  "description": "...", "version": "1.0.0" }
```
`pck_name` MUST match the `.pck` filename (case-insensitive) or the load throws.

## Minimal QOL mod skeleton
```csharp
using HarmonyLib;
using MegaCrit.Sts2.Core.Modding;

[ModInitializer(nameof(Init))]
public class MyQolMod {
    public static void Init() {
        new Harmony("you.myqolmod").PatchAll();
    }
}

// example patch target below
```

## Concrete worked example — the game already has a speed setting
`FastModeType { None, Normal, Fast, Instant }` (`Core.Settings`) is read all over combat, e.g. `Whirlwind.cs`: `FastMode == Fast ? 0.2 : 0.3`. So "faster combat" QOL is trivial:
- **Easiest:** a Harmony postfix on the `PrefsSave.FastMode` getter forcing `Instant` — one method, a few lines.
- Or add UI to expose `Instant` if the settings screen only surfaces `Normal/Fast`.
There's also `NHitStop.SetTimeScale` (VFX hitstop) and `Engine`-level time controls for animation-skip mods.

## Difficulty tiers
- **Trivial (an afternoon):** behavior tweaks via a single Harmony patch — force fast/instant mode, always-show numeric values, disable screen shake (`ScreenRumbleInstance`), auto-proceed on reward screens, wider tooltips.
- **Easy (a weekend):** new content via `ModHelper.AddModelToPool` — a card, relic, or potion (art/text as Godot resources in your pck).
- **Moderate:** new mechanics/characters, or UI screens (needs Godot scene work in the pck).

## What you'd need to install to actually build one
Neither is installed yet:
- **.NET 9 SDK** — compile the mod DLL against `sts2.dll` + `0Harmony.dll` (both in the game's `data_sts2_macos_arm64/`).
- **Godot 4.5.1 Mono** — pack the `.pck` (manifest + any assets). (A code-only QOL mod's pck can be nearly empty apart from the manifest.)

Then: `git clone STS2FirstMod` as a base, reference the two DLLs, write the initializer + Harmony patch, `dotnet build`, pack the pck in Godot, drop both in the mods dir, accept the in-game mod warning.
