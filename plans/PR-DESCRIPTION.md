# PR: 原子操作扩展 + 多版本构建管道

本次 PR 包含两部分工作：

- **原子操作扩展**：实现 [EXTENSION-PLAN.md](plans/EXTENSION-PLAN.md) 全部范围，原子操作从约 40 个扩展至 **64 个**
- **多版本构建管道**：建立版本清单驱动的跨运行时编译体系，支持 Revit 2020–2027+

总计：**约 38 个文件变更，+6900 行新增**

---

## 提交记录

| Commit | 说明 |
|--------|------|
| `93e8fe7` | 初始计划文档 + 新操作骨架 |
| `dc8ee71` | 计划迭代 |
| `321a69e` | 计划迭代 |
| `d414f7f` | 主实现波：P0/P1/P2 操作 + 查询/出图扩展 |
| `9e09094` | 最终实现：MEP 查询、截面工厂、Schema 管理 |
| `b758753` | 多版本构建管道：version-manifest、src-net8、src-net10、build-all |

---

## 一、原子操作扩展（64 个操作）

### 新增源文件

| 文件 | 用途 | 批次 |
|------|------|------|
| `src/BridgeFailurePreprocessor.cs` | `IFailuresPreprocessor` — 自动消除警告、失败消息写入结果 JSON | P0 §5.4 |
| `src/BridgeFamilyLoadOptions.cs` | `IFamilyLoadOptions` — `load_family` 静默覆盖回调 | P1 §6.7 |
| `src/BridgeSchemas.cs` | Extensible Storage Schema `RcbAiMetadata` — `set`/`get`/`clear`/`transport` | P2 §7.7 |
| `src/RevitParameterAdmin.cs` | 项目参数 CRUD — `add_shared`/`delete`/`list`，`#if` 版本切换 | P3 §8.1 |
| `src/RevitSectionFactory.cs` | 截面轮廓工厂 — `rect`/`circle`/`horseshoe`/`rect_ring`/`circle_ring` | P2 §7.8 |

### 扩展的源文件

| 文件 | 变更 |
|------|------|
| `src/RevitPlanMutations.cs` | 新增 `TransformElements`、`RenameElement`、`SetElementCurve`、`DuplicateType`、`ManageSchemaData`、`ManageFamilyParameters` |
| `src/RevitPlanCreations.cs` | +605 行：MEP 系统、保温层、族加载、放样形状、族实例放置类型全覆盖、结构构件、相机朝向；扩展开洞/连接/坡度/材质 |
| `src/RevitPlanQueries.cs` | +669 行：`QueryReferences`、`QueryParameters`、`QueryGeometry`、`QueryRoom`、`CheckInterferences`、`QueryMepNetwork`、`QueryViewRange`；`QueryCatalog` 扩展 8 种 kind |
| `src/RevitOutputOperations.cs` | +773 行：完整出图标注管线（详图/剖面/立面/详图索引视图、尺寸、标记、填充、修订、明细表）；图形替换、视图过滤器、视图范围、明细表字段管理、图形资源管理、导出增强 |
| `src/PlanCommandExecutor.cs` | 白名单扩展至 65 个操作；`WriteOperations`/`ExternalOperations` 更新；`NormalizeAtomicOperation` 新增约 50 条中文别名；`PlanExecutionContext` 延迟引用解析 |
| `src/RevitPlanOperations.cs` | switch-case 新增约 25 个分发入口 |
| `src/PlanValues.cs` | 新增 `ToRadians`、`ToMillimeters`、`ParseMillimeters`、`DictionaryList`、`List` 等工具方法 |

### 操作清单

| 批次 | 操作 | 状态 |
|------|------|------|
| **P0** 基础闭环 | `create_opening`(vertical/shaft)、`connect_mep`(reducer/cross/extend)、`transform_elements`(move/copy/rotate/mirror)、`BridgeFailurePreprocessor` | 4/4 |
| **P1** 感知与系统 | `check_interferences`、`create_mep_system`、`create_mep_curve` slope、`query_catalog(links)`、`query_parameters`、`rename_element`、`load_family`、`query_geometry`、`set_element_curve`、`query_room`、`duplicate_view` option | 11/11 |
| **P2** 深化表现 | `create_insulation`、`query_mep_network`、`set_element_overrides`+`set_category_overrides`、`manage_view_filters`、`query/set_view_range`、`manage_schedule_fields`、`manage_schema_data`、`create_swept_shape`、`create_view` camera、`manage_family_parameters` | 10/10 |
| **P3** 按需 | `manage_project_parameters`、`manage_graphics_resources` | 2/2 |

最终 `AtomicOperations` 白名单共 **64 项**（含 `query_document` 等 10 个只读、4 个外部操作、12 个独立新增，加上原有 40 个操作中的重叠去重）。

### 架构特征

1. **失败预处理器全覆盖**：所有写事务自动挂接 `BridgeFailurePreprocessor`，无人值守队列不因模态警告卡死
2. **Preview/Deferred 模式**：所有写操作实现 `preview` 分流；依赖前置步骤 ID 的操作实现 deferred 模式
3. **单位体系**：长度统一 mm（入口）/ feet（API 层），角度统一度（契约）/ 弧度（API 层）
4. **中文别名**：`NormalizeAtomicOperation` 为每个新操作绑定中文名称
5. **只读安全**：`check_interferences`/`query_*` 等只读操作不在写事务内执行
6. **性能护栏**：`query_geometry`/`check_interferences`/`query_mep_network` 均设数量/深度上限

### 协议与文档

| 文件 | 变更 |
|------|------|
| `PROTOCOL.md` | 协议版本升级至 `revit-command-bridge/2.0`，操作表 40→64 项 |
| `schemas/execute-plan.schema.json` | operation enum 扩展 |
| `plans/EXTENSION-PLAN.md` | 1478 行 — 完整扩展计划与技术设计 |
| `plans/ATOMIC-ANALYSIS.md` | 141 行 — 300+ sepd-revit-extension 用法的原子化分析（取代 SEPD-ATOMIC-ANALYSIS.md） |
| `plans/FAQ.md` | 117 行 — 常见问题 |

---

## 二、多版本构建管道

### 设计目标

- 每个 Revit 版本有独立的编译入口、运行时和项目文件
- 单一 `build.ps1` 作为统一调度器，按版本矩阵分发
- 共享源码（`src/`）通过 `<Compile Link>` 被所有版本项目引用
- 版本差异（API、运行时、编译符号）集中在版本清单中声明

### 版本矩阵

| 世代 | Revit | 运行时 | 编译工具 | 项目目录 |
|------|-------|--------|---------|---------|
| 第一代 | 2020–2024 | .NET Framework 4.8 | `csc.exe` | _无（纯命令行）_ |
| 第二代 | 2025–2026 | .NET 8 | `dotnet build` | `src-net8/` |
| 第三代 | 2027+ | .NET 10 | `dotnet build` | `src-net10/` |

### 新增文件

| 文件 | 行数 | 说明 |
|------|------|------|
| `build/version-manifest.json` | 72 | 版本矩阵声明 — 每个版本的运行时、编译器、符号、入口类 |
| `src-net8/Directory.Build.props` | 31 | .NET 8 项目公共配置：`net8.0-windows`、源码链接、System.Text.Json |
| `src-net8/RevitCommandBridge.Adapter25.csproj` | 13 | Revit 2025 项目（`REVIT_FORGE_UNITS`+`REVIT_PARAMETER_GROUPS`+`REVIT_NET8`+`REVIT_2025`） |
| `src-net8/RevitCommandBridge.Adapter26.csproj` | 13 | Revit 2026 项目（同上，`REVIT_2026` 替换） |
| `src-net8/AdapterEntry25.cs` | 21 | 2025 入口：`RevitCommandBridgeApp25 : RevitCommandBridgeApp` |
| `src-net8/AdapterEntry26.cs` | 21 | 2026 入口：`RevitCommandBridgeApp26 : RevitCommandBridgeApp` |
| `src-net10/Directory.Build.props` | 31 | .NET 10 项目公共配置：`net10.0-windows`、System.Text.Json 10.0 |
| `src-net10/RevitCommandBridge.Adapter27.csproj` | 16 | Revit 2027 项目（追加 `REVIT_NET10`+`REVIT_2027`） |
| `src-net10/AdapterEntry27.cs` | 21 | 2027 入口：`RevitCommandBridgeApp27 : RevitCommandBridgeApp` |
| `build-all.ps1` | 56 | 批量编译 — 遍历 version-manifest 全部或指定版本依次编译 |
| `plans/BUILD-PIPELINE.md` | 476 | 构建管道设计文档 |
| `plans/REVIT2025-PORT.md` | 382 | Revit 2025+（.NET 8/10）移植方案 |

### 修改的源文件

| 文件 | 变更 |
|------|------|
| `src/BridgeBuildInfo.cs` | 新增 `SetApiYear(int)` — 各年份 AdapterEntry 在 `OnStartup` 中设定版本 |
| `src/RevitCommandBridgeApp.cs` | 移除 `sealed` — 允许各年份入口类继承 |

### 管道执行流程

```
build.ps1 -RevitVersion 2026
     │
     ▼
加载 build/version-manifest.json
     │
     ├── compiler = "csc"   → 管道 A: csc.exe（2020-2024）
     │                       从 manifest 读取 define_symbols + framework_references
     │                       构造 csc 命令行 → 编译
     │
     └── compiler = "dotnet" → 管道 B: dotnet build（2025+）
                              读取 project_file + define_symbols
                              注入 Directory.Build.props → dotnet build
     │
     ▼
     公共打包步骤（复制 scripts/examples/schemas/... → dist/）
     写入 bridge.config.json（含 runtime 字段）
```

### 管道特征

1. **版本清单驱动**：`build/version-manifest.json` 是单一数据源，新增版本只需改清单 + 加 `.csproj`/`AdapterEntry`，`build.ps1` 不动
2. **独立运行时目录**：每个 .NET 世代有独立目录（`src-net8/`、`src-net10/`），`Directory.Build.props` 共享该代配置
3. **源码链接**：各版本项目通过 `<Compile Include="..\src\*.cs" Link=...>` 引用 `src/` 源码
4. **条件编译**：版本差异用 `#if REVIT_FORGE_UNITS` / `#if REVIT_NET8` / `#if REVIT_NET10` 等符号切换
5. **批量编译**：`build-all.ps1` 遍历全部版本，一个版本失败不中断其他
6. **元数据输出**：`bridge.config.json` 包含 `runtime` 字段（`net48`/`net8.0-windows`/`net10.0-windows`）

### 新增版本的标准流程（以 Revit 2028 为例）

1. 在 `build/version-manifest.json` 添加一条
2. 在对应运行时目录（`src-net8/` 或新建 `src-netXX/`）创建 `.csproj` 和 `AdapterEntry28.cs`
3. 在共享 `src/` 中添加 `#if REVIT_2028` 分支（如果需要）

---

## 三、架构特征（汇总）

1. **失败预处理器全覆盖** — 写事务自动挂接 `BridgeFailurePreprocessor`
2. **Preview/Deferred 模式** — 写操作 preview 分流，依赖前置 ID 的操作 deferred
3. **单位体系** — mm/度（入口）→ feet/弧度（API 层）
4. **中文别名** — 约 50 条中文→英文操作名映射
5. **只读安全** — 只读操作禁止 `Transaction`/`Set`/`Delete`
6. **性能护栏** — 查询操作设数量/深度上限
7. **版本兼容** — `#if` 条件编译支持 API/运行时差异
8. **版本清单驱动** — `version-manifest.json` 统一管理各版本编译配置
9. **独立运行时目录** — `src-net8/`、`src-net10/` 分代管理项目文件
10. **源码链接** — 所有版本共享 `src/`，避免重复维护
