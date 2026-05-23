# MSIX Packaging

Laplace includes a manual MSIX build pipeline for the current CLI executable.

Files:

- `installer/build-msix.ps1`
- `installer/msix/AppxManifest.template.xml`

## Prerequisites

- Windows
- .NET SDK 8+
- Windows SDK (for `makeappx.exe` and `signtool.exe`)

## Build MSIX

```powershell
powershell -ExecutionPolicy Bypass -File .\installer\build-msix.ps1 `
  -Configuration Release `
  -Runtime win-x64 `
  -Version 0.1.0.0 `
  -PackageName Laplace.Project `
  -Publisher "CN=LaplaceProject"
```

Output:

- `artifacts\msix\Laplace_<version>_<runtime>.msix`
- `artifacts\msix\cert\LaplaceProjectDev.pfx` (if auto-generated)

## Install Package

```powershell
Add-AppxPackage .\artifacts\msix\Laplace_0.1.0.0_win-x64.msix
```

## Certificate Notes

By default, the script creates a self-signed development certificate and uses it to sign the package.

For local sideloading on the same machine, use:

```powershell
powershell -ExecutionPolicy Bypass -File .\installer\build-msix.ps1 -InstallCertificate
```

For broader distribution, sign with a trusted certificate:

```powershell
powershell -ExecutionPolicy Bypass -File .\installer\build-msix.ps1 `
  -PfxPath "C:\certs\your-prod-cert.pfx" `
  -PfxPassword "<password>" `
  -Publisher "CN=YourCompany"
```

`Publisher` must match the certificate subject exactly.
