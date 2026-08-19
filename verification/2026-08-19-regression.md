# 2026-08-19 Regression Record

## Environment

- Revit API: a local Revit 2020 `RevitAPI.dll`, assembly version `20.0.0.0`.
- Compiler: `.NET Framework 4.x csc.exe` used by `build.ps1`.
- Protocol: `revit-command-bridge/2.0`.

## Verified live Revit 2020 results from this workspace

| Check | Result |
| --- | --- |
| Create family | `RCB_LiveFamily.rfa` saved, parameters/types/box geometry written, loaded into project, one OneLevelBased instance placed. |
| Load existing family | `RCB_LoadOnly.rfa` loaded, family id and one symbol id returned. |
| Restore active project after family workflow | `RCB_LiveValidation.rvt` active; query returned `标高 1`. |
| Architecture/output transaction | One transaction created floor, model curve, isometric 3D view, sheet, and viewport. |

Raw result files were captured outside the distributable source tree during the run and are intentionally not included in this repository.

## Static regression for expanded output module

- `build.ps1 -RevitVersion 2020 -SkipInstaller`: passed with the pre-existing `FormattedText` deprecation warning only.
- `node --check scripts/*.mjs`: passed.
- All `examples/*.json` and `schemas/execute-plan.schema.json`: parsed successfully.
- Atomic operation list and schema enum: both contain 41 entries and match exactly.
- MCP `initialize` and `tools/list`: passed; server reports `0.5.0-revit2020`, including `revit_execute_plan`.

## Explicitly not live-tested in this pass

The added output operations (`create_drafting_view`, `create_section_view`, `create_elevation_view`, `create_callout`, annotations, schedules, revisions, export, save) were API-compiled and protocol-tested, but not run in a live Revit session at the user's request. Use `preview=true` first on the target template/project.
