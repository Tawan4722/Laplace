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
- `.lpc` context verbs:
  - Open with Laplace
  - Extract Here
  - Extract to "<name>\"
  - Test archive
  - Archive info
- File/folder context verbs:
  - Add to "<name>.lpc"
  - Add to .lpc archive...

## Notes

- `compress-dialog` is a placeholder until GUI archive-creation dialog is added.
- Installer-integrated registration/unregistration is planned for later phases.
