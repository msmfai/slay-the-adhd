#!/usr/bin/env bash
# ============================================================================
# Slay The Math — headless verification loop.
#
# One re-runnable command, ZERO on-screen footprint (no game launch, no window,
# no dock icon). It:
#   1. builds the mod Release DLL (proves the patches + logic still compile
#      against the real game API) and installs it to the game's mods/ dir;
#   2. runs both test suites against the REAL sts2.dll headless:
#        - contract  (metadata: every patched game member still exists)
#        - runtime   (executes real game code: builds real Player/CombatState,
#                     verifies each guard's logic + the API it reads, weaves all
#                     Harmony patches in-process to catch "Bad IL" regressions);
#   3. prints a per-feature PASS/FAIL matrix and exits non-zero on any failure.
#
# Why headless-in-process and not "launch the game": the shipped macOS .app
# wrapper always registers an NSApplication (dock icon) even with Godot's
# renderer headless, so there is no way to drive the real UI without a visible
# app on macOS. The combat/logic classes are plain C# (not Godot nodes), so we
# construct and exercise them directly instead — same code, no window.
# ============================================================================
set -uo pipefail
MOD_ROOT="$(cd "$(dirname "$0")" && pwd)"
GAME_MODS="$HOME/Library/Application Support/Steam/steamapps/common/Slay the Spire 2/SlayTheSpire2.app/Contents/MacOS/mods/PokaYokeSpire"

DOTNET_PATH="$(nix build --no-link --print-out-paths 'nixpkgs#dotnet-sdk_9' 2>/dev/null)"
DOTNET="$DOTNET_PATH/bin/dotnet"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 DOTNET_CLI_HOME=/tmp/dotnet_home
TRX_DIR="$(mktemp -d)"; trap 'rm -rf "$TRX_DIR"' EXIT

say() { printf '%s\n' "$*"; }
rule() { printf '%s\n' "────────────────────────────────────────────────────────────"; }

# ---- 1. Build + install ----------------------------------------------------
rule; say "① Build mod (Release)"
if ! "$DOTNET" build -c Release "$MOD_ROOT/PokaYokeSpire/PokaYokeSpire.csproj" >/"$TRX_DIR/build.log" 2>&1; then
  say "  BUILD FAILED:"; grep -iE "error" "$TRX_DIR/build.log" | head -10; exit 1
fi
say "  build OK"
# Godot.NET.Sdk emits to .godot/mono/temp/bin/Release (not bin/Release).
BUILT_DLL="$MOD_ROOT/PokaYokeSpire/.godot/mono/temp/bin/Release/PokaYokeSpire.dll"
[ -f "$BUILT_DLL" ] || BUILT_DLL="$MOD_ROOT/PokaYokeSpire/bin/Release/PokaYokeSpire.dll"
mkdir -p "$GAME_MODS"
if cp "$BUILT_DLL" "$GAME_MODS/" 2>/dev/null && cp "$MOD_ROOT/PokaYokeSpire/mod_manifest.json" "$GAME_MODS/" 2>/dev/null; then
  INSTALLED=1; say "  installed to mods/PokaYokeSpire/"
else
  INSTALLED=0; say "  (install skipped — build output or game dir missing)"
fi

# ---- 2. Run suites, each in its OWN process (emit trx) ---------------------
# The in-process Harmony weave check does global PatchAll on the mod assembly, so it runs
# ISOLATED from the rest — a host crash there can't truncate the other suites' results.
run_suite() { # <csproj> <trx-name> [filter]
  local flt=(); [ -n "${3:-}" ] && flt=(--filter "$3")
  "$DOTNET" test -c Release "$1" "${flt[@]}" \
    --logger "trx;LogFileName=$2" --results-directory "$TRX_DIR" >/dev/null 2>&1
}
rule; say "② Run test suites (headless, against real sts2.dll)"
run_suite "$MOD_ROOT/PokaYokeSpire.Tests/PokaYokeSpire.Tests.csproj" contract.trx; C=$?
say "  contract  suite done"
run_suite "$MOD_ROOT/PokaYokeSpire.Tests.Runtime/PokaYokeSpire.Tests.Runtime.csproj" runtime.trx "FullyQualifiedName!~PatchWeave"; R=$?
say "  runtime   suite done"
run_suite "$MOD_ROOT/PokaYokeSpire.Tests.Runtime/PokaYokeSpire.Tests.Runtime.csproj" weave.trx "FullyQualifiedName~PatchWeave"; W=$?
say "  weave     check done (isolated process)"

# Flatten all results to "outcome<TAB>testname" lines.
ALL="$TRX_DIR/all.txt"; : > "$ALL"
for trx in "$TRX_DIR"/contract.trx "$TRX_DIR"/runtime.trx "$TRX_DIR"/weave.trx; do
  [ -f "$trx" ] || continue
  grep -oE '<UnitTestResult[^>]*>' "$trx" | while read -r line; do
    name=$(printf '%s' "$line" | grep -oE 'testName="[^"]+"' | head -1 | sed 's/testName="//;s/"$//')
    out=$(printf '%s'  "$line" | grep -oE 'outcome="[^"]+"'  | head -1 | sed 's/outcome="//;s/"$//')
    [ -n "$name" ] && printf '%s\t%s\n' "$out" "$name" >> "$ALL"
  done
done
TOTAL=$(wc -l < "$ALL" | tr -d ' '); PASSED=$(grep -c '^Passed' "$ALL"); FAILED=$(grep -cv '^Passed' "$ALL")

# ---- 3. Per-feature matrix -------------------------------------------------
# feature-label ‖ regex over test names ('‖' separates label from regex; '|' is regex-or)
FEATURES=(
  "Guard 1 · end-turn w/ energy       ‖EndTurn_"
  "Guard 2 · elite/boss potion        ‖Potion_"
  "Guard 3 · deck-check speed bump     ‖DeckCheck_"
  "HUD math · energy/radial/row layout ‖Radial_|Row_|CurrentOnly|WithMax|ZeroMax|NegativeCurrent"
  "Relic counters · per-relic registry ‖Registry_"
  "Harmony weave · 8 patches, no BadIL ‖AllPatches_Weave"
  "Real game code · relics/cards run   ‖EveryRelicInstantiates|CardModelsInstantiate|GameHasManyRelics"
  "Game API contract · members intact  ‖ApiContract"
)
rule; say "③ Per-feature results"
overall_ok=1
for entry in "${FEATURES[@]}"; do
  label="${entry%%‖*}"; rx="${entry#*‖}"
  n=$(grep -Ec "($rx)" "$ALL")
  f=$(grep -E "($rx)" "$ALL" | grep -cv '^Passed')
  if [ "$n" -eq 0 ]; then status="  --  (no tests matched)"; overall_ok=0;
  elif [ "$f" -eq 0 ]; then status="✅ PASS ($n)";
  else status="❌ FAIL ($f/$n)"; overall_ok=0; fi
  printf '  %s %s\n' "$label" "$status"
done

rule
if [ "$FAILED" -eq 0 ] && [ "$C" -eq 0 ] && [ "$R" -eq 0 ] && [ "$W" -eq 0 ] && [ "$overall_ok" -eq 1 ]; then
  [ "${INSTALLED:-0}" -eq 1 ] && inst="built + installed" || inst="built (install skipped)"
  say "ALL GREEN — $PASSED/$TOTAL tests passed. Mod $inst."
  exit 0
else
  say "FAILURES — $PASSED/$TOTAL passed, $FAILED failed."
  grep -v '^Passed' "$ALL" | sed 's/^/  ✗ /' | head -20
  exit 1
fi
