# Changelog

All notable changes to Laplace will be documented here.

## Unreleased

## 2.1.1 - 2026-06-20

- Redesigned Windows Explorer right-click context menu integration to route all actions directly to the WPF GUI, eliminating flashing CLI windows.
- Added native WPF GUI startup options for testing, info details, repair operations, extraction (here and named folders), and beside compression.

## 2.1.0 - 2026-06-14

- Migrated the desktop user interface from WinForms to a modern WPF design.
- Added support for the CMIX compression method, size-gated for LPC archives.

## 1.10.0 - 2026-06-07

- Tuned LPC `extreme` mode with tiered Zstd long-distance matching, improving large distant-repeat compression while reducing peak memory.
- Added a reproducible Ultra Ratio benchmark harness with ratio, speed, selected-method, and peak-working-set reporting.
- Replaced the logo backdrop with true transparency across PNG, ICO, executable, installer, README, and MSIX assets.
- Replaced placeholder MSIX tile images with branded Laplace logo assets.
- Embedded the release version into published CLI and desktop binaries.
- Rewrote the README around installation, desktop workflows, CLI usage, supported formats, security, compression, and release verification.

## 1.9.1 - 2026-06-07

- Added a centered operation overlay with progress details and cancellation for desktop archive operations.
- Rebuilt the archive password dialog with archive context, show-password support, Caps Lock feedback, input validation, and incorrect-password retry.

## 1.9.0 - 2026-06-06

- Added true multi-volume `.7z` and `.rar` creation through `--volume-size` and desktop creation presets.
- Added multi-volume output discovery, aggregate size reporting, and first-volume verification in the CLI.
- Added LPC-only `extreme` compression with automatic memory tiers, large LZMA dictionaries, ratio-first selection, and single-worker memory bounds.
- Added the Explorer `Ultra Ratio` action for verified extreme-mode LPC creation.

## 1.8.0 - 2026-06-06

- Added LPCv5 versioned key derivation with Argon2id defaults and backward-compatible PBKDF2 reads.
- Added LPCv6 AES-256-GCM encryption for file and block metadata tables through `--hide-names`.
- Added ordered multi-threaded solid-block compression using the configured thread count.
- Added LPCv7 striped Reed-Solomon recovery records, integrity validation, and native `laplace repair` support.
- Added desktop controls and archive information fields for metadata encryption and recovery records.

## 1.7.0

- Added `compressed`/`ultra` mode for strongest ratio-focused archive creation.
- Added external 7-Zip ultra-solid `.7z` creation for compressed/max modes when `7z.exe` is installed.
- Added WinRAR/RAR discovery through Windows uninstall registry locations, covering non-default installs such as `D:\WinRAR`.
- Added RAR5 solid best-compression `.rar` creation for `--mode compressed --solid on` using installed WinRAR/RAR tools.
- Added `.iso` shell integration and desktop workflow to extract ISO contents to a selected removable drive without formatting or raw-writing the drive.
- Added `extract --no-verify` and `--quiet` options for faster benchmark and extraction workflows.
- Added WinRAR and 7-Zip comparison scripts under `scripts/`.
- Updated README and shell integration documentation for compressed mode, ISO extraction, and expanded archive support.
- Added an `intensive` compression mode for ratio-focused LPC candidate testing and surfaced it in CLI, desktop create settings, and documentation.
- Added single-input auto `.lpc` naming for CLI compression and collision-safe desktop create defaults.
- Added password confirmation for interactive encrypted archive creation.
- Added CLI black-box regression coverage for help output, unknown commands, LPC round trips, encrypted password-file workflows, and invalid argument handling.

## 1.6.3

- Hardened archive extraction path validation against absolute paths, traversal variants, Windows reserved names, alternate data streams, and unsafe path segments.
- Blocked extraction through existing destination reparse points such as symlinks and junctions.
- Strengthened new encrypted LPC archives with 600,000 PBKDF2 iterations, 32-byte salts, and bounded KDF metadata validation.
- Added regression coverage for extraction path safety and LPC encryption metadata.

## 1.6.2

- Fixed release verification installer smoke tests so they restore the previous Inno installer registration after running.
- Updated the installer to use the default per-user Laplace install directory instead of reusing a previous app directory during silent installs.

## 1.6.1

- Fixed release verification shell-integration smoke tests so they restore any existing Explorer right-click registration after running.
- Refreshed Explorer shell associations after installing or uninstalling Laplace integration so right-click menu changes appear immediately.

## 1.6.0

- Added selected-entry extraction for the desktop UI and archive extraction services, covering native LPC, ZIP, and managed external archive paths such as `.7z`.

## 1.5.0

- Fixed Windows native extraction fallback for external archives that SharpCompress opens but later fails to enumerate, including larger `.tar.zst` archives.
- Added a desktop status-bar Cancel action for long-running create, extract, test, and estimate operations.

## 1.4.1

- Added Windows native archive extraction fallback through the inbox `tar.exe`/libarchive path when managed external extraction cannot read an archive.

## 1.4.0

- Added `.7z` archive creation, fixed managed `.7z` read/extract/test paths, and added optional WinRAR/RAR CLI-backed `.rar` creation.
- Added compression size estimation for CLI, desktop UI, and Explorer create-context actions.
- Added sampled trial-compression estimates with confidence, likely methods, and approximate LPC metadata overhead.

## 1.3.0

- Improved adaptive compression analysis with pattern reuse, text ratio, and zero-byte signals.
- Reworked block selection so mixed files can store incompressible blocks raw while compressing later compressible blocks.
- Stopped forcing broad media/document categories to raw without testing the actual bytes, improving results for compressible formats such as simple `.bmp` and `.wav` files.
- Added regression coverage for compressible media-like files and mixed random/repeated block files.

## 1.2.0

- Reworked Explorer integration into a branded `Laplace` cascade submenu for archive and create actions.
- Added archive context actions for supported formats without taking over the default app for non-`.lpc` archives.
- Added `compress-beside` so quick-create actions name archives from the selected item, for example `report.pdf` -> `report.lpc`, while keeping original file names inside the archive.
- Added `extract-dialog` and GUI `--extract` startup support for context-menu extraction with options.

## 1.1.0

- Added a Windows desktop archive-manager UI with RAR-style menus, toolbar actions, archive path bar, file listing, and status bar.
- Added create, extract, inspect/list, test, and archive information workflows to the desktop UI.
- Updated CLI open/dialog helper commands and Explorer shell integration to launch the desktop UI when available.
- Updated installer packaging so Start Menu and desktop shortcuts open the GUI while the CLI remains available for automation.

## 1.0.0

- Added common archive read/extract/list/info/test support through a format-routing layer.
- Added `.zip` creation, including AES-256 encrypted ZIP output.
- Added LPCv2 payload encryption with AES-256-GCM and PBKDF2-HMAC-SHA256.
- Added password input support with `--password`, `--password-file`, Windows popup UI, and console fallback.
- Added normalized `list`, `info`, and `test` output for non-LPC archive formats.
- Added a project logo and embedded it into `laplace.exe` and the Windows installer.
- Updated release packaging to produce self-contained installer and MSIX artifacts.
- Expanded README documentation for features, formats, security, installation, and release process.

## 0.1.1

- Added GitHub Actions CI for restore/build/test on `main` and pull requests.
- Added tagged release automation for Windows installer and MSIX artifacts.
- Added SHA-256 checksum generation for release assets.
- Added `global.json` to pin the .NET SDK feature band.

## 0.1.0

- Initial public baseline for the `.lpc` archive format, CLI, compression pipeline, integrity checks, shell integration, and Windows packaging scripts.
