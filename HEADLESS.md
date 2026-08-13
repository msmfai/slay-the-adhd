# Headless verification loop

`./headless-loop.sh` — one re-runnable command that verifies the mod with **zero
on-screen footprint** (no game launch, no window, no dock icon). Run it after any change:

```
./headless-loop.sh
```

It builds the mod (Release) + installs it to the game's `mods/` dir, then runs three
isolated test passes against the real `sts2.dll` headless and prints a per-feature
PASS/FAIL matrix:

```
① Build mod (Release)                         build OK / installed
② contract  suite   — game API members intact (metadata reflection)
   runtime   suite   — pure logic + real relic/card execution
   weave     check   — all 8 Harmony patches weave, no "Bad IL" (own process)
③ per-feature matrix + ALL GREEN / FAILURES
```

## What it can and can't check (honest boundary)

**Can, headless:** each guard's decision logic (`GuardLogic`), the HUD layout math
(`Layout`), the per-relic counter registry, real relic/card model execution, that every
patched game member still exists, and that all 8 patches weave without the "Bad IL" crash
that once bricked the game.

**Can't, headless — and why:** the shipped macOS `.app` cannot run windowless. Godot's
renderer *does* go headless with `--headless` (no window is drawn), but the game's macOS
wrapper still registers a foreground NSApplication (a dock icon) within ~2s — there is no
flag that stops that. And the combat *objects* (`CombatState`/`Player`/`PlayerCombatState`)
are plain C# but reach into uninitialised Godot natives when exercised in a plain process,
which segfaults. So **live in-game UI behaviour** (does the popup actually appear, does the
relic pin render in the right place) still needs either a real play session or non-macOS
headless CI. The loop verifies everything up to that last mile.

## Driving the real game — `./drive-headless.sh`

There's a second script that launches the **real game**, invisibly and silently, and pilots
it into a live combat:

- **invisible** — `--headless` (no window) + `LSUIElement` set on the app bundle just for the
  run (no dock icon; reverted on exit). A probe confirms zero visible windows the whole time.
- **silent** — the driver mod sets the game's own `TestMode.IsOn`, which gates every audio
  call, plus `--audio-driver Dummy`.
- **piloted** — the driver (`PokaYokeHarness/`) unlocks the game's dev features, deletes any
  disposable in-progress run, and starts a run directly into combat (replicating the game's
  debug `StartNewRun`), bypassing the main menu.
- **isolated + safe** — runs with only PokaYokeSpire + BaseLib (the other Workshop mods are
  moved aside for the run and always restored); the driver and the local BaseLib copy are
  removed on exit so normal play is untouched.

What it proves, live and repeatably, ending in a per-feature ✅/❌ matrix:

- the mod loads, **all 8 Harmony patches weave with no Bad IL** (in isolation and, separately,
  in the full mod stack);
- the game is piloted into a live combat;
- **all six features fire** — Guards 1/2/3 and Features 4/5/6 each print their `FIRED` marker.

How the last part works despite headless not building the combat UI scene: the Godot runtime
*is* live in the running game, so the driver constructs the specific node each feature hooks
(`new NEnergyCounter()`, `new NEndTurnButton()`, `new NCardRewardSelectionScreen()`), injects
the **real live combat state** into it, and invokes the mod's patch methods directly — running
the actual patched code paths against real game state. (The original game methods —
`_Process`/`AnimIn`/`OnRelease` — NRE headless because they drive tweens/UI, so the driver
calls our Prefix/Postfix directly rather than through them.)

Two findings worth keeping: (1) the mod's own patches are fine — they weave cleanly even in
the full stack, so breakage isn't a patch/Bad-IL problem; (2) another Workshop mod crashes the
main-menu navigation (`NMainMenuSubmenuStack.GetSubmenuType` throws), which likely contributes
to features seeming broken in the real game.

Still not covered (needs a real session): the *visual* correctness of the HUD features — that
the relic pin sits in the right spot, the popup renders legibly. The drive proves the code
paths execute against real state; it can't screenshot a headless run.

## Notes for anyone driving the real game (not the default path)

- Launching the binary directly needs `steam_appid.txt` (contents: `2868840`) next to the
  executable, or Steam init fails and pops an error window.
- The game ignores `SIGTERM`; kill it with `kill -9` / `pkill -9 -f "Contents/MacOS/Slay the Spire 2"`.
- The game ships a full autoplayer (`MegaCrit.Sts2.Core.AutoSlay.AutoSlayer`) gated behind
  `NGame.IsReleaseGame()` (hard-coded `true`). Harmony-patching that to `false` unlocks it
  (see the RL kit's `bridge_mod`) — useful if a windowed run is ever acceptable.
