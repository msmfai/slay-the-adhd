#!/usr/bin/env bash
# Run the whole PokaYokeSpire test suite:
#   1. Contract tests  — metadata-only reflection over sts2.dll (game API surface the
#      Harmony patches depend on). Fails loud if a patched method/type/field is renamed.
#   2. Runtime tests   — load & EXECUTE real game code headless (every relic + card
#      constructor), plus pure unit + metamorphic tests of the mod's own logic.
set -euo pipefail
here="$(dirname "$0")"
echo "== Contract tests (game API surface) =="
"$here/PokaYokeSpire.Tests/run-tests.sh" "$@"
echo
echo "== Runtime tests (executes game code + unit/metamorphic) =="
"$here/PokaYokeSpire.Tests.Runtime/run-tests.sh" "$@"
echo
echo "All suites passed."
