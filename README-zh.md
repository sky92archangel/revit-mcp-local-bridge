# Revit 命令桥 (Revit Command Bridge)

面向 Revit 的本地命令桥。Revit 插件只执行受控 Revit API 命令；Codex、WorkBuddy、任意 MCP 客户端、任意 Function Calling Harness 或 OpenAI 兼容模型 API，通过统一 JSON、CLI、REST 或 MCP 接口调用。它不依赖 Dynamo，也不绑定模型厂商。新建模统一走 `execute_plan`：一个计划可组合建筑、结构、机电、空间、出图、参数和选中显示，而不是为每一种构件增加一个插件命令。

> 每个 Revit 年份必须使用对应 API 编译出的 DLL，不能共用一个"万能 DLL"。本交付包支持 Revit 2025–2027；版本边界见 [VERSION-SUPPORT.md](./VERSION-SUPPORT.md)。

单文件安装器会自动扫描本机 Revit 2025–2027，使用内置预编译适配包。普通用户不需要选择 DLL、安装 Visual Studio 或手工填写 Revit 路径。构建机器需安装对应年份的 Revit 和 .NET 8/10 SDK。

## 目录结构

```
revit-mcp-local-bridge/
│
├── src/                               ← ★ 单一事实源（22 个 .cs，全部版本共享）
│   ├── BridgeModels.cs
│   ├── BridgeRuntime.cs
│   ├── PlanCommandExecutor.cs
│   ├── RevitCommandExecutor.cs
│   ├── RevitCommandBridgeApp.cs
│   ├── RevitLookups.cs
│   ├── RevitParameterAdmin.cs
│   ├── RevitPlanCreations.cs
│   ├── RevitPlanMutations.cs
│   ├── RevitPlanQueries.cs
│   ├── RevitPlanOperations.cs
│   ├── RevitFamilyOperations.cs
│   ├── RevitGeometryFactory.cs
│   ├── RevitSectionFactory.cs
│   ├── RevitOutputOperations.cs
│   ├── CommandPanelForm.cs
│   ├── BridgeFailurePreprocessor.cs
│   ├── BridgeFamilyLoadOptions.cs
│   ├── BridgeFileQueue.cs
│   ├── BridgeSchemas.cs
│   ├── BridgeBuildInfo.cs
│   └── PlanValues.cs
│
├── build/                             ← 版本矩阵
│   └── version-manifest.json
│
├── src-net8/                          ← .NET 8 项目族（Revit 2025–2026）
│   ├── Directory.Build.props
│   ├── RevitCommandBridge.Adapter25.csproj
│   ├── RevitCommandBridge.Adapter26.csproj
│   ├── AdapterEntry25.cs
│   └── AdapterEntry26.cs
│
├── src-net10/                         ← .NET 10 项目族（Revit 2027+）
│   ├── Directory.Build.props
│   ├── RevitCommandBridge.Adapter27.csproj
│   └── AdapterEntry27.cs
│
├── scripts/                           ← 运行时脚本
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
├── examples/                          ← 请求示例
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
├── plans/                             ← 设计方案
│   ├── BUILD-PIPELINE.md
│   ├── EXTENSION-PLAN.md
│   ├── REVIT2025-PORT.md
│   ├── SEPD-ATOMIC-ANALYSIS.md
│   ├── FAQ.md
│   └── PR-DESCRIPTION.md
│
├── deploy/
│   └── RevitCommandBridge.addin.template
│
├── verification/
│   └── 2026-08-19-regression.md
│
├── setup/
│   ├── RevitAIHubSetup.cs
│   └── RevitCommandBridge.ico
│
├── release/                           ← 发布包输出
├── build.ps1                          ← 单版本编译
├── build-all.ps1                      ← 全版本批量编译
├── build-installer.ps1                ← 安装器打包
├── install-revit.ps1                  ← 安装/检测
├── uninstall-revit.ps1
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

更多构建管道细节见 [plans/BUILD-PIPELINE.md](./plans/BUILD-PIPELINE.md)。

## 安装

构建和安装工具的产出位置：

| 脚本 | 产出 | 说明 |
|------|------|------|
| `build.ps1 -RevitVersion 2026` | `dist\RevitCommandBridge-2026\RevitCommandBridge.dll` | 单版本 DLL + 配套脚本 |
| `build-all.ps1` | `dist\RevitCommandBridge-202{5..7}\` | 全部版本 DLL |
| `build-installer.ps1` | `dist\RevitCommandBridgeSetup.exe` | 单文件安装器（内置所有年份 DLL + Node） |
| `build-installer.ps1 -OutputPath "dist\RevitCommandBridgeSetup-2026.exe"` | 自定义输出文件名 | 单版本安装器，方便版本区分 |
| `install-revit.ps1` | → `%LOCALAPPDATA%\RevitCommandBridge\{year}\` | 复制文件 + 写 `.addin` 清单 |

### 方式 A：开发者模式（编译 → 安装）

```powershell
# 0. 先关闭 Revit

# 1. 查看本机安装的 Revit 版本
.\install-revit.ps1 -ListDetected

# 2. 构建指定版本（编译 DLL + 打包安装器）
.\build.ps1 -RevitVersion 2026

# 3. 安装到本机 Revit
.\install-revit.ps1 -RevitVersion 2026
```

`build.ps1` 产出：
```
dist\RevitCommandBridge-2026\
├── RevitCommandBridge.dll
├── RevitCommandBridge.pdb
├── bridge.config.json
├── scripts\          ← MCP Server、REST 网关、CLI 发送器
├── examples\         ← JSON 请求模板
├── deploy\           ← .addin 模板
├── schemas\          ← JSON Schema
├── install-revit.ps1
├── uninstall-revit.ps1
├── PROTOCOL.md 等文档
```

`install-revit.ps1` 自动完成：
1. 检测本机 Revit 安装路径（注册表 + `C:\Program Files\Autodesk`）
2. 匹配 `dist\RevitCommandBridge-{year}\` 包
3. 复制所有文件至 `%LOCALAPPDATA%\RevitCommandBridge\{year}\`
4. 写入 `%APPDATA%\Autodesk\Revit\Addins\{year}\RevitCommandBridge.addin`
5. 清理旧版本残留文件（对比 `install-manifest.json`）
6. 可选配置 AI 客户端连接（`-Connector`）

> 安装前确保 Revit 已关闭。`install-revit.ps1` 支持 `-WhatIf` 预览安装位置不实际写入。

### 方式 B：最终用户模式（单文件安装器）

```powershell
# 步骤 1：编译指定版本
.\build.ps1 -RevitVersion 2026

# 步骤 2：打包安装器（默认输出 dist\RevitCommandBridgeSetup.exe）
.\build-installer.ps1

# 也可指定版本号后缀的输出文件名：
.\build-installer.ps1 -OutputPath "dist\RevitCommandBridgeSetup-2026.exe"
```

打包多个版本到同一个安装器：
```powershell
.\build.ps1 -RevitVersion 2026
.\build.ps1 -RevitVersion 2027
.\build-installer.ps1 -RevitVersion 2026,2027 -OutputPath "dist\RevitCommandBridgeSetup-2026-2027.exe"
```

产出文件可分发到没有开发环境的机器：
```
dist\
├── RevitCommandBridgeSetup.exe          ← 双击或命令行运行
├── RevitCommandBridge-2026\
└── RevitCommandBridge-2027\
```

`RevitCommandBridgeSetup.exe` 内置了 DLL 和 Node.js 运行时：
- 自动扫描本机 Revit
- 使用内置预编译适配包
- 自动配置已识别的 MCP 客户端（Codex、WorkBuddy、Claude Desktop、Cursor、Windsurf、Cline、Roo Code）

### 验证安装

启动 Revit 并打开项目后，桥接自动启动。运行健康检查：

```powershell
& "$env:LOCALAPPDATA\RevitCommandBridge\2026\scripts\send-revit-command.ps1" `
  -RequestPath "$env:LOCALAPPDATA\RevitCommandBridge\2026\examples\health.json"
```

返回 `"status": "ok"` 即安装成功。功能区"Revit 命令桥"选项卡的"启动桥接"按钮也可确认连接信息。

### 卸载

```powershell
.\uninstall-revit.ps1 -RevitVersion 2026
```

卸载程序会移除桥接文件、`.addin` 注册清单和队列目录。不同年份的桥接互不干扰，只卸载指定版本。

## 快速开始

启动 Revit（以 2026 为例）：

~~~powershell
& 'C:\Program Files\Autodesk\Revit 2026\Revit.exe'
~~~

打开 Revit 后，桥接会自动启动；功能区“Revit 命令桥”的“启动桥接”按钮可用于确认连接信息。随后执行一次只读健康检查：

~~~powershell
& "$env:LOCALAPPDATA\RevitCommandBridge\2026\scripts\send-revit-command.ps1" -RequestPath "$env:LOCALAPPDATA\RevitCommandBridge\2026\examples\health.json"
~~~

预览一个通用建模计划，不修改模型：

~~~powershell
& "$env:LOCALAPPDATA\RevitCommandBridge\2026\scripts\send-revit-command.ps1" -RequestPath "$env:LOCALAPPDATA\RevitCommandBridge\2026\examples\preview-universal-plan.json"
~~~

确认预览返回正确后，把请求中的 preview 改为 false 再提交。实际写入使用 Revit Transaction，可在 Revit 中用原生撤销命令回退。

功能区“命令面板”提供当前桥接状态、最近操作和当前项目状态；点击“刷新项目状态”会提交只读 `health` 请求。“预览计划”不会修改模型，“确认执行”会再次要求确认；完成后用 Revit 原生 `Ctrl+Z` 撤销该事务。

## 一键识别客户端

安装器默认选择“自动识别并配置本机 AI 客户端”。它会扫描已知的 MCP 客户端配置位置，备份原文件后合并 Revit MCP Server；当前适配 Codex、WorkBuddy、Claude Desktop、Cursor、Windsurf、Cline 和 Roo Code。未识别的软件不会阻断安装，安装器始终生成标准 MCP JSON，支持 MCP 的新客户端可直接导入。

客户端识别只是安装层适配器，不进入 Revit 插件核心。以后支持新软件只增加一条配置适配规则，不需要重新设计 Revit 命令协议或建模功能。

## 接入不同客户端

安装器固定使用“自动识别并配置本机 AI 客户端”。它会生成并保存连接配置到安装目录的 `connections` 文件夹；未识别的客户端直接使用“复制 MCP”按钮提供的通用配置。详见 [CONNECTORS.md](./CONNECTORS.md)。

| 客户端能力 | 入口 | 适用场景 |
| --- | --- | --- |
| 支持 stdio MCP | scripts/revit-mcp-server.mjs | Codex、WorkBuddy 和其它 MCP Harness |
| 能调用 HTTP | scripts/revit-http-gateway.mjs | 任意 Function Calling Harness、后端服务、自动化平台 |
| 只有 OpenAI 兼容模型 API | scripts/revit-openai-compatible-chat.mjs | DeepSeek 及其它支持 Chat Completions + Tool Calling 的模型 |
| 能运行 PowerShell | scripts/send-revit-command.ps1 | Codex Shell、人工测试、批处理 |
| 只能读写文件 | %LOCALAPPDATA%\RevitCommandBridge\inbox/outbox | 自定义旧系统或最小 Harness |

### Codex MCP

优先在 Revit 功能区点击“复制 MCP”，再粘贴到客户端的 MCP 配置页。手工配置时，使用安装器内置的年份 Node 运行时（不要求另装 Node.js）：

~~~toml
[mcp_servers.revit]
command = "C:\\Users\\<用户名>\\AppData\\Local\\RevitCommandBridge\\2026\\runtime\\node.exe"
args = ["C:\\Users\\<用户名>\\AppData\\Local\\RevitCommandBridge\\2026\\scripts\\revit-mcp-server.mjs"]
~~~

重新启动 Codex 任务后，客户端可发现 `revit_execute_plan`。这是长期主入口；旧的 `revit_create_wall` 等工具仅为兼容已有脚本保留。

### 当前能力范围

| 模块 | 已实现的高频操作 |
| --- | --- |
| 查询与编辑 | 文档、目录、元素、参数、删除、选择与定位 |
| 建筑 | 标高、轴网、墙、楼板、墙洞口、模型线、房间、空间、DirectShape |
| 结构 | 梁、柱、斜撑，以及已载入结构族的实例放置 |
| MEP | 管道、风管、线管、桥架、直连/弯头/三通/活接连接 |
| 族 | 样板查询、新建 `.rfa`、参数、类型、box/cylinder/extrusion 几何、保存、载入、放置 |
| 放置方式 | 非宿主、宿主、面宿主、工作平面、视图、线基和自适应族 |
| 出图与注释 | 3D / 平面 / 天花 / 结构平面 / 绘图 / 剖面 / 立面 / 详图索引视图、复制与样板、图纸、视图/明细表放图纸、详图线、文字、尺寸、标签、填充区域、修订及修订云线 |
| 导出与交付 | PNG/JPG/TIFF/BMP 图像、DWG/DXF、IFC、明细表 CSV/TXT、保存 `.rvt`；导出/保存须作为独立计划执行 |

用于出图时，先以 `query_catalog(kind=view_types|title_blocks|text_types|filled_region_types|revisions)` 查询项目资源；需要尺寸或标签时，以 `query_references` 读取元素稳定引用，再提交 `create_dimension` 或 `create_tag`。`export` 和 `save_document` 有外部文件副作用，须分别放在只含一个步骤的 `execute_plan` 中。完整参数和覆盖边界见 [PROTOCOL.md](./PROTOCOL.md)。

“Revit 的所有功能”包含数千个 API 对象，桥接不会开放任意 C# 执行；新增能力统一以受控原子步骤加入 `execute_plan`。完整参数和覆盖边界见 [PROTOCOL.md](./PROTOCOL.md)。

### 通用 MCP JSON

使用 JSON MCP 配置的客户端采用相同进程参数：

~~~json
{
  "mcpServers": {
    "revit-command-bridge": {
      "command": "C:\\Users\\<用户名>\\AppData\\Local\\RevitCommandBridge\\2026\\runtime\\node.exe",
      "args": [
        "C:\\Users\\<用户名>\\AppData\\Local\\RevitCommandBridge\\2026\\scripts\\revit-mcp-server.mjs"
      ]
    }
  }
}
~~~

### REST 与通用 Function Calling Harness

启动仅监听本机的 REST 网关：

~~~powershell
node "$env:LOCALAPPDATA\RevitCommandBridge\2026\scripts\revit-http-gateway.mjs"
~~~

查询状态：

~~~powershell
Invoke-RestMethod 'http://127.0.0.1:8765/health'
~~~

提交并等待预览结果：

~~~powershell
$body = @{
  operation = 'execute_plan'
  args = @{
    steps = @(
      @{ id = 'check'; operation = 'query_document'; args = @{} }
      @{ id = 'support'; operation = 'create_direct_shape'; args = @{
        name = '测试支座'
        geometry = @(@{ kind = 'box'; min = @{ x = 0; y = 0; z = 0 }; max = @{ x = 3000; y = 2000; z = 2500 } })
      } }
    )
  }
  preview = $true
} | ConvertTo-Json -Depth 12

Invoke-RestMethod -Method Post -Uri 'http://127.0.0.1:8765/commands?wait_seconds=60' -ContentType 'application/json; charset=utf-8' -Body $body
~~~

远程模型 API 本身不直接访问本机 Revit；本机 Harness 把 Function Calling 参数转发到这个 REST 端点，或直接启动 MCP Server。需要使用模型 API 时，运行 `scripts/configure-ai-provider.ps1` 保存配置，再启动本机助手；API Key 使用 Windows DPAPI 按当前用户加密保存，不写入 MCP/REST 配置文件。

## 工作方式

~~~mermaid
flowchart LR
    A["Codex / WorkBuddy / 任意模型或 Harness"] --> B["MCP / REST / CLI"]
    B --> C["本地原子 JSON 队列"]
    C --> D["对应年份的 Revit 插件"]
    D --> E["ExternalEvent 主线程调度"]
    E --> F["受控 Revit API + Transaction"]
    F --> G["结果 JSON / Revit 模型"]
~~~

桥接层不执行任意 C#、Python 或自然语言。它只接受已注册的顶层 operation 和受控原子步骤，校验参数和目标文档后才调用 Revit API。`execute_plan` 中所有写步骤使用一个 all-or-nothing Revit Transaction。REST 默认只绑定 127.0.0.1，未检测到活动 Revit 桥时拒绝提交。

## 常见问题

| 现象 | 原因与处理 |
| --- | --- |
| REST 返回 503 bridge_not_running | Revit 未启动、插件未加载，或启动后尚未完成初始化 |
| 返回“尚未打开项目文档” | 在 Revit 中打开或新建 .rvt 项目后重试 |
| 返回文档标题不一致 | 请求指定了 document_title，但当前活动项目不是该文档 |
| 返回命令 ID 已存在 | 读取已有 outbox 结果，或生成新 id |
| Revit 功能区没有按钮 | 检查 %APPDATA%\Autodesk\Revit\Addins\<year>\RevitCommandBridge.addin |
| 其它年份 Revit 不加载 | 为对应年份重新引用 RevitAPI.dll / RevitAPIUI.dll 并编译适配包 |

完整请求、响应和操作参数见 [PROTOCOL.md](./PROTOCOL.md)；任意 Harness 可直接采用 [schemas/execute-plan.schema.json](./schemas/execute-plan.schema.json) 做 Function Calling / 请求校验；长期扩展原则与覆盖边界见 [ARCHITECTURE.md](./ARCHITECTURE.md)。更多常见问题见 [plans/FAQ.md](./plans/FAQ.md)。

## 原子操作一览

所有顶层 `operation` 和 `execute_plan` 中的 `steps[].operation` 均由以下注册表调度。

### 顶层操作（直接传入 `operation` 字段）

| 操作名称 | 功能 |
|---|---|
| `health` | 桥接健康检查，返回状态、文档信息 |
| `execute_plan` | **主入口**。执行多步骤建模/出图计划，写步骤合并为一个事务 |
| `new_project` | 创建新项目（可选 .rte 样板），可选保存为 .rvt |
| `create_family` | 从 .rft 样板创建 .rfa 族，支持参数/类型/几何 |
| `load_family` | 载入已有 .rfa 到当前项目 |
| `list_family_templates` | 列出本机 Revit 族样板路径 |
| `list_levels` | 列出项目标高（兼容旧入口） |
| `list_wall_types` | 列出基本墙类型（兼容旧入口） |
| `create_level` | 创建标高（兼容旧入口） |
| `create_grid` | 创建轴网（兼容旧入口） |
| `create_wall` | 创建直墙（兼容旧入口） |
| `create_rectangle_walls` | 创建四面闭合矩形墙（兼容旧入口） |

### execute_plan 原子步骤

#### 查询类

| 操作名称 | 功能 |
|---|---|
| `query_document` | 返回当前文档信息（标题、路径、活动视图等） |
| `query_catalog` | 项目资源目录：标高、类别、视图、图纸、明细表、族类型、MEP 类型、链接等 |
| `query_elements` | 按类别/名称/族名/ID 过滤查询元素及其参数 |
| `query_references` | 返回元素的稳定几何引用（面/边） |
| `query_parameters` | 列出单个元素的所有参数 |
| `query_geometry` | 返回元素包围盒、实体摘要或面信息 |
| `query_room` | 查询房间/空间，支持按点查找或全量列出 |
| `query_selection` | 读取 Revit 界面当前选中的图元 ID、名称、类别 |
| `query_mep_network` | 从种子元素遍历 MEP 连接拓扑 |
| `query_view_range` | 返回平面视图范围（顶/剖切面/底/视图深度） |

#### 创建类

| 操作名称 | 功能 |
|---|---|
| `create_level` | 创建标高 |
| `create_grid` | 创建轴网 |
| `create_wall` | 创建直墙，支持新墙类型克隆 |
| `create_floor` | 从闭合轮廓创建楼板 |
| `create_room` | 创建房间 |
| `create_space` | 创建 MEP 空间 |
| `create_model_curve` | 创建模型线 |
| `create_direct_shape` | 从几何图元（box/cylinder/sphere 等）创建 DirectShape |
| `create_swept_shape` | 沿路径扫掠创建实体（矩形/圆形/管道截面） |
| `create_mep_curve` | 创建 MEP 管线（管道/风管/线管/桥架） |
| `connect_mep` | 连接 MEP 元素，支持直连/弯头/三通/变径/四通 |
| `create_mep_system` | 创建管道或风管系统 |
| `create_insulation` | 添加管道/风管保温层 |
| `place_family_instance` | 放置族实例，支持多种放置方式 |
| `load_family` | 载入 .rfa 到项目 |
| `create_structural_member` | 创建结构构件（梁/斜撑/柱） |
| `create_view` | 创建 3D/平面/天花/结构平面视图 |
| `create_sheet` | 创建图纸（可选标题栏） |
| `place_view_on_sheet` | 将视图放置到图纸 |
| `create_opening` | 创建洞口（墙/楼板/竖井） |
| `create_drafting_view` | 创建绘图视图 |
| `create_section_view` | 创建剖面/详图视图 |
| `create_elevation_view` | 创建立面视图 |
| `create_callout` | 创建详图索引视图 |
| `duplicate_view` | 复制视图，可选应用视图样板 |
| `create_view_template` | 从现有视图创建视图样板 |
| `create_detail_curve` | 创建详图线 |
| `create_text_note` | 创建文字注释 |
| `create_dimension` | 创建尺寸标注 |
| `create_tag` | 创建独立标记 |
| `create_filled_region` | 创建填充区域 |
| `create_revision` | 创建修订 |
| `create_revision_cloud` | 创建修订云线 |
| `create_schedule` | 创建明细表（常规/材质提取/关键字/视图列表/图纸列表/修订） |
| `place_schedule_on_sheet` | 将明细表放置到图纸 |

#### 视图属性与覆盖

| 操作名称 | 功能 |
|---|---|
| `set_view_properties` | 设置视图属性（比例、裁剪框、样板、详细程度、规程等） |
| `set_element_overrides` | 设置元素图形覆盖（颜色、线宽、半色调等） |
| `set_category_overrides` | 设置类别图形覆盖 |
| `manage_view_filters` | 管理视图过滤器（添加/删除，支持规则与覆盖） |
| `set_view_range` | 设置平面视图范围（顶/剖切面/底/视图深度） |
| `manage_schedule_fields` | 管理明细表字段（添加/删除/隐藏/排序/过滤） |
| `manage_graphics_resources` | 管理图形资源（线样式子类别/填充图案） |

#### 编辑与变更

| 操作名称 | 功能 |
|---|---|
| `set_parameters` | 批量设置元素参数值 |
| `manage_schema_data` | 扩展数据读写与搬运 |
| `manage_family_parameters` | 编辑族参数（添加/重命名/删除/设公式） |
| `manage_project_parameters` | 管理项目参数 |
| `duplicate_type` | 复制 ElementType，可选覆盖参数 |
| `transform_elements` | 移动/复制/旋转/镜像元素 |
| `rename_element` | 重命名元素（单个或批量前缀模式） |
| `set_element_curve` | 修改线性元素的 LocationCurve |
| `delete_elements` | 删除元素 |
| `select_elements` | 选中并显示/缩放至元素 |

#### 外部操作（需单独执行，不能与其他步骤混合）

| 操作名称 | 功能 |
|---|---|
| `export` | 导出视图（PNG/JPG/DWG/DXF/IFC/明细表 CSV） |
| `save_document` | 保存当前文档 |

> 全部操作共约 65 个原子步骤。新增能力以受控原子步骤加入此表，不开放任意 C# 执行。完整参数定义见 [PROTOCOL.md](./PROTOCOL.md) 和 [schemas/execute-plan.schema.json](./schemas/execute-plan.schema.json)。
