# Changelog

All notable changes to Laplace will be documented here.

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
