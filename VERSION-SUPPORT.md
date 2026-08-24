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

MCP profiles are independent per version. REST keeps `2020 = 8765` for compatibility and assigns `2021 = 8766` through `2024 = 8769`; `REVIT_BRIDGE_PORT` can override the selected port.

## Build pipeline

All versions are compiled via a unified `build.ps1` that reads `build/version-manifest.json` and dispatches to the correct compiler pipeline.

```powershell
# Build any single version
.\build.ps1 -RevitVersion 2024
.\build.ps1 -RevitVersion 2025
.\build.ps1 -RevitVersion 2027

# Build all versions defined in the manifest
.\build-all.ps1
```

## Three runtime generations

| Generation | Revit | Runtime | Compiler | Project directory |
|---|---|---|---|---|
| 1st-gen | **2020–2024** | .NET Framework 4.8 | `csc.exe` | _none (command-line)_ |
| 2nd-gen | **2025–2026** | .NET 8 | `dotnet build` | `src-net8/` |
| 3rd-gen | **2027+** | .NET 10+ | `dotnet build` | `src-net10/` |

### 1st gen (2020–2024): .NET Framework 4.8 + csc.exe

- Uses the .NET Framework compiler (`csc.exe`) included with Windows — no Visual Studio or SDK required
- All 22 `.cs` files in `src/` are compiled directly into `RevitCommandBridge.dll`
- Conditional compilation symbols (`REVIT_FORGE_UNITS`, `REVIT_PARAMETER_GROUPS`) are injected from `build/version-manifest.json`
- The `.addin` manifest references `RevitCommandBridge.RevitCommandBridgeApp`

### 2nd gen (2025–2026): .NET 8 + dotnet build

- Requires the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- Project files in `src-net8/` link to `src/*.cs` via `<Compile Include="..\src\*.cs" Link=...>`
- Each year has its own `.csproj` with independent `DefineConstants` and entry adapter class
- The `.addin` manifest references `RevitCommandBridge.RevitCommandBridgeApp25` / `App26`
- `bridge.config.json` contains `runtime: "net8.0-windows"`

### 3rd gen (2027+): .NET 10+

- Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- Project files in `src-net10/` follow the same pattern as 2nd gen
- Each year has its own `.csproj` and entry adapter class
- The `.addin` manifest references `RevitCommandBridge.RevitCommandBridgeApp27`
- `bridge.config.json` contains `runtime: "net10.0-windows"`

## Current support matrix

| Revit version | Build route | Validation state | Notes |
| --- | --- | --- | --- |
| 2020 | csc.exe (net48) | [V] core workflow live-regressed; 0.5.0 output expansion API-compiled | Family/model/view/sheet core workflows ran in Revit 2020; newly added annotations/schedules/export require their own template-specific live regression. |
| 2021–2024 | csc.exe (net48) | [T] implemented, corresponding Revit APIs not present locally | Setup detects each installed version and compiles a year-matched DLL from its local `RevitAPI.dll` / `RevitAPIUI.dll`; each year still needs a real-machine load and modelling test. |
| 2025–2026 | dotnet build (net8.0-windows) | [T] project structure created, restore validated | Requires .NET 8 SDK and Revit 2025/2026 API assemblies to complete compilation. |
| 2027 | dotnet build (net10.0-windows) | [T] project structure created, restore validated | Requires .NET 10 SDK and Revit 2027 API assemblies to complete compilation. |

## Automatic setup behavior

The single-file setup (`install-revit.ps1`) scans registry and standard installation locations for all Revit versions (2020 through 2027+). It lists only detected versions. For each selected version it follows this rule:

1. Use a pre-built, year-matched package from `dist/RevitCommandBridge-{year}`.
2. If no matching package is found, attempt to compile one locally.
3. Verify the selected directory's `RevitAPI.dll` major version matches the requested Revit year.
4. Install into version-isolated add-in and queue directories.

The user does not need Visual Studio. For 2020–2024, the .NET Framework compiler is included with Windows.
For 2025+, the .NET SDK must be installed separately.

## Build a specific version

```powershell
# 2024 (.NET Framework 4.8, csc.exe)
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1 `
  -RevitVersion 2024 `
  -RevitInstallDirectory 'C:\Program Files\Autodesk\Revit 2024'

# 2025 (.NET 8, dotnet build)
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1 `
  -RevitVersion 2025 `
  -RevitInstallDirectory 'C:\Program Files\Autodesk\Revit 2025'

# 2027 (.NET 10, dotnet build)
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1 `
  -RevitVersion 2027 `
  -RevitInstallDirectory 'C:\Program Files\Autodesk\Revit 2027'
```

The result is a separate package per version:

```text
dist\RevitCommandBridge-2024
dist\RevitCommandBridge-2025
dist\RevitCommandBridge-2027
```

## Install a specific version

```powershell
# List detected Revit installations
powershell -NoProfile -ExecutionPolicy Bypass -File .\install-revit.ps1 -ListDetected

# Install a matching package
powershell -NoProfile -ExecutionPolicy Bypass -File .\install-revit.ps1 `
  -RevitVersion 2025 `
  -PackageDirectory .\dist\RevitCommandBridge-2025

# Preview without making changes
powershell -NoProfile -ExecutionPolicy Bypass -File .\install-revit.ps1 `
  -RevitVersion 2025 `
  -PackageDirectory .\dist\RevitCommandBridge-2025 `
  -WhatIf
```

The installer writes its DLL and client scripts to `%LOCALAPPDATA%\RevitCommandBridge\<year>` and its add-in manifest to `%APPDATA%\Autodesk\Revit\Addins\<year>`.

## Evidence

- **E1 [V]** `build.ps1` reads `build/version-manifest.json` and dispatches to `csc` or `dotnet` pipelines.
- **E2 [V]** `BridgeBuildInfo.cs` and `BridgeFileQueue.cs` derive the local queue path from the compiled Revit year.
- **E3 [V]** `install-revit.ps1 -ListDetected` locates Revit 2020+ through the standard/registry discovery path during verification.
- **E4 [V]** `build-revit-adapter.ps1` generates a Revit adapter from the locally installed Revit API.
- **E5 [V]** csc pipeline produces identical reference and symbol arguments to the original `build.ps1`.
- **E6 [V]** `src-net8/` and `src-net10/` projects all restore successfully (`dotnet restore`).
- **E7 [T]** Revit 2021–2024, 2025–2026, and 2027 full compilation require their respective Revit API assemblies; restore and script logic verified.
- **E8 [V/T]** Revit 2020 output expansion (`RevitOutputOperations.cs`) compiled with the local 20.0 API, schema/MCP regression passed; its live output execution was deliberately deferred for this release pass. See [verification/2026-08-19-regression.md](./verification/2026-08-19-regression.md).
