# Slay the ADHD — working notes for LLM contributors

## ARCHITECTURE: correctness by construction (read this first)

An LLM can't see the running game, so bugs can't be found by looking — they must be made
**impossible to write**. Whole classes of regression (input capture, exceptions leaking into
the game, "broken card", eyeballed layout, duplicated/half-built overlays, main-thread
stalls) are structural. Do NOT hand-roll around them; go through the abstractions below. The
meta-tests in `PokaYokeSpire.Tests.Runtime/ArchitectureTests.cs` FAIL the build if you don't.

**The invariants, and the one sanctioned way to satisfy each:**

1. **A patch never crashes or blocks the game, and runs only when it should.** Every Harmony
   patch body is a one-liner delegating to the guarded runner — never a free-floating body:
   - `Feature.Run("name", gate, body)` (postfix/void) — fail-open + auto-disable + gated;
   - `Feature.RunUi(...)` — also honours the master `DisableAllOverlays` kill switch;
   - `Feature.Prefix("name", gate, boolBody, passThrough:true)` — for a confirm-then-proceed
     guard; on gate-off / auto-disable / any error it returns "let the original run", so a
     guard can never trap a core action (ending a turn).
   (`src/Core/Features.cs`.)
2. **A mod overlay never eats a click.** Attach UI only via `Overlay.Attach(parent, name,
   factory)` (mouse-transparent + idempotent by construction) or, if you must `AddChild` a
   cloned game subtree yourself, call `UiSafety.Passthrough(node)` right after. Never leave a
   raw `AddChild` of a Control onto a game node without one of these. (`src/Core/Overlay.cs`,
   `src/UiSafety.cs`.)
3. **A displayed card always renders.** Show cards only through `CardDisplay.Attach(...)` — the
   game's create→defer→ready path. Never `NCard.Create` in a feature: adding a card during a
   screen's `_Ready` leaves it as "broken card". (`src/Core/CardDisplay.cs`.)
4. **Placement is data, not intuition** — see the spatial section below (pure layout functions
   + lints as source of truth + runtime self-correction). Applies to ALL placement.
5. **Expensive work is bounded and off the game thread.** Snapshot to plain value-structs on
   the game thread, solve on a `Task`, cache a result the per-frame hook reads. (The lethal
   solver + `CombatSnapshot` do this.)

Rule of thumb: if you're writing a `try { … } catch` inside a patch, a raw `AddChild`, or an
`NCard.Create` in a feature — stop; you're reintroducing a defect class. Use the abstraction.
Verification (`headless-loop.sh`) then only *confirms* correctness; it is not how you find it.

## SPATIAL PLACEMENT: LLMs are blind, so never eyeball coordinates

An LLM editing this mod **cannot see the running game**. There is no way to visually
verify a layout on macOS (the combat/reward UI does not instantiate in `--headless`, and
driving the real game erases the player's save). Any coordinate an LLM "eyeballs" is a
guess it has no way to check — and it *will* be wrong (cards occluding each other, text
off-screen, overlays stacked on top of each other).

**Therefore all spatial placement MUST be done as data, not intuition:**

1. **Build a hypergraph over the UI elements.** Each element is a node carrying its
   rectangle (`UiRect`: x, y, w, h). Hyperedges connect the elements whose relationship
   matters.
2. **Compute pairwise, triplet-wise, and quad-wise features over that graph**, e.g.
   - *pairwise* — intersection, overlap fraction, horizontal/vertical gap, visible sliver
     (the step between two overlapping cards in a fan);
   - *triplet* — collinearity, left-to-right ordering, even spacing;
   - *quad* — mirror symmetry / balanced fan / grid alignment.
   These live in `src/Spatial/Spatial.cs`.
3. **Write lints and tests over those features** and make them the source of truth:
   - a *lint* is a predicate over the graph (`OcclusionViolations`, `MinSpacingViolations`,
     `OutOfBounds`, group-vs-group non-occlusion) that returns the offending edges;
   - a *test* pins the layout math for representative inputs (deck of 5..40 at 1080p/1440p)
     and asserts every lint is clean.
   Spatial tests live in `PokaYokeSpire.Tests.Runtime/SpatialLayoutTests.cs`.
4. **Make the runtime feature correct by construction:** compute the layout with the pure
   function, then *run the same lints at runtime against the measured rectangles* and
   self-correct (shrink scale / increase separation) until they pass — never trust the
   guessed constants to be right on the real screen.

Rule of thumb: if you find yourself typing a magic pixel offset and reasoning "that looks
about right," stop. Express the intent as a spatial invariant, add the lint + test, and let
the feature enforce it. The tests are the only eyes an LLM has here.

## Verify without erasing saves

`headless-loop.sh` builds + installs + runs all in-process tests (contract + runtime +
Harmony weave) with **zero on-screen footprint and without touching the save**. Use it.
Do **NOT** run `drive-headless.sh` — it deletes the in-progress run and overwrites the save
slot. The player cares about their saves.
