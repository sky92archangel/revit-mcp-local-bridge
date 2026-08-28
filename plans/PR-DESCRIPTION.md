# PR: 原子操作扩展 + 构建管道

## 第一部分：原子操作扩展

原子操作从约 40 个扩展至 **64 个**。

### 新增源文件

| 文件 | 用途 |
|------|------|
| `src/BridgeFailurePreprocessor.cs` | `IFailuresPreprocessor` — 自动消除警告、失败消息写入结果 JSON |
| `src/BridgeFamilyLoadOptions.cs` | `IFamilyLoadOptions` — `load_family` 静默覆盖回调 |
| `src/BridgeSchemas.cs` | Extensible Storage Schema `RcbAiMetadata` — `set`/`get`/`clear`/`transport` |
| `src/RevitParameterAdmin.cs` | 项目参数 CRUD — `add_shared`/`delete`/`list`，`#if` 版本切换 |
| `src/RevitSectionFactory.cs` | 截面轮廓工厂 — `rect`/`circle`/`horseshoe`/`rect_ring`/`circle_ring` |

### 扩展的源文件

| 文件 | 变更 |
|------|------|
| `src/RevitPlanMutations.cs` | 新增 `TransformElements`、`RenameElement`、`SetElementCurve`、`DuplicateType`、`ManageSchemaData`、`ManageFamilyParameters` |
| `src/RevitPlanCreations.cs` | +605 行：MEP 系统、保温层、族加载、放样形状、族实例放置类型全覆盖、结构构件、相机朝向；扩展开洞/连接/坡度/材质 |
| `src/RevitPlanQueries.cs` | +669 行：`QueryReferences`、`QueryParameters`、`QueryGeometry`、`QueryRoom`、`CheckInterferences`、`QueryMepNetwork`、`QueryViewRange`；`QueryCatalog` 扩展 8 种 kind |
| `src/RevitOutputOperations.cs` | +773 行：完整出图标注管线；图形替换、视图过滤器、视图范围、明细表字段管理、图形资源管理、导出增强 |
| `src/PlanCommandExecutor.cs` | 白名单扩展至 65 个操作；`WriteOperations`/`ExternalOperations` 更新；中文别名约 50 条 |
| `src/RevitPlanOperations.cs` | switch-case 新增约 25 个分发入口 |
| `src/PlanValues.cs` | 新增 `ToRadians`、`ToMillimeters`、`ParseMillimeters`、`DictionaryList`、`List` 等工具方法 |

### 操作清单

| 批次 | 操作 | 状态 |
|------|------|------|
| **P0** 基础闭环 | `create_opening`(vertical/shaft)、`connect_mep`(reducer/cross/extend)、`transform_elements`(move/copy/rotate/mirror)、`BridgeFailurePreprocessor` | 4/4 |
| **P1** 感知与系统 | `check_interferences`、`create_mep_system`、`create_mep_curve` slope、`query_catalog(links)`、`query_parameters`、`rename_element`、`load_family`、`query_geometry`、`set_element_curve`、`query_room`、`duplicate_view` option | 11/11 |
| **P2** 深化表现 | `create_insulation`、`query_mep_network`、`set_element_overrides`+`set_category_overrides`、`manage_view_filters`、`query/set_view_range`、`manage_schedule_fields`、`manage_schema_data`、`create_swept_shape`、`create_view` camera、`manage_family_parameters` | 10/10 |
| **P3** 按需 | `manage_project_parameters`、`manage_graphics_resources` | 2/2 |

最终白名单共约 **73 项**（截至 `PlanCommandExecutor.cs` 中 73 个 case）。

## 第二部分：版本构建管道

### 版本矩阵

| Revit | 运行时 | 编译工具 |
|-------|--------|---------|
| 2020–2024 | .NET Framework 4.8 | `dotnet build` (MSBuild) |
| 2025–2026 | .NET 8 Windows | `dotnet build` (MSBuild) |

- 统一 `.csproj` 管道（14 个配置：Debug/Release × R20–R26），所有版本共享 `src/` 源码
- Nice3point NuGet 自动获取对应年份的 `RevitAPI.dll` / `RevitAPIUI.dll`
- 版本差异通过 `.csproj` 的 `PropertyGroup` 注入递增符号（`REVIT2022_OR_GREATER` 等）
- `build-all.ps1` 批量编译全部版本

## 架构特征

1. **失败预处理器全覆盖** — 写事务自动挂接 `BridgeFailurePreprocessor`
2. **Preview/Deferred 模式** — 写操作 preview 分流，依赖前置 ID 的操作 deferred
3. **单位体系** — mm/度（入口）→ feet/弧度（API 层）
4. **中文别名** — 约 50 条中文→英文操作名映射
5. **只读安全** — 只读操作禁止 `Transaction`/`Set`/`Delete`
6. **性能护栏** — 查询操作设数量/深度上限
7. **版本兼容** — `#if` 条件编译 `REVIT_FORGE_UNITS` / `REVIT_PARAMETER_GROUPS`
