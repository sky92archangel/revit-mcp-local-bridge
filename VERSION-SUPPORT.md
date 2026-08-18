# Revit version support

## What is shared across versions

The command protocol, JSON schema, MCP tool names, REST contract, and user workflow are version-independent. The Revit add-in DLL is not. Every Revit major version needs a DLL compiled against that version's `RevitAPI.dll` and `RevitAPIUI.dll`.

Each installed version also has an isolated command queue:

```text
%LOCALAPPDATA%\RevitCommandBridge\2020
%LOCALAPPDATA%\RevitCommandBridge\2021
...
```

This prevents an agent connected to one Revit version from sending a command to another version's open project.

MCP profiles are independent per version. REST keeps `2020 = 8765` for compatibility and assigns `2021 = 8766` through `2026 = 8771`; `REVIT_BRIDGE_PORT` can override the selected port.

## Current support matrix

| Revit version | Build route | Validation state | Notes |
| --- | --- | --- | --- |
| 2020 | Bundled adapter or automatic local API build | [V] compiled both ways on this machine | Setup detects the installed version automatically; installation/live-model regression remains a separate step. |
| 2021–2024 | Automatic local API build | [T] implemented, corresponding Revit APIs not present locally | Setup detects each installed version and compiles a year-matched DLL from its local `RevitAPI.dll` / `RevitAPIUI.dll`; each year still needs a real-machine load and modelling test. |
| 2025–2026 | Reserved version identifier | [T] not implemented | These releases require a .NET 8 add-in adapter; the current build script intentionally stops rather than emitting an invalid DLL. |

## Automatic setup behavior for 2020–2024

The single-file setup scans registry and standard installation locations for Revit 2020 through 2024. It lists only detected versions. For each selected version it follows this rule:

1. Use a bundled, year-matched adapter when one is present.
2. Otherwise compile a matching adapter locally against that installed Revit version's API assemblies.
3. Verify the selected directory's `RevitAPI.dll` major version matches the requested Revit year.
4. Install into version-isolated add-in and queue directories.

The user does not need Visual Studio. The local adapter compiler is the .NET Framework compiler included with Windows. No Autodesk binaries are copied into the setup package.

## Build a specific version

Close the target Revit instance before installation. Building itself does not modify Revit.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1 `
  -RevitVersion 2024 `
  -RevitInstallDirectory 'C:\Program Files\Autodesk\Revit 2024'
```

The result is a separate package:

```text
dist\RevitCommandBridge-2024
```

## Install a specific version

The installer detects Revit locations through registry and configured scan roots. Inspect detection first:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\install-revit.ps1 -ListDetected
```

Then install exactly one matching package:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\install-revit.ps1 `
  -RevitVersion 2024 `
  -PackageDirectory .\dist\RevitCommandBridge-2024
```

Preview the exact target paths without copying files or changing Revit configuration:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\install-revit.ps1 `
  -RevitVersion 2024 `
  -PackageDirectory .\dist\RevitCommandBridge-2024 `
  -WhatIf
```

The installer writes its DLL and client scripts to `%LOCALAPPDATA%\RevitCommandBridge\<year>` and its add-in manifest to `%APPDATA%\Autodesk\Revit\Addins\<year>`.

## Evidence

- **E1 [V]** `build.ps1` now compiles with `REVIT_<year>` and produces a year-specific package.
- **E2 [V]** `BridgeBuildInfo.cs` and `BridgeFileQueue.cs` derive the local queue path from the compiled Revit year.
- **E3 [V]** `install-revit.ps1 -ListDetected` located a local Revit 2020 installation through the configured scan roots and registry.
- **E4 [V]** `build-revit-adapter.ps1` generated a Revit 2020 adapter from the locally installed Revit 2020 API and emitted `build_mode=local-api-adapter`.
- **E5 [V]** The setup payload contains the C# adapter sources and local adapter build script; Revit 2020 `-WhatIf` installation remains non-mutating.
- **E6 [T]** No Revit 2021–2024 installation was available locally, so those four API-specific compilations and live-model tests remain pending.
- **E7 [T]** Revit 2025–2026 require the separate .NET 8 adapter and are outside this package version.
