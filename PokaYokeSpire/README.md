# Poka-Yoke Spire

Mistake-proofing guards for Slay the Spire 2. Confirm-before-you-probably-misclick,
tuned for ADHD: catch the impulsive/autopilot actions that are usually errors.

## v1 guards (test build)

1. **End turn with energy + playable card** — ending your turn while you still have
   unspent energy AND an affordable card in hand pops a Yes/No confirm.
   Hook: `NEndTurnButton.CallReleaseLogic` prefix. Uses the game's own
   `PlayerCombatState.Energy` and `HasCardsToPlay()`.
2. **Potion reminder in elite/boss fights** — the first card you play each turn in an
   elite or boss fight, while holding an unused potion, asks "are you sure you don't
   want a potion?". Once per turn (not per card). Potion use does NOT trip it.
   Hook: `CardModel.EnqueueManualPlay` prefix + `CombatState.Encounter.RoomType`.

Both are individually toggleable (`src/Config.cs`). Both intercept *before* the action
is enqueued, and fail-open (if the popup can't load, the action just proceeds).

## Project layout
```
PokaYokeSpire/
├── mod_manifest.json          # goes INSIDE the .pck at res://mod_manifest.json
├── PokaYokeSpire.csproj       # Godot.NET.Sdk 4.5.1, refs sts2.dll + 0Harmony.dll
└── src/
    ├── Plugin.cs              # [ModInitializer("Init")] → Harmony.PatchAll
    ├── Config.cs              # per-guard toggles
    ├── PopupHelper.cs         # spawns the game's NVerticalPopup (native yes/no)
    └── Guards/
        ├── EndTurnEnergyGuard.cs
        └── ElitePotionGuard.cs
```

## Build (needs .NET 9 SDK; full Godot app NOT required to compile)

`Godot.NET.Sdk` and `GodotSharp.dll` restore from NuGet — compiling only needs the
.NET 9 SDK. The Godot **app** is only needed if you pack the `.pck` with the editor;
you can avoid it (see packing option B).

```bash
cd "modding/PokaYokeSpire"
dotnet build -c Release        # → bin/Release/PokaYokeSpire.dll
```

### Pack the .pck (holds only mod_manifest.json for a code-only mod)
- **Option A — Godot 4.5.1 Mono editor:** create a minimal project containing
  `mod_manifest.json` at `res://`, export a PCK named `PokaYokeSpire.pck`.
- **Option B — godotpcktool (no editor):** pack the manifest directly:
  ```bash
  godotpcktool PokaYokeSpire.pck --add mod_manifest.json res://mod_manifest.json
  ```
  (VERIFY the pack format matches Godot 4.5.x; GDRE Tools can also build/inspect PCKs.)

## Install
Drop both files together into the game's local mods directory (same layout the Steam
Workshop mods use — a folder with `Name.dll` + `Name.pck` + manifest):
```
~/Library/Application Support/SlayTheSpire2/mods/PokaYokeSpire/
    PokaYokeSpire.dll
    PokaYokeSpire.pck
```
(Confirm the exact mods dir name from `ModManager` at first run — it also loads from
the Steam Workshop path. `mods/` did not exist yet; create it.)
Then launch, accept the one-time mod-loading warning, and enable Poka-Yoke Spire in the
in-game mod menu.

## Verify-at-build checklist (things that need the compiler to confirm)
- `ModInitializer` method convention (instance `Init()` vs static) — matches STS2FirstMod.
- Popup parenting in `PopupHelper` (CanvasLayer on root vs combat's GlobalUi layer).
- `LocString(table, key)` constructor shape and the `GENERIC_POPUP.confirm/cancel` keys.
- Re-invoking the private `CardModel.EnqueueManualPlay` after confirm (Traverse call).
All are localized to one file each and marked with comments.

## Roadmap (more guards to add next)
Low HP + unused potions on end-turn; shop-exit with unspent gold + removal available;
rest-site wrong-choice (rest at high HP / forge at low HP before boss). Then migrate
`Config` to BaseLib so toggles get in-game UI (like Minty Spire 2).
