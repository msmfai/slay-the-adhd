# Goal: static coverage of card/power effects in the turn simulator

**Objective.** Instead of discovering simulator gaps one at a time from play, mine all 577 card
models + 260 power models in the decompiled source, bucket every combat effect by whether the
pure `TurnSim` can model it, and close as many modelable gaps as possible — each with a unit
test. Whatever can't be modeled must at least be *logged* by the reader's coverage flags so it's
never a silent wrong number.

Decompiled source: `rlkit/sts2-rl-agent/decompiled/MegaCrit.Sts2.Core.Models.{Cards,Powers}/`.

## What the sim already models (baseline — do NOT re-report these as gaps)

Card effects: attack damage (DamageVar / CalculatedDamage, multi-hit, AOE, X-cost attacks,
enchant mult/add), block (BlockVar), FlatBlock (card-applied Plating/Metallicize), self
Strength/Dexterity/Vigor gain, apply Vulnerable/Weak to enemies, enemy Strength-loss
(`StrengthLoss`), energy gain, and the bespoke cards Body Slam, Second Wind, Entrench, Rage,
Evil Eye. Exhaust keyword sets "exhausted this turn".

Powers already read off the creature (so already reflected): player Strength, Dexterity, Weak,
Frail, Vulnerable, Shrink, Intangible, Vigor, Rage; enemy Strength, Weak, Vulnerable, Hardened
Shell (damage cap). Mitigation relics: Tungsten Rod, Beating Remnant. Scheduled DoT (offense
+d): Poison, Constrict, Inferno.

## Buckets

- **MODELABLE-EXACT** — deterministic this-turn damage/block/mitigation the pure sim can compute
  exactly (self buff/debuff powers, enemy attack debuffs, HP-cost/self-damage on play,
  conditional block/damage with a snapshot-known condition). → implement + test.
- **MODELABLE-APPROX** — value depends on state the sim tracks only partially (e.g. block =
  discard-pile size, damage scaling with attacks played this turn); model as a documented
  conservative snapshot. → implement where cheap, else log.
- **OUT-OF-SCOPE (log only)** — orbs/channels, card draw/scry/reveal, next-turn/persistent
  effects, summons/pets, deck-pile manipulation, healing, on-kill triggers, gold/economy,
  cost-reduction of other cards. The reader logs these; the sim treats them as no-ops.

## Status

- [x] Baseline modeled set (above).
- [x] Static gap catalog — all 577 cards swept (A–D 170, E–L 140, M–R 129, S–Z ~140).
- [x] Implement MODELABLE-EXACT batch 1 (below).
- [x] Build + run tests to green the whole thing — **272/272**.
- [x] Solver architecture: demonic heuristic (worst-case-by-exclusion) formalized; lean analyzer
      routes trivial single-enemy hands to a closed-form two-knapsack fast path, DFS otherwise,
      proven equal by a 500-case cross-check.
- [ ] Batch 2: outgoing multipliers (Lethality/Double Damage/Tracking), counter-driven hits
      (Finisher/Lunar Blast/Conflagration), block multipliers (Shadowmeld/Unmovable/Stack).
- [ ] Confirm every remaining card hits a coverage log, not a silent no-op.

## Implemented (batch 1 — this pass)

- **Self HP-cost on play** — generic via the `HpLossVar` key (Bloodletting, Blood Wall,
  Breakthrough, Brand, Demonic Shield, Hemokinesis, Offering, …) → unblockable HP added to the
  defense number.
- **End-of-turn-in-hand HP loss** — extended to `HpLossVar` (BadLuck 13, Beckon 6) and Regret
  (= hand size), on top of the existing DamageVar path (Burn/Toxic/Decay).
- **Self Intangible** — generic via `PowerVar<IntangiblePower>` (Apparition, Wraith Form).
- **Doom execute** — generic via `PowerVar<DoomPower>` (Oblivion, End of Days, Negative Pulse,
  No Escape): an enemy at/under its Doom counts as killed for the kill glow / offense.
- **Demonic Shield** → double current block (reuses Entrench) + its −1 HP.
- **Expose** → strip target's Block. **Dismantle** → hits ×2 if target Vulnerable.
- **Resonance / Fight Me** → direct enemy Strength change (Strength-loss made signed).
- Perfected Strike / Rend / Bully / Times-Up-style scaling already handled by the
  calculated-damage fallback (snapshot).

Tests added for each in `TurnSimTests.cs`.

## Deferred to batch 2 (modelable, higher cost)

- **Outgoing multipliers** — Lethality (first-attack +%), Double Damage (×2), Tracking (×vs
  Weak). Needs a player outgoing-multiplier + first-attack flag.
- **Counter-driven hit/damage** — Finisher (attacks played), Lunar Blast (skills played),
  Conflagration (cards played), Gold Axe (cards played), Helix Drill (energy spent). Needs
  per-turn counters in sim state (these become *exact*, not approx, once tracked).
- **Block multipliers** — Shadowmeld (×2^N), Unmovable (double first N block), Stack (= discard
  size, snapshot-approx).
- **Bespoke** — Malaise (X-cost −X Str/+X Weak), MoltenFist (double target Vuln), Omnislice
  (echo realized damage), Buffer (negate one hit), Debilitate (amplify Vuln/Weak), Maul/Rampage/
  Claw escalation, One-Two-Punch replay.

## Out of scope (log-only, confirmed across all ranges)

Orbs / channels / Focus / Stars; card draw / scry / tutor / pile manipulation; next-turn or
whole-combat persistent powers (Demon Form, Corruption, Barricade, Blur, Thorns/Caltrops); Osty
pet cards; poison/DoT application (future turns — offense +d stays current-only by decision);
healing; on-kill triggers; gold/economy; cost-reduction / extra-play powers; multiplayer-only
cards. The reader's coverage log flags any card/power in these buckets so nothing is a silent
wrong number.
