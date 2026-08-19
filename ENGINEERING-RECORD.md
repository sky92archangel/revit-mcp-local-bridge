# 通用执行底座工程记录

## 范围

将现有 Revit 2020 单命令桥改为跨 Agent 的 `execute_plan` 执行底座；不安装或重启正在运行的 Revit，不修改当前未保存项目。

## 证据

| ID | 结论 | 证据 |
| --- | --- | --- |
| E1 | 原桥接已经通过文件队列、`ExternalEvent` 回到 Revit 主线程 | [src/BridgeRuntime.cs](./src/BridgeRuntime.cs)、[src/BridgeFileQueue.cs](./src/BridgeFileQueue.cs) |
| E2 | 新计划引擎采用单个 all-or-nothing Revit Transaction | [src/PlanCommandExecutor.cs](./src/PlanCommandExecutor.cs) |
| E3 | 专用桁架源码已移除，改用通用 `create_direct_shape` 原语 | `src/TrussCommandExecutor.cs` 已删除；[src/RevitGeometryFactory.cs](./src/RevitGeometryFactory.cs) |
| E4 | 新 DLL 按本机 Revit 2020 API 编译成功 | `powershell -ExecutionPolicy Bypass -File .\build.ps1` 输出；[dist/RevitCommandBridge-2020/RevitCommandBridge.dll](./dist/RevitCommandBridge-2020/RevitCommandBridge.dll) |
| E5 | Node 脚本语法、JSON 示例、MCP `initialize` / `tools/list` 已实际验证 | `node --check scripts/*.mjs`；MCP 输出包含 `revit_execute_plan` |
| E6 | 计划解析可识别写计划并拒绝重复步骤 ID | 反射调用 `BridgeJson.ParseRequest` + `PlanCommandExecutor.IsWritePlan` 的实际输出 |
| E7 | 运行队列、健康信息和 MCP 版本已改为按编译目标年份隔离 | [src/BridgeBuildInfo.cs](./src/BridgeBuildInfo.cs)、[src/BridgeFileQueue.cs](./src/BridgeFileQueue.cs)、[scripts/bridge-client.mjs](./scripts/bridge-client.mjs) |
| E8 | 通用安装器能发现本机 Revit 2020，并以 `-WhatIf` 预览年版本安装目标 | `powershell -File .\install-revit.ps1 -ListDetected` 与打包后 `install-revit.ps1 -WhatIf` 实际输出 |
| E9 | Codex、WorkBuddy、DeepSeek、通用 MCP、REST 的连接配置都可生成，JSON 已解析验证 | [scripts/configure-connector.ps1](./scripts/configure-connector.ps1)；`build/connector-profile-test` 生成文件 |
| E10 | Revit 2024 REST 默认端口可在本机启动并返回协议健康状态 | `node` 导入 [scripts/revit-http-gateway.mjs](./scripts/revit-http-gateway.mjs)，`http://127.0.0.1:8769/health` 返回 `revit-command-bridge/2.0` |
| E11 | Revit 命令面板增加状态、项目刷新、预览和二次确认执行入口 | [src/CommandPanelForm.cs](./src/CommandPanelForm.cs)、本机 Revit 2020 API 编译成功 |
| E12 | Revit 2020 真机已完成族创建/载入/放置、楼板、模型线、3D 视图、图纸及视图放图纸回归 | [verification/2026-08-19-regression.md](./verification/2026-08-19-regression.md) |
| E13 | 常用出图扩展已通过 Revit 2020 API 编译、JSON/schema 解析和 MCP tools/list 回归 | [src/RevitOutputOperations.cs](./src/RevitOutputOperations.cs)、[schemas/execute-plan.schema.json](./schemas/execute-plan.schema.json)、[verification/2026-08-19-regression.md](./verification/2026-08-19-regression.md) |

## 开源对照

| ID | 项目 | 取用/未取用 | 版本证据 |
| --- | --- | --- | --- |
| G1 | [mcp-servers-for-revit/revit-mcp](https://github.com/mcp-servers-for-revit/revit-mcp) | 参考工具注册与线/点构件抽象；不直接复用其“一个工具一个处理器”结构 | MIT，`c9ef49e4c397298d291304f822b89ba3a102e6bf` |
| G2 | [mcp-servers-for-revit/revit-mcp-plugin](https://github.com/mcp-servers-for-revit/revit-mcp-plugin) | 参考 ExternalEvent / 命令注册思路；不使用其直接代码执行入口 | MIT，`80085027e3770cd0d7e038daa6637e92769c7573` |

## 覆盖矩阵

| 状态 | 覆盖项 | 验证方式 |
| --- | --- | --- |
| [V] | 计划解析、步骤唯一性、受控操作白名单 | E6 |
| [V] | C# 以本机 RevitAPI.dll 编译 | E4 |
| [V] | CLI/REST/MCP 使用同一 JSON 外层协议 | [scripts/bridge-client.mjs](./scripts/bridge-client.mjs)、E5 |
| [V] | DirectShape、MEP、族、结构、参数等 API 调用在编译期可解析 | E4 |
| [V] | 常用出图协议：视图、图纸、注释、明细表、修订、导出和保存操作均已注册并通过 Revit 2020 API 编译 | E13 |
| [V] | 新操作与 JSON schema 白名单一致，MCP 0.5.0 暴露 `revit_execute_plan` | E13 |
| [V] | 2020 年份隔离队列、安装预览、连接配置和 Revit 面板代码可编译/解析 | E7、E8、E9、E11 |
| [V] | 2024 REST 年份端口计算和 HTTP 健康端点 | E10 |
| [T] | 新 DLL 在已打开 Revit 项目的真实 `preview` | 需保存项目、关闭 Revit、安装新包、重启后执行示例 |
| [T] | 真机创建 pipe/duct/conduit/cable tray、族和 MEP fitting | 需在含对应类型/系统的项目中逐项预览和执行 |
| [T] | 新增出图扩展真实 Revit 回归 | 用户本轮要求不做真机测试；需在含图框、标签、文字、修订资源的项目中逐项预览和执行 |
| [T] | Revit 2021–2024 适配包 | 构建路由已实现；仍需要该年份 Revit API DLL 与独立构建/真机验证 |
| [T] | Revit 2025–2026 适配包 | 需完成 .NET 8 适配构建和真机验证 |

## 已知待办

| 优先级 | 项目 | 下一步 |
| --- | --- | --- |
| P0 | 新出图功能真机验证 | 在典型项目中预览/执行 `preview-output-documentation-plan.json`，并单独验证 `preview-export-image.json` |
| P1 | 宿主/面基/工作平面族 | 增加通用 host、face、work-plane 放置原子参数 |
| P1 | 自动 MEP 路径和避障 | 作为上层规划器能力，生成多个 `create_mep_curve` + `connect_mep` 步骤 |
| P2 | Revit 2021–2024 适配 | 为每个目标年份提供 Revit API、生成对应 DLL 并建立真机兼容测试矩阵 |
| P2 | Revit 2025–2026 .NET 8 适配 | 将宿主层迁移到 .NET 8，同时保持 `execute_plan` 协议不变 |
