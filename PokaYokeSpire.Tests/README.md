# PokaYokeSpire.Tests

Contract tests that **link the real game** (`sts2.dll`) and assert every method, type,
property and field the mod depends on still exists with the shape the mod assumes.

Run: `./run-tests.sh`  (~a few seconds, no game launch, no Godot runtime).

This is one of **two** suites. Its sibling `../PokaYokeSpire.Tests.Runtime` goes further:
it loads `sts2.dll` at full runtime and *executes* game code headless (real relic/card
constructors) alongside pure unit + metamorphic tests. Run both with
`../run-all-tests.sh`.

## Why this exists
Most of the mod's bugs were "my assumption about a game member was wrong" — a method on
the wrong namespace, a nested enum, a renamed property — and they only surfaced after a
full relaunch. These tests catch that class of bug in seconds, before shipping.

## How it works
`GameApi` loads `sts2.dll` via **MetadataLoadContext** (metadata only — no execution, no
Godot native library needed), then each `[Fact]` asserts a member exists. It auto-finds
the game dir under Steam; override with `STS2_GAME_DIR`.

## What this does NOT cover (honest limits)
- **Combat behavioral tests** (e.g. "vulnerable multiplies damage by 1.5"). The engine's
  damage logic is instance methods coupled to `CombatState`/`Player` and Godot, so it
  can't be executed headless. Those live in the mechanics reimplementation at
  `../../rlkit/sts2-rl-agent` (Python, ~4600 tests) — a complementary suite. Note the
  runtime project DOES execute the parts of game code that construct in isolation (all
  relic/card models), so metamorphic tests over *those* live there, not here.
- **Harmony "Bad IL" / patch-conflict detection.** Attempted and shelved: in isolation the
  mod's patches weave cleanly; the in-game Bad IL is a *multi-mod stacking* conflict
  (RitsuLib/BaseLib patch hundreds of methods too) and only manifests when the woven method
  is actually JIT'd at runtime, which needs a live Godot instance. `RuntimeHelpers.PrepareMethod`
  JITs the original, not Harmony's replacement, so it can't detect it headless.

## When to run
Before shipping any mod change, and after any game update — a failing test names the exact
member that moved.
