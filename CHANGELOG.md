# Changelog — Pixygon Build Tools

All notable changes to `com.pixygon.buildtools`. Format loosely follows
[Keep a Changelog](https://keepachangelog.com/); versions track `package.json`.

## [0.1.0] — Unreleased

### Added
- **Build core** (`Editor/BuildTools.cs`) — one-button, multi-platform build entry
  points under the `Pixygon/Build/` menu:
  - **WebGL (Release)** — Brotli + content-hashed filenames + decompression-fallback
    (own-site/CDN hosting profile); mirrors to `Builds/WebGL/latest/`.
  - **Windows / macOS / Linux (Mono)** — standalone players into `Builds/<target>/<version>/`.
  - **All Standalone + WebGL** — builds every target in one blocking run.
- `GameName` auto-derives from `PlayerSettings.productName` (non-alphanumerics
  stripped), so output paths/filenames need no per-project config.
- Scenes always read from the Build Profiles list (`EditorBuildSettings.scenes`,
  enabled only) — single source of truth.
- `EnsureActiveTarget` guard so Addressables-on-player-build content builds for the
  correct active platform (switch-and-abort on async single-target runs to avoid a
  domain reload killing an in-flight build).
- Package scaffold: `package.json`, Editor asmdef (`Pixygon.BuildTools.Editor`), README
  (design of record for the full pipeline), git repo.

- **Ship layer** (migrated from Pixiel Dreadwager): `BuildAndShip` (Build & Ship menu
  + cross-reload queue), `BunnyUploader` (BunnyCDN upload + cache purge),
  `BunnySettingsWindow` (per-machine credentials), `VersionTools` (patch bump),
  `WebGLBuildManifest` (`build-manifest.json`), `WebGLRollbackWindow`, `BuildHandoff`
  (close-build-reopen auto-handoff), `BuildCLI` (batchmode entry). All generic; Bunny
  config via env / `~/.config/pixygon/bunny.json` / EditorPrefs.

### Not yet migrated (roadmap — see README)
- Shell-script CLI wrappers (`ship.sh` / `notarize-mac.sh`): still hardcode the game
  name/path; to be generalized + shipped in the package with a one-command install.
- The project-specific changelog export (reads `VersionData` SOs) intentionally stays
  in each game.
