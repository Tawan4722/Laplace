# Shell Integration (Current Implementation)

Laplace currently provides a **per-user** shell integration flow via CLI:

```powershell
laplace integrate install
laplace integrate status
laplace integrate uninstall
```

Optional explicit path:

```powershell
laplace integrate install --cli-path "C:\Program Files\Laplace\laplace.exe"
```

## Scope

- Registry scope: `HKCU\Software\Classes` (current user)
- No admin rights required
- Safe removal via `integrate uninstall`

## Registered Items

- `.lpc` file association to `Laplace.Archive`
- A branded `Laplace` cascade menu for archive files:
  - Open in Laplace
  - Extract with options...
  - Extract here
  - Extract to archive-named folder
  - Test integrity
  - Show archive details
- Archive menus are registered for `.lpc`, `.zip`, `.7z`, `.rar`, `.tar`, `.gz`, `.tgz`, `.bz2`, `.xz`, `.zst`, and `.lzip` without taking over the default app for non-`.lpc` formats.
- A branded `Laplace` cascade menu for normal files, folders, and folder background:
  - Create archive...
  - Create .lpc beside item
  - Create verified .lpc
- Quick create commands use the selected item name and add the `.lpc` file extension. For example, `report.pdf` becomes `report.lpc`, while the archived entry remains `report.pdf`.

## Notes

- The registry integration is intentionally lightweight and per-user. It supports Explorer single-item context actions; true 7-Zip-style multi-selection batching would require a native COM shell extension.
- Installer-integrated registration/unregistration is handled by the Inno Setup installer when the shell integration task is selected.
