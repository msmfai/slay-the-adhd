# Slay the ADHD — working notes for LLM contributors

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
