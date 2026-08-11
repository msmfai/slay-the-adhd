#!/usr/bin/env bash
# Run the runtime tests — these load the game's real sts2.dll and EXECUTE its code
# (relic/card constructors, model reflection) headless, plus pure-logic unit and
# metamorphic tests. Uses the dotnet SDK from the nix store.
set -euo pipefail
DOTNET_PATH="$(nix build --no-link --print-out-paths 'nixpkgs#dotnet-sdk_9' 2>/dev/null)"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 DOTNET_CLI_HOME=/tmp/dotnet_home
cd "$(dirname "$0")"
exec "$DOTNET_PATH/bin/dotnet" test -c Release "$@"
