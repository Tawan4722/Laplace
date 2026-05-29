# Changelog

All notable changes to Laplace will be documented here.

## Unreleased

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
