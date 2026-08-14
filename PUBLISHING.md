# Publishing PokaYokeSpire to the Steam Workshop

The game itself has **no in-game publish button** — its modding screen only *reads*
the Workshop (`ModManager.InitializeSteamMods` → `SteamUGC.GetSubscribedItems`).
Publishing is done with MegaCrit's external CLI, **`sts2-mod-uploader`**. This repo
is already set up to produce the payload it needs.

## What the game loads (the contract)

`ModManager` scans mod folders for a **`.pck`** file, then loads the sibling
`<name>.dll`, then reads **`res://mod_manifest.json` from inside the pck**. So a valid
Workshop payload is exactly two files:

```
content/PokaYokeSpire.pck   # carries mod_manifest.json (pck_name must == "PokaYokeSpire")
content/PokaYokeSpire.dll
```

Both are produced by `package-workshop.sh` from `_pack/` (already configured:
`_pack/mod_manifest.json` has the correct `pck_name` schema).

> The top-level `PokaYokeSpire/mod_manifest.json` uses an older `id`/`has_dll`/
> `dependencies` schema. **The 0.107.1 game ignores it** — only the pck-internal
> `_pack/mod_manifest.json` is read at load. It's kept as project metadata; don't rely
> on it for loading.

## One-time setup

1. **Build the uploader** (cross-platform; ships a macOS `libsteam_api.dylib`):
   ```sh
   git clone https://github.com/megacrit/sts2-mod-uploader
   cd sts2-mod-uploader
   dotnet publish -c Release -r osx-arm64
   ```
2. Run it once to scaffold a `NewModWorkspace/`. **We don't need its workspace** — this
   repo already provides one at `modding/workshop/` (with `workshop.json`). You only need
   the uploader *binary*.

## Each release

1. **Quit the game.** `package-workshop.sh` rebuilds the DLL, which crashes an
   in-progress run.
2. Assemble the payload:
   ```sh
   ./package-workshop.sh
   ```
   → writes `workshop/content/{PokaYokeSpire.pck, PokaYokeSpire.dll}` and verifies the
   pck loads its manifest.
3. **Create `workshop/image.png`** — the Workshop thumbnail, **< 1 MB** (square, e.g.
   512×512). One-time; reused across updates. *(Still missing — you must add this.)*
4. Review `workshop/workshop.json` — title, description, `visibility` (`private` for the
   first upload, flip to `public` when ready), and `dependencies` (BaseLib's Workshop ID
   **`3737335127`** is already listed so subscribers auto-get it). Update `changeNote`.
5. Upload:
   ```sh
   /path/to/ModUploader upload -w "$PWD/workshop"
   ```
   - Steam must be **running and logged in**.
   - The first upload writes `workshop/mod_id.txt`; every later run reuses that ID and
     **updates the same item** — so re-running step 5 is how you push updates.
6. Verify on the item's Workshop page, then set `visibility` to `public`.

## Checklist of what's in place vs still needed

| Item | State |
|------|-------|
| `_pack/` pck builder + correct `mod_manifest.json` | ✅ in repo |
| `PokaYokeSpire.csproj` (`AssemblyName=PokaYokeSpire`) | ✅ |
| `workshop/workshop.json` (deps → BaseLib `3737335127`) | ✅ created |
| `package-workshop.sh` (DLL + pck → `content/`) | ✅ created |
| `workshop/image.png` thumbnail (< 1 MB) | ❌ **you must create** |
| `sts2-mod-uploader` binary | ❌ clone + build once |

Source of truth for the load contract:
`rlkit/sts2-rl-agent/decompiled/MegaCrit.Sts2.Core.Modding/{ModManager,ModManifest}.cs`.
