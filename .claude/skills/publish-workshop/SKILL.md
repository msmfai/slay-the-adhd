---
name: publish-workshop
description: Publish (or update) the "Slay The Math" mod to the Steam Workshop headlessly, using the user's already-running Steam session. Invoke when the user asks to publish/upload/push the mod to the Workshop, or to ship a new version. Encodes the safe build → assemble → confirm → upload sequence.
---

# Publish Slay The Math to the Steam Workshop

Internal assembly name is `PokaYokeSpire`; the **display/Workshop name is "Slay The Math"**.
Authoritative reference: `PUBLISHING.md` at the repo root. This skill is the operational runbook.

## ⚠️ Hard rules
- **Publishing is an irreversible outward action** that creates a live Workshop item under the
  user's account. NEVER run the upload step without an explicit, in-the-moment "yes, upload".
- **Building crashes a live game run.** Before building, check the game is not running
  (`pgrep -fl "SlayTheSpire2|Slay the Spire 2"`). If it is, STOP and ask the user to quit it.
- First upload keeps `visibility: "private"` (already set in `workshop/workshop.json`). Only the
  user flips it to public, on the item's Steam page.
- The uploader authenticates via the **already-logged-in local Steam client** — never ask for,
  handle, or store Steam credentials.

## Preconditions to verify (report any that fail, don't work around them)
1. Steam is running:  `pgrep -x steam_osx`.
2. Game is NOT running (build safety): `pgrep -fl "SlayTheSpire2|Slay the Spire 2"`.
3. Tooling: `git`, network to github.com, and Godot at
   `_tools/Godot.app/Contents/MacOS/Godot`; a .NET 9 SDK (`headless-loop.sh` resolves it via
   `nix build nixpkgs#dotnet-sdk_9` — confirm `dotnet` runs).
4. **Preview image** `workshop/image.png` exists and is < 1 MB.
   - ⛔️ **PENDING TODO — the user will supply this screenshot when the mod is done.** See
     `workshop/PREVIEW-TODO.md`. Until then, either wait, or (only if the user says to proceed
     now) generate a temporary placeholder PNG to unblock a private test upload, and remind them
     to swap in the real screenshot before going public.

## Steps

### Phase A — prep (safe; stops before any upload)
1. Verify preconditions above.
2. Build the uploader once (skip if `sts2-mod-uploader` already built):
   ```sh
   git clone https://github.com/megacrit/sts2-mod-uploader   # into a scratch/tools dir
   cd sts2-mod-uploader && dotnet publish -c Release -r osx-arm64
   ```
   The resulting `ModUploader` binary is what runs the upload.
3. Assemble the payload (this BUILDS the mod — recheck the game isn't running first):
   ```sh
   ./package-workshop.sh
   ```
   → produces `workshop/content/{PokaYokeSpire.pck, PokaYokeSpire.dll}` and verifies the pck
   loads its manifest. Confirm the pck's manifest `name` reads **"Slay The Math"**.
4. Review `workshop/workshop.json` — title, description (rewrite if stale), `visibility`
   (`private` for first upload), `changeNote`, and `dependencies` (BaseLib `3737335127`).
5. **STOP. Summarize what will be uploaded and ask the user for explicit go-ahead.**

### Phase B — upload (only after explicit confirmation)
```sh
/path/to/ModUploader upload -w "$PWD/workshop"
```
- Uses the user's live Steam session.
- First run writes `workshop/mod_id.txt` (the item ID). Every later run reuses it and UPDATES
  the same item — that's the normal release path.
- If it fails on the **Workshop Legal Agreement**, the item was created but is hidden until the
  user clicks "I agree" on the item's page (browser/overlay) once. Surface this; it can't be
  automated.
- If the shell can't reach the Steam IPC socket (sandbox), it fails fast and safe — report it.

### After a successful upload
- Report the item ID / URL from the uploader output.
- Remind: swap in the real preview screenshot if a placeholder was used, then set visibility to
  public on the item's Steam Workshop page when ready.

## Updating an existing release
Same as Phase A then B; `mod_id.txt` makes Phase B update-in-place. Bump `version` in
`_pack/mod_manifest.json` and set a `changeNote` in `workshop.json` first.
