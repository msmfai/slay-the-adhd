#!/usr/bin/env bash
# Run the API-contract tests against the installed game's sts2.dll.
# Uses the dotnet SDK from the nix store (re-fetches if garbage-collected).
set -euo pipefail
DOTNET_PATH="$(nix build --no-link --print-out-paths 'nixpkgs#dotnet-sdk_9' 2>/dev/null)"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 DOTNET_CLI_HOME=/tmp/dotnet_home
cd "$(dirname "$0")"
exec "$DOTNET_PATH/bin/dotnet" test -c Release "$@"
