# Revit version support

## What is shared across versions

The command protocol, JSON schema, MCP tool names, REST contract, and user workflow are version-independent. The Revit add-in DLL is not. Every Revit major version needs a DLL compiled against that version's `RevitAPI.dll` and `RevitAPIUI.dll`.

Each installed version also has an isolated command queue:

```text
%LOCALAPPDATA%\RevitCommandBridge\2020
%LOCALAPPDATA%\RevitCommandBridge\2021
...
%LOCALAPPDATA%\RevitCommandBridge\2026
```

This prevents an agent connected to one Revit version from sending a command to another version's open project.

MCP profiles are independent per version. REST assigns ports sequentially: `2020 = 8765`, `2021 = 8766`, ..., `2026 = 8771`; `REVIT_BRIDGE_PORT` can override the selected port.

## Build pipeline

All versions share a single `.csproj` and are compiled via `dotnet build` using 14 configurations (Debug/Release × R20–R26). Nice3point NuGet packages provide the matching Revit API assemblies automatically.

```powershell
# Build any single version (Debug)
dotnet build -c "Debug R26"

# Build via wrapper script
.\build.ps1 -RevitVersion 2026

# Build all versions
.\build-all.ps1
```

## Runtime matrix

| Revit | Runtime | Compiler | SDK required |
|-------|---------|----------|-------------|
| **2020–2024** | .NET Framework 4.8 | `dotnet build` (MSBuild) | .NET Framework 4.8 targeting pack |
| **2025–2026** | .NET 8.0 Windows | `dotnet build` | .NET 8 SDK |

- Revit API references come from `Nice3point.Revit.Api.RevitAPI` / `RevitAPIUI` NuGet packages — no manual DLL management
- All `.cs` files in `src/` are compiled into `RevitCommandBridge.dll` per configuration
- Conditional compilation symbols are injected via `.csproj` `PropertyGroup` per configuration:
  - `REVIT2022_OR_GREATER` (R22+)
  - `REVIT2023_OR_GREATER` (R23+)
  - `REVIT2024_OR_GREATER` (R24+)
  - `REVIT2025_OR_GREATER` (R25+)
- Each adapter entry (`AdapterEntry{20..26}.cs`) registers `RevitBuildInfo.SetApiYear()` at startup
- `bridge.config.json` contains `runtime: "net48"` or `runtime: "net8.0-windows"` per version

## Current support matrix

| Revit version | Runtime | Validation state | Notes |
| --- | --- | --- | --- |
| 2020 | net48 | [V] core workflow live-regressed | Family/model/view/sheet core workflows ran in Revit 2020. |
| 2021–2024 | net48 | [T] implemented, not locally verified | Each year needs its own Revit API DLL to compile and live-test. |
| 2025 | net8.0-windows | [T] implemented, not locally verified | New .NET 8 runtime baseline; `ForgeTypeId` always available. |
| 2026 | net8.0-windows | [V] compile-only; live regression deferred | API-compiled against 2026 NuGet packages; no real-machine test yet. |

## Automatic setup behavior

The single-file setup (`install-revit.ps1`) scans registry and standard installation locations for all Revit versions (2020 through 2026). It lists only detected versions. For each selected version it follows this rule:

1. Use a pre-built, year-matched package from `dist/RevitCommandBridge-{year}`.
2. If no matching package is found, attempt to compile one locally via `dotnet build`.
3. Verify the selected directory's `RevitAPI.dll` major version matches the requested Revit year.
4. Install into version-isolated add-in and queue directories.

The user needs .NET SDK (for R25–R26) or .NET Framework targeting pack (for R20–R24). Visual Studio is optional.

## Build a specific version

```powershell
# 2026 (.NET 8.0, dotnet build)
.\build.ps1 -RevitVersion 2026

# Or directly with dotnet:
dotnet build -c "Release R26"
```

The result is a separate package per version:

```text
dist\RevitCommandBridge-2026\
├── RevitCommandBridge.dll
├── RevitCommandBridge.pdb
├── bridge.config.json
├── scripts\
├── examples\
├── deploy\
├── schemas\
├── install-revit.ps1
├── uninstall-revit.ps1
└── PROTOCOL.md and other docs
```

## Install a specific version

```powershell
# List detected Revit installations
.\install-revit.ps1 -ListDetected

# Install a matching package
.\install-revit.ps1 -RevitVersion 2026

# Preview without making changes
.\install-revit.ps1 -RevitVersion 2026 -WhatIf
```

The installer writes its DLL and client scripts to `%LOCALAPPDATA%\RevitCommandBridge\<year>` and its add-in manifest to `%APPDATA%\Autodesk\Revit\Addins\<year>`.

## Build a specific version (legacy csc.exe path)

The old `csc.exe` pipeline (described in `plans/BUILD-PIPELINE.md` v1) has been superseded by the `.csproj` + `dotnet build` pipeline. The `build/version-manifest.json` now uses `"compiler": "dotnet"` (schema v2). The csc pipeline remains documented for reference only.

## Evidence

- **E1 [V]** `RevitCommandBridge.csproj` defines 14 configurations (Debug/Release × R20–R26) with correct framework targets and conditional symbols.
- **E2 [V]** Nice3point NuGet packages resolve year-specific `RevitAPI.dll`/`RevitAPIUI.dll` automatically.
- **E3 [V]** `AdapterEntry{20..26}.cs` register runtime API year via `BridgeBuildInfo.SetApiYear()`.
- **E4 [V]** `build/version-manifest.json` (schema v2) contains all 7 versions with `compiler: "dotnet"`.
- **E5 [V]** R2020 and R2026 both compile successfully via `dotnet build`.
- **E6 [T]** R2021–R2025 full compilation requires their respective NuGet packages; script logic verified.
