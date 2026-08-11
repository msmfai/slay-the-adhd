# PokaYokeSpire.Tests.Runtime

Execution-based tests. Unlike the sibling `PokaYokeSpire.Tests` (metadata-only
contract checks), this project **loads the game's real `sts2.dll` at full runtime** and
runs its code headless, so we test the mod's logic against actual game objects — no
running Godot, no launching the game.

## How the scaffolding works

- `Bootstrap.cs` — a `[ModuleInitializer]` hooks `AssemblyLoadContext.Default.Resolving`
  to the installed game directory (env `STS2_GAME_DIR`, else the default macOS path), so
  `sts2.dll`'s transitive deps (`GodotSharp`, etc.) resolve at load time.
- The `.csproj` references `sts2.dll` / `GodotSharp` / `0Harmony` / `BaseLib` with
  `<Private>true</Private>` so they're copied next to the test DLL — needed for xUnit's
  test *discovery* pass, which runs before the ModuleInitializer.
- `GameRelics.cs` — enumerates every concrete `RelicModel` subclass and instantiates it
  via `Activator.CreateInstance(t, nonPublic: true)`. This is the reusable fixture for
  running real relic code in isolation.
- The mod's pure logic (`Layout.cs`, `Relics/RelicRegistry.cs`) is compiled straight into
  this project (see `<Compile Include>` in the csproj) and tested directly.

## What's covered

| File | Kind | What it tests |
|------|------|---------------|
| `LayoutTests.cs` | unit + metamorphic | radial/row placement math — on-circle, top-balanced, mirror-symmetric, monotonic spacing |
| `CounterDisplayTests.cs` | unit | current-only vs current/max text rendering |
| `RelicRuntimeTests.cs` | integration + metamorphic | every real relic constructs; registry key = class name; custom override; bad-fn fallback; default = `DisplayAmount` |
| `ModelRuntimeTests.cs` | integration + metamorphic | real card models also construct headless (scaffolding reaches beyond relics) |

## Run

```
./run-tests.sh                 # this project only
../run-all-tests.sh            # contract + runtime, both suites
```

Requires the game installed (for `sts2.dll`) and the nix `dotnet-sdk_9`.
