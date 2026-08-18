# Revit Command Bridge

面向 Revit 的本地命令桥。Revit 插件只执行受控 Revit API 命令；Codex、WorkBuddy、任意 MCP 客户端、任意 Function Calling Harness 或 OpenAI 兼容模型 API，通过统一 JSON、CLI、REST 或 MCP 接口调用。它不依赖 Dynamo，也不绑定模型厂商。新建模统一走 `execute_plan`：一个计划可组合建筑、结构、机电、参数和选中显示，而不是为每一种构件增加一个插件命令。

> 独立第三方项目，未获 Autodesk/Revit 认可或关联；仓库不分发 Revit 软件、Revit API DLL 或模型文件。使用者应自行安装并按许可使用 Revit。

## 交流

- QQ 交流群：`1102212354`
- 问题与功能建议：请提交仓库 Issue。

> 每个 Revit 年份必须使用对应 API 编译出的 DLL，不能共用一个“万能 DLL”。当前机器只安装了 Revit 2020；2020 包已构建，安装和真机回归仍待执行。版本边界见 [VERSION-SUPPORT.md](./VERSION-SUPPORT.md)。

单文件安装器会自动扫描本机 Revit 2020–2024。检测到内置适配包时直接使用；没有对应预编译包时，安装器使用该电脑对应年份的 Revit API 自动生成匹配 DLL。普通用户不需要选择 DLL、安装 Visual Studio 或手工填写 Revit 路径。2021–2024 的自动适配路线已实现，但仍需分别在装有对应 Revit 的机器上完成加载和建模回归。

## 快速开始

先查看可安装的 Revit：

~~~powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\install-revit.ps1 -ListDetected
~~~

关闭目标 Revit，然后构建并安装指定版本：

~~~powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1 -RevitVersion 2020
powershell -NoProfile -ExecutionPolicy Bypass -File .\install-revit.ps1 -RevitVersion 2020 -Connector codex
~~~

启动中文 Revit 2020：

~~~powershell
& '<REVIT_INSTALL_DIRECTORY>\Revit.exe' /language CHS
~~~

打开 Revit 后，桥接会自动启动；功能区“Revit 命令桥”的“启动桥接”按钮可用于确认连接信息。随后执行一次只读健康检查：

~~~powershell
& "$env:LOCALAPPDATA\RevitCommandBridge\2020\scripts\send-revit-command.ps1" -RequestPath "$env:LOCALAPPDATA\RevitCommandBridge\2020\examples\health.json"
~~~

预览一个通用建模计划，不修改模型：

~~~powershell
& "$env:LOCALAPPDATA\RevitCommandBridge\2020\scripts\send-revit-command.ps1" -RequestPath "$env:LOCALAPPDATA\RevitCommandBridge\2020\examples\preview-universal-plan.json"
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

本机 Codex 的 config.toml 可加入：

~~~toml
[mcp_servers.revit]
command = "C:\\Program Files\\nodejs\\node.exe"
args = ["<USER_LOCALAPPDATA>\\RevitCommandBridge\\2020\\scripts\\revit-mcp-server.mjs"]
~~~

重新启动 Codex 任务后，客户端可发现 `revit_execute_plan`。这是长期主入口；旧的 `revit_create_wall` 等工具仅为兼容已有脚本保留。

### 通用 MCP JSON

使用 JSON MCP 配置的客户端采用相同进程参数：

~~~json
{
  "mcpServers": {
    "revit": {
      "command": "C:\\Program Files\\nodejs\\node.exe",
      "args": [
        "<USER_LOCALAPPDATA>\\RevitCommandBridge\\2020\\scripts\\revit-mcp-server.mjs"
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

远程模型 API 本身不直接访问本机 Revit；本机 Harness 把 Function Calling 参数转发到这个 REST 端点，或直接启动 MCP Server。自动识别未覆盖的 OpenAI 兼容客户端可直接使用随安装包提供的本机 Harness；API Key 使用 Windows DPAPI 按当前用户加密保存，不写入 MCP/REST 配置文件。

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

完整请求、响应和操作参数见 [PROTOCOL.md](./PROTOCOL.md)；任意 Harness 可直接采用 [schemas/execute-plan.schema.json](./schemas/execute-plan.schema.json) 做 Function Calling / 请求校验；长期扩展原则与覆盖边界见 [ARCHITECTURE.md](./ARCHITECTURE.md)。
