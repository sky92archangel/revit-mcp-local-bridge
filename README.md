# Revit Command Bridge

A local command bridge for Revit. The Revit add-in only executes controlled Revit API commands; Codex, WorkBuddy, any MCP client, any Function Calling Harness, or any OpenAI-compatible model API can invoke it through a unified JSON, CLI, REST, or MCP interface. It does not depend on Dynamo, nor is it tied to any specific model vendor. All new modeling goes through `execute_plan`: a single plan can combine architecture, structure, MEP, spaces, documentation, parameters, and selection display, instead of adding a new add-in command for each element type.

> Each Revit year must use a DLL compiled against its corresponding API; a single "universal DLL" cannot be shared across versions. This delivery package supports Revit 2020–2026; see [VERSION-SUPPORT.md](./VERSION-SUPPORT.md) for version boundaries.

The single-file installer automatically scans for locally installed Revit 2020–2026 and uses the built-in precompiled adapter packages. End users do not need to select DLLs, install Visual Studio, or manually fill in Revit paths. Build machines must have the corresponding Revit API NuGet packages (via Nice3point) and .NET Framework 4.8 targeting pack / .NET 8 SDK installed.

## Directory Structure

```
revit-mcp-local-bridge/
│
├── src/                               ← ★ Single source of truth (shared across all versions)
│   ├── BridgeModels.cs
│   ├── BridgeRuntime.cs
│   ├── PlanCommandExecutor.cs
│   ├── PlanValues.cs
│   ├── RevitApiExtensions.cs
│   ├── RevitCommandBridgeApp.cs
│   ├── RevitCommandExecutor.cs
│   ├── RevitFamilyOperations.cs
│   ├── RevitGeometryFactory.cs
│   ├── RevitLookups.cs
│   ├── RevitOutputOperations.cs
│   ├── RevitParameterAdmin.cs
│   ├── RevitPlanCreations.cs
│   ├── RevitPlanMutations.cs
│   ├── RevitPlanQueries.cs
│   ├── RevitPlanOperations.cs
│   ├── RevitSectionFactory.cs
│   ├── CommandPanelForm.cs
│   ├── BridgeFailurePreprocessor.cs
│   ├── BridgeFamilyLoadOptions.cs
│   ├── BridgeFileQueue.cs
│   ├── BridgeSchemas.cs
│   ├── BridgeBuildInfo.cs
│   ├── BridgeFailurePreprocessor.cs
│   ├── BridgeFamilyLoadOptions.cs
│   ├── BridgeSchemas.cs
│   ├── GlobalUsings.cs
│   ├── PlanCommandExecutor.cs
│   ├── PlanValues.cs
│   ├── RevitApiExtensions.cs
│   ├── RevitCommandBridgeApp.cs
│   ├── RevitCommandExecutor.cs
│   ├── RevitFamilyOperations.cs
│   ├── RevitGeometryFactory.cs
│   ├── RevitLookups.cs
│   ├── RevitOutputOperations.cs
│   ├── RevitParameterAdmin.cs
│   ├── RevitPlanCreations.cs
│   ├── RevitPlanMutations.cs
│   ├── RevitPlanOperations.cs
│   ├── RevitPlanQueries.cs
│   ├── RevitSectionFactory.cs
│   ├── CommandPanelForm.cs
│   │
│   ├── Adapter/                       ← Version-specific entry points (R20–R27)
│   │   ├── AdapterEntry20.cs .. 27.cs
│   │
│   └── Utils/                         ← Utility classes
│
├── build/                             ← Version manifest
│   └── version-manifest.json
│
├── scripts/                           ← Runtime scripts
│   ├── revit-mcp-server.mjs
│   ├── revit-http-gateway.mjs
│   ├── revit-openai-compatible-chat.mjs
│   ├── bridge-client.mjs
│   ├── send-revit-command.ps1
│   ├── configure-ai-provider.ps1
│   ├── configure-connector.ps1
│   ├── configure-detected-clients.ps1
│   └── start-openai-compatible-chat.ps1
│
├── examples/                          ← Request examples
│   ├── health.json
│   ├── create-level.json
│   ├── preview-rectangle-walls.json
│   ├── preview-universal-plan.json
│   ├── preview-create-family.json
│   ├── preview-export-image.json
│   ├── preview-architecture-output-plan.json
│   └── preview-output-documentation-plan.json
│
├── schemas/
│   └── execute-plan.schema.json
│
├── plans/                             ← Design documents
│   ├── BUILD-PIPELINE.md
│   ├── EXTENSION-PLAN.md
│   ├── CAD-BRIDGE-PLAN.md
│   ├── ATOMIC-ANALYSIS.md
│   ├── FAQ.md
│   └── PR-DESCRIPTION.md
│
├── deploy/
│   ├── RevitCommandBridge.addin.template
│   └── RevitCommandBridge.2026.addin    ← Generated deployment manifest
│
├── verification/
│   └── 2026-08-19-regression.md
│
├── setup/
│   ├── RevitAIHubSetup.cs
│   └── RevitCommandBridge.ico
│
├── depandency/                        ← Precompiled dependencies (SQLite, Json, RevitAPI, etc.)
│
├── release/                           ← Release package output
├── build.ps1                          ← Single-version build
├── build-all.ps1                      ← Multi-version batch build
├── build-installer.ps1                ← Installer packaging
├── install-revit.ps1                  ← Install / detect
├── uninstall-revit.ps1
├── fix_all.ps1
├── fix_value.ps1
├── RevitCommandBridge.csproj          ← Single project, conditional compilation R20–R26
├── RevitCommandBridge.slnx            ← .slnx format solution
├── PROTOCOL.md
├── ARCHITECTURE.md
├── VERSION-SUPPORT.md
├── CONNECTORS.md
├── ENGINEERING-RECORD.md
├── NOTICE.md
├── LICENSE
├── SOURCE-PACKAGE.txt
└── README.md
```

See [plans/BUILD-PIPELINE.md](./plans/BUILD-PIPELINE.md) for more build pipeline details.

## Installation

Build and install tool outputs:

| Script | Output | Description |
|--------|--------|-------------|
| `dotnet build -c "Debug R26"` | `bin\R26\RevitCommandBridge.dll` | Single-version DLL via .csproj + Nice3point NuGet |
| `build.ps1 -RevitVersion 2026` | `dist\RevitCommandBridge-2026\` | Build + package DLL and companion scripts |
| `build-all.ps1` | `dist\RevitCommandBridge-202{0..6}\` | All-version DLLs |
| `build-installer.ps1` | `dist\RevitCommandBridgeSetup.exe` | Single-file installer (embeds all-year DLLs + Node) |
| `build-installer.ps1 -OutputPath "dist\RevitCommandBridgeSetup-2026.exe"` | Custom output filename | Single-version installer for easier version distinction |
| `install-revit.ps1` | → `%LOCALAPPDATA%\RevitCommandBridge\{year}\` | Copy files + write `.addin` manifest |

### Method A: Developer Mode (Build → Install)

```powershell
# 0. Close Revit first

# 1. Check locally installed Revit versions
.\install-revit.ps1 -ListDetected

# 2. Build the specified version (compile DLL + package installer)
.\build.ps1 -RevitVersion 2026
# Or directly via dotnet:
dotnet build -c "Debug R26"

# 3. Install to local Revit
.\install-revit.ps1 -RevitVersion 2026
```

`build.ps1` output:
```
dist\RevitCommandBridge-2026\
├── RevitCommandBridge.dll
├── RevitCommandBridge.pdb
├── bridge.config.json
├── scripts\          ← MCP Server, REST gateway, CLI sender
├── examples\         ← JSON request templates
├── deploy\           ← .addin template
├── schemas\          ← JSON Schema
├── install-revit.ps1
├── uninstall-revit.ps1
├── PROTOCOL.md and other docs
```

`install-revit.ps1` automatically:
1. Detects local Revit installation paths (registry + `C:\Program Files\Autodesk`)
2. Matches the `dist\RevitCommandBridge-{year}\` package
3. Copies all files to `%LOCALAPPDATA%\RevitCommandBridge\{year}\`
4. Writes `%APPDATA%\Autodesk\Revit\Addins\{year}\RevitCommandBridge.addin`
5. Cleans up leftover files from old versions (compared against `install-manifest.json`)
6. Optionally configures AI client connections (`-Connector`)

> Ensure Revit is closed before installation. `install-revit.ps1` supports `-WhatIf` to preview installation locations without actually writing.

### Method B: End-User Mode (Single-File Installer)

```powershell
# Step 1: Build the specified version
.\build.ps1 -RevitVersion 2026

# Step 2: Package installer (default output dist\RevitCommandBridgeSetup.exe)
.\build-installer.ps1

# Can also specify a version-suffixed output filename:
.\build-installer.ps1 -OutputPath "dist\RevitCommandBridgeSetup-2026.exe"
```

Package multiple versions into one installer:
```powershell
.\build.ps1 -RevitVersion 2026
.\build.ps1 -RevitVersion 2027
.\build-installer.ps1 -RevitVersion 2026,2027 -OutputPath "dist\RevitCommandBridgeSetup-2026-2027.exe"
```

Output files can be distributed to machines without a development environment:
```
dist\
├── RevitCommandBridgeSetup.exe          ← Double-click or run from command line
├── RevitCommandBridge-2026\
└── RevitCommandBridge-2027\
```

`RevitCommandBridgeSetup.exe` embeds DLLs and Node.js runtime:
- Auto-scans local Revit installations
- Uses built-in precompiled adapter packages
- Auto-configures recognized MCP clients (Codex, WorkBuddy, Claude Desktop, Cursor, Windsurf, Cline, Roo Code)

### Verify Installation

Start Revit and open a project; the bridge starts automatically. Run a health check:

```powershell
& "$env:LOCALAPPDATA\RevitCommandBridge\2026\scripts\send-revit-command.ps1" `
  -RequestPath "$env:LOCALAPPDATA\RevitCommandBridge\2026\examples\health.json"
```

A response of `"status": "ok"` confirms successful installation. The "Start Bridge" button on the "Revit Command Bridge" ribbon tab also shows connection information.

### Uninstall

```powershell
.\uninstall-revit.ps1 -RevitVersion 2026
```

The uninstaller removes bridge files, `.addin` registration manifest, and queue directories. Different year bridges do not interfere with each other; only the specified version is uninstalled.

## Quick Start

Launch Revit (using 2026 as an example):

~~~powershell
& 'C:\Program Files\Autodesk\Revit 2026\Revit.exe'
~~~

After Revit opens, the bridge starts automatically; the "Start Bridge" button on the "Revit Command Bridge" ribbon tab can confirm connection information. Then perform a read-only health check:

~~~powershell
& "$env:LOCALAPPDATA\RevitCommandBridge\2026\scripts\send-revit-command.ps1" -RequestPath "$env:LOCALAPPDATA\RevitCommandBridge\2026\examples\health.json"
~~~

Preview a universal modeling plan without modifying the model:

~~~powershell
& "$env:LOCALAPPDATA\RevitCommandBridge\2026\scripts\send-revit-command.ps1" -RequestPath "$env:LOCALAPPDATA\RevitCommandBridge\2026\examples\preview-universal-plan.json"
~~~

After confirming the preview returns correctly, change `preview` to `false` in the request and submit. Actual writes use Revit Transaction and can be undone with Revit's native undo.

The "Command Panel" in the ribbon shows current bridge status, recent operations, and current project status; clicking "Refresh Project Status" submits a read-only `health` request. "Preview Plan" does not modify the model; "Confirm Execute" asks for confirmation again; after completion, use Revit native `Ctrl+Z` to undo the transaction.

## One-Click Client Detection

The installer defaults to "auto-detect and configure local AI clients". It scans known MCP client configuration locations, backs up original files, and merges the Revit MCP Server. Currently adapts Codex, WorkBuddy, Claude Desktop, Cursor, Windsurf, Cline, and Roo Code. Unrecognized software does not block installation; the installer always generates standard MCP JSON that any MCP-compatible client can import directly.

Client detection is an installer-layer adapter and does not enter the Revit add-in core. Supporting new software only requires adding a configuration adaptation rule, without redesigning the Revit command protocol or modeling functionality.

## Connecting Different Clients

The installer uses "auto-detect and configure local AI clients" by default. It generates and saves connection configurations to the `connections` folder in the installation directory. Unrecognized clients can use the universal configuration provided by the "Copy MCP" button. See [CONNECTORS.md](./CONNECTORS.md) for details.

| Client Capability | Entry Point | Use Case |
| --- | --- | --- |
| Supports stdio MCP | scripts/revit-mcp-server.mjs | Codex, WorkBuddy, and other MCP Harnesses |
| Can call HTTP | scripts/revit-http-gateway.mjs | Any Function Calling Harness, backend services, automation platforms |
| Only OpenAI-compatible model API | scripts/revit-openai-compatible-chat.mjs | DeepSeek and other models supporting Chat Completions + Tool Calling |
| Can run PowerShell | scripts/send-revit-command.ps1 | Codex Shell, manual testing, batch processing |
| Can only read/write files | %LOCALAPPDATA%\RevitCommandBridge\inbox/outbox | Custom legacy systems or minimal Harnesses |

### Codex MCP

Preferably click "Copy MCP" on the Revit ribbon, then paste into the client's MCP configuration page. For manual configuration, use the year-specific Node runtime bundled with the installer (no separate Node.js installation required):

~~~toml
[mcp_servers.revit]
command = "C:\\Users\\<username>\\AppData\\Local\\RevitCommandBridge\\2026\\runtime\\node.exe"
args = ["C:\\Users\\<username>\\AppData\\Local\\RevitCommandBridge\\2026\\scripts\\revit-mcp-server.mjs"]
~~~

After restarting the Codex session, the client will discover `revit_execute_plan`. This is the long-term main entry; the old `revit_create_wall` etc. tools are retained only for backward compatibility with existing scripts.

### Current Capability Scope

| Module | High-Frequency Operations |
| --- | --- |
| Query & Edit | Document, catalog, elements, parameters, delete, select & zoom |
| Architecture | Levels, grids, walls, floors, wall openings, model lines, rooms, spaces, DirectShape |
| Structure | Beams, columns, braces, and instance placement of loaded structural families |
| MEP | Pipes, ducts, conduits, cable trays, straight/elbow/tee/union connections |
| Family | Template query, new `.rfa`, parameters, types, box/cylinder/extrusion geometry, save, load, place |
| Placement | Unhosted, hosted, face-based, work plane, view, curve-based, and adaptive families |
| Documentation & Annotation | 3D/plan/ceiling/structural framing/drafting/section/elevation/callout views, duplicate & template, sheets, view/schedule placement on sheets, detail lines, text, dimensions, tags, filled regions, revisions & revision clouds |
| Export & Delivery | PNG/JPG/TIFF/BMP images, DWG/DXF, IFC, schedule CSV/TXT, save `.rvt`; export/save must be executed as standalone plans |

When producing documentation, first use `query_catalog(kind=view_types|title_blocks|text_types|filled_region_types|revisions)` to query project resources; when dimensions or tags are needed, use `query_references` to read stable element references, then submit `create_dimension` or `create_tag`. `export` and `save_document` have external file side effects and must each be placed in a single-step `execute_plan`. See [PROTOCOL.md](./PROTOCOL.md) for full parameters and coverage boundaries.

"All of Revit's functionality" encompasses thousands of API objects; the bridge does not expose arbitrary C# execution. New capabilities are uniformly added as controlled atomic steps in `execute_plan`. See [PROTOCOL.md](./PROTOCOL.md) for complete parameter and coverage details.

### Universal MCP JSON

Clients using JSON MCP configuration use the same process parameters:

~~~json
{
  "mcpServers": {
    "revit-command-bridge": {
      "command": "C:\\Users\\<username>\\AppData\\Local\\RevitCommandBridge\\2026\\runtime\\node.exe",
      "args": [
        "C:\\Users\\<username>\\AppData\\Local\\RevitCommandBridge\\2026\\scripts\\revit-mcp-server.mjs"
      ]
    }
  }
}
~~~

### REST & Generic Function Calling Harness

Start the REST gateway (localhost only):

~~~powershell
node "$env:LOCALAPPDATA\RevitCommandBridge\2026\scripts\revit-http-gateway.mjs"
~~~

Query status:

~~~powershell
Invoke-RestMethod 'http://127.0.0.1:8765/health'
~~~

Submit and wait for preview results:

~~~powershell
$body = @{
  operation = 'execute_plan'
  args = @{
    steps = @(
      @{ id = 'check'; operation = 'query_document'; args = @{} }
      @{ id = 'support'; operation = 'create_direct_shape'; args = @{
        name = 'test_support'
        geometry = @(@{ kind = 'box'; min = @{ x = 0; y = 0; z = 0 }; max = @{ x = 3000; y = 2000; z = 2500 } })
      } }
    )
  }
  preview = $true
} | ConvertTo-Json -Depth 12

Invoke-RestMethod -Method Post -Uri 'http://127.0.0.1:8765/commands?wait_seconds=60' -ContentType 'application/json; charset=utf-8' -Body $body
~~~

The remote model API does not directly access the local Revit; the local Harness forwards Function Calling parameters to this REST endpoint, or starts the MCP Server directly. When a model API is needed, run `scripts/configure-ai-provider.ps1` to save configuration, then start the local assistant. API Keys are encrypted per-user using Windows DPAPI and are not written to MCP/REST config files.

## How It Works

~~~mermaid
flowchart LR
    A["Codex / WorkBuddy / Any Model or Harness"] --> B["MCP / REST / CLI"]
    B --> C["Local Atomic JSON Queue"]
    C --> D["Year-matched Revit Add-in"]
    D --> E["ExternalEvent Main Thread Dispatch"]
    E --> F["Controlled Revit API + Transaction"]
    F --> G["Result JSON / Revit Model"]
~~~

The bridge layer does not execute arbitrary C#, Python, or natural language. It only accepts registered top-level operations and controlled atomic steps, validating parameters and target document before calling the Revit API. All write steps in `execute_plan` use a single all-or-nothing Revit Transaction. REST binds to 127.0.0.1 by default and rejects submissions when no active Revit bridge is detected.

## FAQ

| Symptom | Cause & Resolution |
| --- | --- |
| REST returns 503 bridge_not_running | Revit not started, add-in not loaded, or initialization not yet complete |
| Returns "no open project document" | Open or create a .rvt project in Revit and retry |
| Document title mismatch | Request specified `document_title` but the active project is not that document |
| Command ID already exists | Read the existing outbox result, or generate a new id |
| No buttons on the Revit ribbon | Check %APPDATA%\Autodesk\Revit\Addins\<year>\RevitCommandBridge.addin |
| Other year Revit not loading | Re-reference RevitAPI.dll / RevitAPIUI.dll for the target year and recompile the adapter package |

See [PROTOCOL.md](./PROTOCOL.md) for complete request/response and operation parameters. Any Harness can directly use [schemas/execute-plan.schema.json](./schemas/execute-plan.schema.json) for Function Calling / request validation. Long-term extension principles and coverage boundaries are in [ARCHITECTURE.md](./ARCHITECTURE.md). More FAQs in [plans/FAQ.md](./plans/FAQ.md).

## Atomic Operations Reference

All top-level `operation` and `execute_plan` `steps[].operation` values are dispatched via the following registry.

### Top-Level Operations (passed directly as the `operation` field)

| Operation | Description |
|---|---|
| `health` | Bridge health check, returns status and document info |
| `execute_plan` | **Main entry**. Executes multi-step modeling/documentation plan, write steps merged into one transaction |
| `new_project` | Create new project (optional .rte template), optionally save as .rvt |
| `create_family` | Create .rfa family from .rft template with parameters/types/geometry |
| `load_family` | Load existing .rfa into the current project |
| `list_family_templates` | List local Revit family template paths |
| `list_levels` | List project levels (legacy entry) |
| `list_wall_types` | List basic wall types (legacy entry) |
| `create_level` | Create level (legacy entry) |
| `create_grid` | Create grid (legacy entry) |
| `create_wall` | Create straight wall (legacy entry) |
| `create_rectangle_walls` | Create four-sided closed rectangle walls (legacy entry) |

### execute_plan Atomic Steps

#### Query Operations

| Operation | Description |
|---|---|
| `query_document` | Return current document info (title, path, active view, etc.) |
| `query_catalog` | Project resource catalog: levels, categories, views, sheets, schedules, family types, MEP types, links, etc. |
| `query_elements` | Query elements by category/name/family name/ID with parameters |
| `query_references` | Return stable geometric references (faces/edges) of elements |
| `query_parameters` | List all parameters of a single element |
| `query_geometry` | Return element bounding box, solid summary, or face info |
| `query_room` | Query rooms/spaces, supports point lookup or full listing |
| `query_selection` | Read currently selected element IDs, names, categories from the Revit UI |
| `query_mep_network` | Traverse MEP connection topology from a seed element |
| `query_view_range` | Return planar view range (top/cut plane/bottom/view depth) |

#### Creation Operations

| Operation | Description |
|---|---|
| `create_level` | Create a level |
| `create_grid` | Create a grid |
| `create_wall` | Create a straight wall, supports new wall type cloning |
| `create_floor` | Create a floor from a closed loop |
| `create_room` | Create a room |
| `create_space` | Create an MEP space |
| `create_model_curve` | Create a model line |
| `create_direct_shape` | Create DirectShape from geometry primitives (box/cylinder/sphere etc.) |
| `create_swept_shape` | Create solids by sweeping along a path (rectangular/circular/pipe cross-section) |
| `create_mep_curve` | Create MEP piping (pipe/duct/conduit/cable tray) |
| `connect_mep` | Connect MEP elements with straight/elbow/tee/transition/cross connections |
| `create_mep_system` | Create a piping or duct system |
| `create_insulation` | Add pipe/duct insulation |
| `place_family_instance` | Place a family instance with multiple placement methods |
| `load_family` | Load .rfa into the project |
| `create_structural_member` | Create a structural member (beam/brace/column) |
| `create_view` | Create 3D/plan/ceiling/structural framing views |
| `create_sheet` | Create a sheet (optional title block) |
| `place_view_on_sheet` | Place a view onto a sheet |
| `create_opening` | Create an opening (wall/floor/shaft) |
| `create_drafting_view` | Create a drafting view |
| `create_section_view` | Create a section/detail view |
| `create_elevation_view` | Create an elevation view |
| `create_callout` | Create a callout view |
| `duplicate_view` | Duplicate a view, optionally applying a view template |
| `create_view_template` | Create a view template from an existing view |
| `create_detail_curve` | Create a detail line |
| `create_text_note` | Create a text note |
| `create_dimension` | Create a dimension |
| `create_tag` | Create an independent tag |
| `create_filled_region` | Create a filled region |
| `create_revision` | Create a revision |
| `create_revision_cloud` | Create a revision cloud |
| `create_schedule` | Create a schedule (regular/material takeoff/keynote/view list/sheet list/revision) |
| `place_schedule_on_sheet` | Place a schedule onto a sheet |

#### View Properties & Overrides

| Operation | Description |
|---|---|
| `set_view_properties` | Set view properties (scale, crop box, template, detail level, discipline, etc.) |
| `set_element_overrides` | Set element graphic overrides (color, line weight, halftone, etc.) |
| `set_category_overrides` | Set category graphic overrides |
| `manage_view_filters` | Manage view filters (add/remove with rules and overrides) |
| `set_view_range` | Set planar view range (top/cut plane/bottom/view depth) |
| `manage_schedule_fields` | Manage schedule fields (add/remove/hide/sort/filter) |
| `manage_graphics_resources` | Manage graphic resources (line style subcategories/fill patterns) |

#### Edit & Modify

| Operation | Description |
|---|---|
| `set_parameters` | Batch set element parameter values |
| `manage_schema_data` | Extended data read/write and transfer |
| `manage_family_parameters` | Edit family parameters (add/rename/delete/set formula) |
| `manage_project_parameters` | Manage project parameters |
| `duplicate_type` | Duplicate an ElementType, optionally overriding parameters |
| `transform_elements` | Move/copy/rotate/mirror elements |
| `rename_element` | Rename elements (single or batch prefix mode) |
| `set_element_curve` | Modify the LocationCurve of a linear element |
| `delete_elements` | Delete elements |
| `select_elements` | Select and show/zoom to elements |

#### External Operations (must be executed standalone, cannot be mixed with other steps)

| Operation | Description |
|---|---|
| `export` | Export views (PNG/JPG/DWG/DXF/IFC/schedule CSV) |
| `save_document` | Save the current document |

> Total of approximately 73 atomic steps. New capabilities are added as controlled atomic steps to this table; arbitrary C# execution is not exposed. See [PROTOCOL.md](./PROTOCOL.md) and [schemas/execute-plan.schema.json](./schemas/execute-plan.schema.json) for complete parameter definitions.
