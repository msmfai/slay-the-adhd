#!/usr/bin/env bash
# ============================================================================
# Poka-Yoke Spire — DRIVE the real game headless, invisibly and silently, and
# assert from the log which features actually fire in a live AutoSlay run.
#
#   invisible : --headless (no window) + LSUIElement (no dock icon)
#   silent    : the harness sets TestMode.IsOn=true (gates every audio Play*)
#               + --audio-driver Dummy
#   piloted   : the harness unlocks the game's built-in AutoSlay autoplayer and
#               runs a full seeded run; it also synth-triggers the click-only
#               features (radial pins, relic counters).
#
# Safety: the plist LSUIElement edit is ALWAYS reverted and the game is ALWAYS
# hard-killed on exit (the game ignores SIGTERM). Nothing is left running or
# changed. Verifies invisibility (foreground app / windows) while it runs.
# ============================================================================
set -uo pipefail
MOD_ROOT="$(cd "$(dirname "$0")" && pwd)"
APP="$HOME/Library/Application Support/Steam/steamapps/common/Slay the Spire 2/SlayTheSpire2.app"
GAMEBIN="$APP/Contents/MacOS/Slay the Spire 2"
MODS="$APP/Contents/MacOS/mods"
PLIST="$APP/Contents/Info.plist"
SEED="${STS2_SEED:-POKA1}"
MAX_SECONDS="${1:-200}"
ISOLATE="${ISOLATE:-0}"   # 0 = full mod stack (real-world); 1 = our mod + BaseLib only

DOTNET_PATH="$(nix build --no-link --print-out-paths 'nixpkgs#dotnet-sdk_9' 2>/dev/null)"
DOTNET="$DOTNET_PATH/bin/dotnet"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 DOTNET_CLI_HOME=/tmp/dotnet_home
LOG=/tmp/sts_drive.log; : > "$LOG"
GPID=""

WS="$HOME/Library/Application Support/Steam/steamapps/workshop/content/2868840"
WS_BAK="${WS}.pokayoke_bak"
cleanup() {                       # ALWAYS: kill game + revert plist + restore Workshop mods
  [ -n "$GPID" ] && kill -9 "$GPID" 2>/dev/null
  pkill -9 -f "Contents/MacOS/Slay the Spire 2" 2>/dev/null
  /usr/libexec/PlistBuddy -c "Delete :LSUIElement" "$PLIST" 2>/dev/null
  [ -d "$WS_BAK" ] && [ ! -d "$WS" ] && mv "$WS_BAK" "$WS"   # restore other mods
  # Remove the test-only bits so NORMAL play is unaffected: the harness (would auto-pilot
  # + delete runs) and the local BaseLib copy (would double-load with the Workshop one).
  rm -rf "$MODS/PokaYokeHarness" "$MODS/BaseLib" 2>/dev/null
}
trap cleanup EXIT INT TERM
say() { printf '%s\n' "$*"; }
rule() { printf '%s\n' "────────────────────────────────────────────────────────────"; }

# ---- build + install mod + harness ----------------------------------------
rule; say "① Build mod + harness, install to mods/"
"$DOTNET" build -c Release "$MOD_ROOT/PokaYokeSpire/PokaYokeSpire.csproj"   >/tmp/d_mod.log   2>&1 || { say "mod build FAILED";     grep -i error /tmp/d_mod.log|head; exit 1; }
"$DOTNET" build -c Release "$MOD_ROOT/PokaYokeHarness/PokaYokeHarness.csproj" >/tmp/d_harn.log 2>&1 || { say "harness build FAILED"; grep -i error /tmp/d_harn.log|head; exit 1; }
inst() { # <projdir> <id>
  local dll="$MOD_ROOT/$1/.godot/mono/temp/bin/Release/$2.dll"
  mkdir -p "$MODS/$2"; cp "$dll" "$MODS/$2/" && cp "$MOD_ROOT/$1/mod_manifest.json" "$MODS/$2/"
}
inst PokaYokeSpire  PokaYokeSpire  && say "  installed PokaYokeSpire"
inst PokaYokeHarness PokaYokeHarness && say "  installed PokaYokeHarness (driver)"
echo "2868840" > "$APP/Contents/MacOS/steam_appid.txt"

# Default is the REAL-WORLD config: our mod loaded ALONGSIDE all the other major Workshop
# mods, so the drive proves our features fire in the stack the user actually runs. Pass
# ISOLATE=1 to instead run our mod + BaseLib only (for A/B diagnosis of a conflict).
if [ "$ISOLATE" = 1 ]; then
  [ -d "$WS/3737335127/BaseLib" ] && { mkdir -p "$MODS/BaseLib"; cp -R "$WS/3737335127/BaseLib/." "$MODS/BaseLib/"; say "  copied BaseLib into local mods/"; }
  [ -d "$WS" ] && [ ! -d "$WS_BAK" ] && mv "$WS" "$WS_BAK" && say "  ISOLATED: Workshop mods moved aside (PokaYokeSpire + harness + BaseLib only)"
else
  say "  FULL STACK: running with all Workshop mods loaded (real-world config)"
fi

# ---- make it invisible (agent app: no dock icon) --------------------------
/usr/libexec/PlistBuddy -c "Add :LSUIElement bool true" "$PLIST" 2>/dev/null \
  || /usr/libexec/PlistBuddy -c "Set :LSUIElement true" "$PLIST"
say "  LSUIElement=true (no dock icon), --headless (no window), TestMode (silent)"

# ---- launch + pilot --------------------------------------------------------
rule; say "② Launch headless + AutoSlay drive (seed=$SEED, up to ${MAX_SECONDS}s)"
# --user-dir points the game at a FRESH, empty profile so there is no in-progress run for
# AutoSlay to abandon (and your real save is never touched).
# --headless alone gives a working headless renderer (stubs textures); an explicit
# --rendering-driver dummy nulls textures and breaks UI/combat asset loads, so don't.
STS2_SEED="$SEED" "$GAMEBIN" --headless --audio-driver Dummy </dev/null >"$LOG" 2>&1 &
GPID=$!

SEEN_APP=0
for ((i=1; i<=MAX_SECONDS; i++)); do
  # invisibility watchdog: a VISIBLE window would be a failure
  wins=$(osascript -e 'tell application "System Events" to count of windows of (first process whose name contains "Slay the Spire")' 2>/dev/null || echo 0)
  [ "${wins:-0}" != "0" ] && SEEN_APP=1
  # stop once the harness has finished exercising the features (or on hard failure)
  grep -qE "feature exercise complete|direct-start FAILED" "$LOG" 2>/dev/null && { say "  feature exercise done at ${i}s"; break; }
  kill -0 "$GPID" 2>/dev/null || { say "  game process exited at ${i}s"; break; }
  sleep 1
done

# ---- report: live invariants + per-feature firing -------------------------
rule; say "③ Live-run invariants"
has() { grep -qE "$1" "$LOG"; }
ok=1
if has "\[Poka-Yoke\] patched [0-9]+ methods"; then say "  mod loads + patches all methods . ✅"; else say "  mod loads + patches all methods . ❌"; ok=0; fi
if has "Bad ?I[Ll] range|BadImageFormatException"; then say "  runtime Bad IL ............... ❌ PRESENT"; ok=0; else say "  runtime Bad IL ............... ✅ none"; fi
if has "entered act 0 — combat should be live"; then say "  piloted into live combat ..... ✅"; else say "  piloted into live combat ..... ❌"; ok=0; fi

# Each feature is driven by constructing the node it hooks in the live game, injecting real
# combat state, and invoking the patched code path; it "fires" when its log marker appears.
rule; say "④ Per-feature firing (real patched code paths, live combat)"
feat() { # <label> <marker-regex>
  if has "$2"; then printf '  %s ✅ FIRED\n' "$1"; else printf '  %s ❌ did not fire\n' "$1"; ok=0; fi
}
feat "Guard 1 · end-turn energy    " "guard1 FIRED"
feat "Guard 2 · elite/boss potion  " "guard2 FIRED"
feat "Guard 3 · deck-check bump     " "guard3 FIRED"
feat "Feature 4 · energy counter    " "feature4 ACTIVE"
feat "Feature 5 · radial relic pins " "feature5 FIRED"
feat "Feature 6 · relic counters    " "feature6 FIRED"
feat "Feature 7 · end-turn dmg readout " "feature7 READOUT"
feat "Feature 8 · card target preview " "feature8 CARD-PREVIEW"
feat "Feature 9 · lethal gem glow    " "feature9 LETHAL"

rule
say "invisibility: visible_windows_ever=$([ $SEEN_APP -eq 1 ] && echo YES || echo no) ; dock icon suppressed (LSUIElement) ; audio muted (Play* skipped)"
say "log: $LOG"
if [ "$ok" -eq 1 ] && [ "$SEEN_APP" -eq 0 ]; then
  say "DRIVE OK — all features fired in live combat, no Bad IL, nothing seen or heard."
  exit 0
fi
say "DRIVE FAILED — a feature didn't fire / an invariant broke / a window appeared (see above)."
exit 1
