# Slay The Math

An ADHD-oriented **mistake-proofing** quality-of-life mod for **Slay the Spire 2**, plus
a headless test suite that runs against the real game code. It does the arithmetic you'd
otherwise do in your head every turn — exact incoming/outgoing damage — so you don't
misjudge a lethal or a wasted turn.

"Poka-yoke" (ポカヨケ) is the Japanese manufacturing term for a *mistake-proofing* device —
a jig that won't let you assemble the part backwards. This mod does the same for StS2: it
catches the impulsive, autopilot misclicks that are usually errors, and it surfaces the
information you'd otherwise forget to check — without getting in the way when you know what
you're doing.

The mod assembly is `PokaYokeSpire` (that's the internal name you'll see in the in-game mod
list and config menu).

## Features

All features are individually toggleable in the in-game **BaseLib** config menu.

**Guards** — a confirm/reminder before a probable mistake:

1. **End turn with unspent energy** — ending your turn while you still have energy *and* an
   affordable card in hand pops a Yes/No confirm.
2. **Potion reminder in elite/boss fights** — on turn 1 of an elite or boss, if you're
   holding an unused potion, a reminder fires (all-caps red if every potion slot is full, so
   you can't even pick one up). Blocking, so you actually see it.
3. **Check your deck before taking a card** — if you take a card reward without opening your
   deck first, a "Be sure to check your deck" speed bump appears; press OK and it takes the
   card. Never blocks — just a nudge.

**HUD** — put the information where your attention already is:

4. **Centered energy counter** — moved to the middle of the screen just above your hand,
   with a height slider. Everything below hangs off it.
5. **Radial relic pins** — left-click a top-bar relic to fan a copy of it in an arc around
   the energy counter (click again to unpin), so the relics you care about stay in view.
6. **Relic counters** — right-click a relic that has a counter to pin a small blue orb
   showing its current (or current/max) value, in a row beside the energy counter.

Plus: **ctrl+click-drag** to reposition any of these UI elements. The relic pins and
counters are children of the energy counter, so they hide and draw with it.

## Layout

```
modding/
├── PokaYokeSpire/               # the mod itself
│   ├── src/                     #   Plugin, Config, guards, features, per-relic registry
│   ├── PokaYokeSpire.csproj     #   Godot.NET.Sdk 4.5.1, references the game's own DLLs
│   └── mod_manifest.json        #   loose JSON manifest (id: PokaYokeSpire, BaseLib dep)
├── PokaYokeSpire.Tests/         # contract tests — metadata-only reflection over sts2.dll
├── PokaYokeSpire.Tests.Runtime/ # runtime tests — EXECUTE real game code headless + unit
└── run-all-tests.sh             # runs both suites
```

## Tests

Two complementary suites, **61 tests**, no game launch and no running Godot required:

- **Contract** (`PokaYokeSpire.Tests`, 33) — links the real `sts2.dll` via
  `MetadataLoadContext` and asserts every game method/type/property/field the mod's Harmony
  patches depend on still exists. Catches signature-drift after a game update in seconds.
- **Runtime** (`PokaYokeSpire.Tests.Runtime`, 28) — loads `sts2.dll` at full runtime and
  *executes* game code headless (every relic and card model's real constructor runs),
  alongside pure unit + metamorphic tests of the mod's own layout math and per-relic
  counter registry.

```bash
./run-all-tests.sh      # both test suites
./headless-loop.sh      # build + install + verify, with a per-feature PASS/FAIL matrix
```

`headless-loop.sh` is the develop-and-check cycle: it builds the mod, installs it, runs the
suites against the real `sts2.dll` headless (no game launch, no window), and reports each
feature green/red. See [HEADLESS.md](HEADLESS.md) for what it can and can't verify without
launching the game.

Requires **Slay the Spire 2** installed (for `sts2.dll`) and the .NET 9 SDK. Point the
build/tests at a non-default install with the `STS2_GAME_DIR` environment variable.

## Building & installing

The mod compiles against the game's own assemblies (referenced by path in the `.csproj`);
no NuGet game libraries are redistributed here.

```bash
cd PokaYokeSpire
dotnet build -c Release
# copy bin/Release/PokaYokeSpire.dll + mod_manifest.json into:
#   <SlayTheSpire2.app>/Contents/MacOS/mods/PokaYokeSpire/
```

The ModManager scans `mods/` next to the executable for loose `.json` manifests. This is a
code-only mod — no `.pck` is needed for the current game version. Requires
[BaseLib](https://steamcommunity.com/sharedfiles/filedetails/?id=3737335127) (declared as a
dependency in the manifest) for the config menu.

## Not affiliated with Mega Crit

Slay the Spire 2 is © Mega Crit. This is an unofficial fan mod. It links against the game's
assemblies at build time but redistributes none of them; you need your own copy of the game.

## License

MIT — see [LICENSE](LICENSE).
