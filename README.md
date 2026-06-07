# Pixygon - Build Tools

One-click, multi-platform **build + ship** for Pixygon Unity games. Extracted from
Pixiel Dreadwager so every Pixygon project gets the same pipeline.

> **Status: build core implemented.** The multi-platform **build** entry points
> (`Editor/BuildTools.cs`) are in and fully generic — `GameName` auto-derives from
> `PlayerSettings.productName`, so it works in any project with zero config. Menu:
> `Pixygon/Build/` → WebGL / Windows / macOS / Linux / All.
>
> The **ship** layer (BunnyCDN upload + cache purge, patch-version bump, WebGL
> `build-manifest.json`, macOS notarization, the cross-reload "Build & Ship ALL"
> queue, and the batchmode `ship.sh` CLI) still migrates here from PixielDreadwager
> next. The sections below are the design of record for that layer.

## What it does

- **Build & ship every platform from the editor** — `Pixygon → Build & Ship → Build & Ship ALL Platforms`. A cross-domain-reload queue (`BuildQueue`) builds each target after its platform switch settles, so Addressables-with-player projects build correctly without closing Unity.
- **Single-platform ships** — `Build & Ship WebGL / Windows / macOS` (async, abort-and-rerun on target switch).
- **Batchmode CLI** — `./ship.sh all [--notarize]` for unattended/CI runs (editor closed).
- **BunnyCDN upload** with cache purge, concurrent retrying uploads, manifest-uploaded-last ordering.
- **Automatic patch-version bump** (ship-only).
- **Self-describing WebGL `build-manifest.json`** so the website reads exact hashed filenames.
- **macOS code-signing + notarization + stapling** via `notarize-mac.sh`.
- **Local rollback** window (re-promote any archived WebGL build).

## Near-zero config

- `GameName` auto-derives from `PlayerSettings.productName` (non-alphanumerics
  stripped): `"Pixiel: Dreadwager"` → `PixielDreadwager`, `"Veilwalkers"` →
  `Veilwalkers`. Optional explicit override planned.
- Bunny storage zone / pull host default to the shared `pixygontech` bucket;
  per-game paths derive from `GameName` (`WebGL/<GameName>_WebGL/`,
  `Builds/<GameName>_win.zip`). Overridable via env / `~/.config/pixygon/bunny.json`
  / EditorPrefs.
- **Credentials + macOS signing identity are per-machine** (EditorPrefs / env), so
  they're set once and work for every Pixygon project on that machine.
- Shell scripts auto-detect the Unity version from `ProjectSettings/ProjectVersion.txt`.

## Adopting in a new project

1. Add the git URL to `Packages/manifest.json`:
   `"com.pixygon.buildtools": "https://github.com/Pixygon/com.pixygon.buildtools.git"`
2. (Optional, for the CLI) `Pixygon → Build & Ship → Install CLI scripts` — copies
   `ship.sh` / `notarize-mac.sh` / `compile-check.sh` to the project root.
3. Credentials are already shared per-machine. Done.

## Roadmap

- **Linux** (`StandaloneLinux64`, tar.gz) — easy, same shape as Windows.
- **Android** (`.apk` direct / `.aab` store) — needs a per-machine keystore.
- **iOS** — distinct "store submission" path (Xcode archive → App Store Connect /
  TestFlight), **not** a CDN download. Tracked separately from the build→zip→CDN flow.
