# Revit Command Bridge

面向 Revit 的本地命令桥。Revit 插件只执行受控 Revit API 命令；Codex、WorkBuddy、任意 MCP 客户端、任意 Function Calling Harness 或 OpenAI 兼容模型 API，通过统一 JSON、CLI、REST 或 MCP 接口调用。它不依赖 Dynamo，也不绑定模型厂商。新建模统一走 `execute_plan`：一个计划可组合建筑、结构、机电、空间、出图、参数和选中显示，而不是为每一种构件增加一个插件命令。

> 每个 Revit 年份必须使用对应 API 编译出的 DLL，不能共用一个"万能 DLL"。本交付包支持 Revit 2020–2024；版本边界见 [VERSION-SUPPORT.md](./VERSION-SUPPORT.md)。

单文件安装器会自动扫描本机 Revit 2020–2024。检测到内置适配包时直接使用；没有对应预编译包时，安装器使用该电脑对应年份的 Revit API 自动生成匹配 DLL。普通用户不需要选择 DLL、安装 Visual Studio 或手工填写 Revit 路径。每个年份仍需在装有对应 Revit 的机器上完成加载和建模回归。

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
├── build/                             ← 版本矩阵 & 编译属性
│   └── version-manifest.json
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
├── build-revit-adapter.ps1
├── install-revit.ps1                  ← 安装/检测
├── install-revit2020.ps1
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

## 快速开始

先查看可安装的 Revit：

~~~powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\install-revit.ps1 -ListDetected
~~~

普通用户直接运行成品包中的 `RevitCommandBridgeSetup.exe`。开发者关闭目标 Revit 后，可构建并安装指定版本：

~~~powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1 -RevitVersion 2020
powershell -NoProfile -ExecutionPolicy Bypass -File .\install-revit.ps1 -RevitVersion 2020
~~~

启动中文 Revit 2020：

~~~powershell
& 'C:\Program Files\Autodesk\Revit 2020\Revit.exe' /language CHS
~~~

打开 Revit 后，桥接会自动启动；功能区"Revit 命令桥"的"启动桥接"按钮可用于确认连接信息。随后执行一次只读健康检查：

~~~powershell
& "$env:LOCALAPPDATA\RevitCommandBridge\2020\scripts\send-revit-command.ps1" -RequestPath "$env:LOCALAPPDATA\RevitCommandBridge\2020\examples\health.json"
~~~

预览一个通用建模计划，不修改模型：

~~~powershell
& "$env:LOCALAPPDATA\RevitCommandBridge\2020\scripts\send-revit-command.ps1" -RequestPath "$env:LOCALAPPDATA\RevitCommandBridge\2020\examples\preview-universal-plan.json"
~~~

确认预览返回正确后，把请求中的 preview 改为 false 再提交。实际写入使用 Revit Transaction，可在 Revit 中用原生撤销命令回退。

功能区"命令面板"提供当前桥接状态、最近操作和当前项目状态；点击"刷新项目状态"会提交只读 `health` 请求。"预览计划"不会修改模型，"确认执行"会再次要求确认；完成后用 Revit 原生 `Ctrl+Z` 撤销该事务。

## 一键识别客户端

安装器默认选择"自动识别并配置本机 AI 客户端"。它会扫描已知的 MCP 客户端配置位置，备份原文件后合并 Revit MCP Server；当前适配 Codex、WorkBuddy、Claude Desktop、Cursor、Windsurf、Cline 和 Roo Code。未识别的软件不会阻断安装，安装器始终生成标准 MCP JSON，支持 MCP 的新客户端可直接导入。

客户端识别只是安装层适配器，不进入 Revit 插件核心。以后支持新软件只增加一条配置适配规则，不需要重新设计 Revit 命令协议或建模功能。

## 接入不同客户端

安装器固定使用"自动识别并配置本机 AI 客户端"。它会生成并保存连接配置到安装目录的 `connections` 文件夹；未识别的客户端直接使用"复制 MCP"按钮提供的通用配置。详见 [CONNECTORS.md](./CONNECTORS.md)。

| 客户端能力 | 入口 | 适用场景 |
| --- | --- | --- |
| 支持 stdio MCP | scripts/revit-mcp-server.mjs | Codex、WorkBuddy 和其它 MCP Harness |
| 能调用 HTTP | scripts/revit-http-gateway.mjs | 任意 Function Calling Harness、后端服务、自动化平台 |
| 只有 OpenAI 兼容模型 API | scripts/revit-openai-compatible-chat.mjs | DeepSeek 及其它支持 Chat Completions + Tool Calling 的模型 |
| 能运行 PowerShell | scripts/send-revit-command.ps1 | Codex Shell、人工测试、批处理 |
| 只能读写文件 | %LOCALAPPDATA%\RevitCommandBridge\inbox/outbox | 自定义旧系统或最小 Harness |

### Codex MCP

优先在 Revit 功能区点击"复制 MCP"，再粘贴到客户端的 MCP 配置页。手工配置时，使用安装器内置的年份 Node 运行时（不要求另装 Node.js）：

~~~toml
[mcp_servers.revit]
command = "C:\\Users\\<用户名>\\AppData\\Local\\RevitCommandBridge\\2020\\runtime\\node.exe"
args = ["C:\\Users\\<用户名>\\AppData\\Local\\RevitCommandBridge\\2020\\scripts\\revit-mcp-server.mjs"]
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

"Revit 的所有功能"包含数千个 API 对象，桥接不会开放任意 C# 执行；新增能力统一以受控原子步骤加入 `execute_plan`。完整参数和覆盖边界见 [PROTOCOL.md](./PROTOCOL.md)。

### 通用 MCP JSON

使用 JSON MCP 配置的客户端采用相同进程参数：

~~~json
{
  "mcpServers": {
    "revit": {
      "command": "C:\\Users\\<用户名>\\AppData\\Local\\RevitCommandBridge\\2020\\runtime\\node.exe",
      "args": [
        "C:\\Users\\<用户名>\\AppData\\Local\\RevitCommandBridge\\2020\\scripts\\revit-mcp-server.mjs"
      ]
    }
  }
}
~~~

### REST 与通用 Function Calling Harness

启动仅监听本机的 REST 网关：

~~~powershell
node "$env:LOCALAPPDATA\RevitCommandBridge\2020\scripts\revit-http-gateway.mjs"
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
| 返回"尚未打开项目文档" | 在 Revit 中打开或新建 .rvt 项目后重试 |
| 返回文档标题不一致 | 请求指定了 document_title，但当前活动项目不是该文档 |
| 返回命令 ID 已存在 | 读取已有 outbox 结果，或生成新 id |
| Revit 功能区没有按钮 | 检查 %APPDATA%\Autodesk\Revit\Addins\<year>\RevitCommandBridge.addin |
| 其它年份 Revit 不加载 | 为对应年份重新引用 RevitAPI.dll / RevitAPIUI.dll 并编译适配包 |

完整请求、响应和操作参数见 [PROTOCOL.md](./PROTOCOL.md)；任意 Harness 可直接采用 [schemas/execute-plan.schema.json](./schemas/execute-plan.schema.json) 做 Function Calling / 请求校验；长期扩展原则与覆盖边界见 [ARCHITECTURE.md](./ARCHITECTURE.md)。更多常见问题见 [plans/FAQ.md](./plans/FAQ.md)。
