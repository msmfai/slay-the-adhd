#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# package-workshop.sh — assemble the Steam Workshop upload payload for
# PokaYokeSpire, WITHOUT publishing. Publishing is a separate, explicit step
# (see PUBLISHING.md) because it needs Steam running + logged in and is an
# irreversible outward action.
#
# ⚠️  This BUILDS the mod (Release DLL + .pck). Building overwrites the DLL the
#     running game has loaded and WILL crash an in-progress run. Do NOT run this
#     while you are playing — quit the game first.
#
# Output: modding/workshop/content/{PokaYokeSpire.dll, PokaYokeSpire.pck}
#         ready to hand to the sts2-mod-uploader as its `content/` folder.
# ---------------------------------------------------------------------------
set -euo pipefail

MOD_ROOT="$(cd "$(dirname "$0")" && pwd)"
GODOT="$MOD_ROOT/_tools/Godot.app/Contents/MacOS/Godot"
DOTNET_PATH="$(nix build --no-link --print-out-paths 'nixpkgs#dotnet-sdk_9' 2>/dev/null || true)"
DOTNET="${DOTNET_PATH:+$DOTNET_PATH/bin/}dotnet"

CSPROJ="$MOD_ROOT/PokaYokeSpire/PokaYokeSpire.csproj"
PACK_DIR="$MOD_ROOT/PokaYokeSpire/_pack"
OUT="$MOD_ROOT/workshop/content"

say() { printf '\033[1;36m%s\033[0m\n' "$*"; }

say "① Build Release DLL"
"$DOTNET" build -c Release "$CSPROJ"
BUILT_DLL="$MOD_ROOT/PokaYokeSpire/.godot/mono/temp/bin/Release/PokaYokeSpire.dll"
[ -f "$BUILT_DLL" ] || BUILT_DLL="$MOD_ROOT/PokaYokeSpire/bin/Release/PokaYokeSpire.dll"
[ -f "$BUILT_DLL" ] || { echo "DLL not found after build"; exit 1; }

say "② Build .pck (embeds mod_manifest.json)"
export PCK_OUT="$PACK_DIR/PokaYokeSpire.pck"
"$GODOT" --headless --path "$PACK_DIR" --script "$PACK_DIR/pack.gd"
say "   verify the pck loads its manifest"
"$GODOT" --headless --path "$PACK_DIR" --script "$PACK_DIR/verify.gd"

say "③ Assemble workshop/content/"
mkdir -p "$OUT"
cp "$BUILT_DLL" "$OUT/PokaYokeSpire.dll"
cp "$PCK_OUT"   "$OUT/PokaYokeSpire.pck"

say "   content/ now contains:"
ls -la "$OUT"

# Preflight the metadata the uploader needs (warn, don't fail).
[ -f "$MOD_ROOT/workshop/workshop.json" ] || echo "⚠️  workshop/workshop.json missing — see PUBLISHING.md"
[ -f "$MOD_ROOT/workshop/image.png" ]     || echo "⚠️  workshop/image.png (Workshop thumbnail, <1 MB) missing — create one before uploading"

say "Done. Payload ready at workshop/content/. Next: PUBLISHING.md → run the uploader."
