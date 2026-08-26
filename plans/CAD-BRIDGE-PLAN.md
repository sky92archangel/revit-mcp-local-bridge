# CAD 命令桥实施计划

## 1. 背景

Revit 命令桥（RCB）的架构已证明"协议层 → 文件队列 → CAD 平台插件"三层分离模式可行。本计划将同一架构迁移至 AutoCAD，构建 **CAD 命令桥（CCB）**，使 MCP/REST/CLI 客户端通过相同协议操作 AutoCAD。

## 2. 目标

- 复用现有 MCP 服务端、REST 网关、文件队列客户端代码
- 新增一套 AutoCAD .NET 插件，实现与 Revit 版对等的原子操作
- 支持 AutoCAD 2025–2027（对应 .NET 8/10）
- 保持 `execute_plan` 协议不变，客户端无需区分后端是 Revit 还是 CAD

## 3. 项目结构

```
cad-mcp-local-bridge/
│
├── src/                              ← ★ 单一事实源（CAD 版 .cs）
│   ├── CadBridgeApp.cs               ← IExtensionApplication 入口
│   ├── CadBridgeRuntime.cs           ← 运行时管理（心跳、队列监听）
│   ├── CadFileQueue.cs               ← 文件队列读写（与 RCB 相同，可直接复制）
│   ├── CadModels.cs                  ← 请求/响应模型（与 RCB 相同）
│   ├── CadPlanExecutor.cs            ← execute_plan 调度器
│   ├── CadPlanOperations.cs          ← 操作路由表
│   ├── CadQueries.cs                 ← 查询类操作
│   ├── CadCreations.cs               ← 创建类操作
│   ├── CadMutations.cs               ← 编辑/删除类操作
│   ├── CadOutputOperations.cs        ← 导出/保存操作
│   ├── CadLookups.cs                 ← 实体查找/属性读取
│   ├── CadPropertyAdmin.cs           ← 扩展属性管理
│   ├── CadBlockOperations.cs         ← 块定义/块参照操作
│   ├── CadGeometryFactory.cs         ← 几何图元生成
│   └── CadLayerManager.cs            ← 图层管理
│
├── src-net8/                         ← .NET 8 项目（AutoCAD 2025–2026）
│   ├── Directory.Build.props
│   ├── CadCommandBridge.25.csproj
│   ├── CadCommandBridge.26.csproj
│   └── AdapterEntry25.cs / 26.cs
│
├── src-net10/                        ← .NET 10 项目（AutoCAD 2027）
│   ├── Directory.Build.props
│   ├── CadCommandBridge.27.csproj
│   └── AdapterEntry27.cs
│
├── deps/                             ← AutoCAD API 引用
│   ├── acdbmgd.dll
│   ├── acmgd.dll
│   ├── accoremgd.dll
│   └── acdbmgdbrep.dll
│
├── scripts/                          ← 从 RCB 复制（改版本标识）
│   ├── cad-mcp-server.mjs
│   ├── cad-http-gateway.mjs
│   ├── bridge-client.mjs             ← 复用
│   └── send-cad-command.ps1
│
├── deploy/
│   └── CadCommandBridge.bundle        ← AutoCAD .bundle 包格式
│
├── setup/
│   ├── CadSetup.cs                   ← 安装器（检测 AutoCAD 安装路径）
│   └── CadCommandBridge.ico
│
├── build/
│   └── version-manifest.json
│
├── build.ps1
├── build-installer.ps1
└── README.md
```

## 4. 实施阶段

### 阶段一：骨架搭建（预估 3 天）

| 任务 | 产出 |
|---|---|
| 新建 `cad-mcp-local-bridge` 仓库 | 项目结构 |
| 复制并调整 `revit-mcp-server.mjs` → `cad-mcp-server.mjs` | MCP 服务端 |
| 复制 `bridge-client.mjs`（不变） | 队列客户端 |
| 编写 `CadBridgeApp.cs` — `IExtensionApplication` | 插件入口 |
| 编写 `CadBridgeRuntime.cs` — 心跳、队列轮询 | 运行时 |

### 阶段二：核心协议（预估 2 天）

| 任务 | 产出 |
|---|---|
| 实现 `CadFileQueue`（复用 RCB 设计） | 文件队列 |
| 实现 `CadPlanExecutor` | 计划调度 |
| 实现 `CadModels`（BridgeRequest/BridgeResponse） | 数据模型 |
| 实现在 `health` 操作 | 健康检查 |

### 阶段三：原子操作（预估 5 天）

| 操作 | CAD API | 难度 |
|---|---|---|
| `query_document` | `Application.DocumentManager.MdiActiveDocument` | ★ |
| `query_catalog` | `Database.LayerTable`, `LinetypeTable`, `BlockTable` | ★★ |
| `query_elements` | `BlockTableRecord` 遍历，`ObjectId` 过滤 | ★★ |
| `create_line` | `Line` + `BlockTableRecord.AppendEntity` | ★ |
| `create_circle` | `Circle` | ★ |
| `create_polyline` | `Polyline`（顶点集合） | ★★ |
| `create_text` | `DBText`, `MText` | ★ |
| `create_dimension` | `RotatedDimension`, `AlignedDimension` | ★★★ |
| `create_hatch` | `Hatch` + `Loop` | ★★★ |
| `create_block_definition` | `BlockTableRecord`（非空间块） | ★★ |
| `insert_block_reference` | `BlockReference` | ★★ |
| `set_properties` | 图层、颜色、线型、线宽 | ★ |
| `delete_elements` | `Entity.Erase()` | ★ |
| `transform_elements` | `Entity.TransformBy()` | ★★ |
| `set_layer` | 创建/冻结/锁定/开关图层 | ★★ |
| `export` | `Application.Export()` PDF/DWF | ★★★ |

### 阶段四：构建与安装（预估 2 天）

| 任务 | 产出 |
|---|---|
| `build.ps1` — 编译 CAD 适配包 | .dll + .bundle |
| `build-installer.ps1` — 打包安装器 | CadCommandBridgeSetup.exe |
| `.bundle` 包格式（PackageContents.xml） | AutoCAD 自动加载 |

## 5. AutoCAD 插件加载方式

AutoCAD 使用 `.bundle` 包（类似 Revit 的 `.addin`），结构如下：

```
CadCommandBridge.bundle/
├── PackageContents.xml
├── Contents/
│   └── Windows/
│       └── 2026/
│           ├── CadCommandBridge.dll
│           └── CadCommandBridge.25.dbx   ← 如有托管 C++ 依赖
└── Scripts/
    └── cad-mcp-server.mjs
```

`PackageContents.xml`：

```xml
<?xml version="1.0" encoding="utf-8"?>
<ApplicationPackage SchemaVersion="1.0">
  <AutodeskProduct>
    <ProductDescription>CAD Command Bridge</ProductDescription>
  </AutodeskProduct>
  <ComponentEntry
    AppName="CadCommandBridge"
    ModuleName="./Contents/Windows/2026/CadCommandBridge.dll"
    AppType=".NET"
    LoadOnAutoCADStartup="true">
    <RuntimeRequirements OS="Win64" Platform="AutoCAD*" SeriesMax="R26.0" SeriesMin="R25.0" />
  </ComponentEntry>
</ApplicationPackage>
```

.bundle 放入 `%APPDATA%\Autodesk\ApplicationPlugins\` 后 AutoCAD 自动加载，无需注册表。

## 6. API 差异备忘

| 项目 | Revit | AutoCAD |
|---|---|---|
| 入口接口 | `IExternalApplication` | `IExtensionApplication` |
| 应用对象 | `UIApplication` / `ControlledApplication` | `Application`（静态类） |
| 文档对象 | `Document`（项目/族） | `Database` |
| 事务 | `Transaction` | `Transaction`（类似，但需手动管理） |
| 元素 ID | `ElementId`（struct） | `ObjectId`（struct） |
| 参数系统 | `Parameter` / `BuiltInParameter` | `TypedValue` / `Resbuf` / XData |
| 几何体系 | `XYZ`, `CurveLoop`, `Solid` | `Point3d`, `Curve3d`, `Solid3d` |
| 事件模型 | 多种 Revit 专用事件 | `DocumentLockModeChanged` 等 |
| 单位制 | 英制英尺（内部） | 英制英寸（内部） |

## 7. 需要适配的单位转换

Revit 内部单位为英尺（`1 Revit foot = 304.8 mm`），AutoCAD 内部单位为英寸（`1 AutoCAD inch = 25.4 mm`）。参数传递统一用毫米：

```csharp
// Revit RCB
public static double ToFeet(double mm) => mm / 304.8;

// AutoCAD CCB
public static double ToInches(double mm) => mm / 25.4;
public static double ToMillimeters(double inches) => inches * 25.4;
```

## 8. 建议

1. 第一阶段不追求功能完整，先跑通"CAD 启动 → 桥接心跳 → MCP 健康检查"链路
2. `query_catalog` 优先实现图层查询，这是其他所有操作的基础
3. `set_properties` 优先实现设置图层和颜色，这是最常用的 CAD 修改
4. 事务管理使用 `using` 模式确保 Dispose，避免 AutoCAD 崩溃
5. 长期运行建议使用 `Idle` 事件或 `Timer` 轮询，避免阻塞主线程
