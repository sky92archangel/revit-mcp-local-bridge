# 原子操作扩展计划

本文档是 `execute_plan` 白名单原子操作的能力扩展计划：现状评估 → 缺口分析 → 优先级路线图 → 扩展流程与验收标准。目标读者是需要在插件端（C#）新增原子操作的开发者。

背景：桥接当前开放约 40 个原子操作（见 [`PlanCommandExecutor.AtomicOperations`](./src/PlanCommandExecutor.cs)）。协议与扩展原则见 [ARCHITECTURE.md](./ARCHITECTURE.md)；完整操作参数见 [PROTOCOL.md](./PROTOCOL.md)。

## 1. 现状评估结论

| 工作深度 | 是否够用 | 说明 |
| --- | --- | --- |
| 方案 / 占位建模 | 够用 | 标高、轴网、墙板、DirectShape、族放置全覆盖 |
| 批量重复工作 | 够用（强项） | "24 根管道 + 阀门 + 连接 + 编号 + 选中复核"是一个计划 |
| 参数写入与复核 | 够用 | `query_*` 四件套 + `set_parameters`（支持 BuiltInParameter） |
| 出图交付 | 基本够用 | 视图 / 图纸 / 明细表 / 标注 / 导出链路完整 |
| MEP 初步设计 | 勉强够用 | 直管 + 弯头 / 三通 / 活接可组合出大部分管网 |
| 施工图深化 / 管综 | 不够 | 存在明确缺口（见下节），需按本计划补齐 |

## 2. 缺口分析（管线方向优先）

| 缺口 | 影响 |
| --- | --- |
| `create_opening` 只支持墙洞口 | 管线穿楼板 / 梁的套管预留无法表达 |
| `connect_mep` 无 reducer（变径）、cross（四通） | 变径只能靠 `auto` 碰运气，异径三通 / 四通连接不可控 |
| 无 move / copy / rotate / mirror 修改类操作 | 调整既有管线位置只能"删除重建" |
| 无碰撞检查 | Agent 无法程序化发现碰撞，自动避障闭环缺一环 |
| 无 MEP System 对象管理 | 只能写参数，不能创建系统实体、指派立管 |
| 无保温层 | 深化阶段需求 |
| 幕墙 / 楼梯 / 栏杆 / 嵌套族 / 钢筋 | ARCHITECTURE.md 已标注为 [T]，按需排期 |

## 3. 优先级路线图

选择标准与现有架构一致：优先"高频 + 可组合 + 参数简单"的原子；"决策型"能力（自动布线、避障路径、管综规则）留给上层 Agent，用 `query_*` 感知 + 原子执行循环实现，不进入桥接。

### P0（第一批，热身闭环）

| 新操作 | 对应 Revit API 入口 | 事务类别 | 难度 |
| --- | --- | --- | --- |
| `create_opening` 扩展：楼板竖直洞口 / 竖井 | `Document.Create.NewVerticalOpening`、`NewShaftOpening` | 写 | 低 |
| `connect_mep` 扩展：`reducer`、`cross` | `Document.Create.NewTransitionFitting`、`NewCrossFitting`（与现有 `NewElbowFitting` 同族） | 写 | 低 |
| `transform_elements`（move/copy/rotate/mirror 四种 mode） | `ElementTransformUtils.MoveElements / CopyElements / RotateElements / MirrorElements` | 写 | 低 |

### P1（第二批，感知与系统）

| 新操作 | 对应 Revit API 入口 | 事务类别 | 难度 |
| --- | --- | --- | --- |
| `check_interferences`（碰撞检查） | `InterferenceChecker.FindInterferences(ElementSet, ElementSet)` | 只读 | 中 |
| `create_mep_system`（创建 / 指派系统） | `PipingSystem.Create`、`MechanicalSystem.Create` + `Add` | 写 | 中 |
| `create_mep_curve` 增加 `slope` 参数 | 无需新 API：按坡度换算终点 Z，纯参数增强 | 写 | 低 |

### P2（第三批，深化表现）

| 新操作 | 对应 Revit API 入口 | 事务类别 | 难度 |
| --- | --- | --- | --- |
| `create_insulation`（保温层） | `PipeInsulation.Create`、`DuctInsulation.Create` | 写 | 低 |
| `set_element_overrides`（图元图形替换） | `View.SetElementOverrides` | 写 | 中 |
| 视图过滤器 | `ParameterFilterElement.Create` + `View.AddFilter` | 写 | 中 |

### P3（按需，复杂对象）

| 领域 | 对应 Revit API 入口 | 难度 |
| --- | --- | --- |
| 楼梯 / 栏杆 | `StairsEditScope`、`Railing.Create` | 高 |
| 幕墙 | `CurtainSystem` 系列 | 高 |
| 钢筋（结构深化） | `Autodesk.Revit.DB.Structure.Rebar.Create` | 高 |

## 4. 新增一个原子操作的固定流程（6 处落点）

以 `transform_elements` 为例：

1. **登记白名单**：在 `src/PlanCommandExecutor.cs` 的 `AtomicOperations` 数组加名字。
2. **声明事务类别**：写模型操作加入同文件 `WriteOperations` 集合（自动获得 all-or-nothing 事务与 preview 状态）；只读感知型（如 `check_interferences`）两个集合都不进；有外部文件副作用的进 `ExternalOperations`（强制单独成计划）。
3. **注册分发**：在 `src/RevitPlanOperations.cs` 的 `Execute` switch 加 case。
4. **写实现**：按类别放入对应文件——修改类 `src/RevitPlanMutations.cs`、创建类 `src/RevitPlanCreations.cs`、查询类 `src/RevitPlanQueries.cs`、出图类 `src/RevitOutputOperations.cs`。实现时复用现成设施：
   - `context.ResolveElementIds()` 解析 `"$步骤ID"` 引用；
   - `PlanValues` / `BridgeArguments` 取参；
   - `FeetPerMillimeter`（`src/RevitCommandExecutor.cs`）做 mm → feet 换算；
   - 骨架照抄 `CreateMepCurve` / `ConnectMep`。
5. **更新契约**：`schemas/execute-plan.schema.json` 的 operation enum 加名字；可选在 `NormalizeAtomicOperation()` 加中文别名（如 `"移动元素"`）。
6. **文档 + 编译**：[PROTOCOL.md](./PROTOCOL.md) 操作表加一行；`build.ps1 -RevitVersion <year>` 按年份重编译。MCP 端零改动——`steps[].operation` 在 MCP schema 中是自由字符串，守门员是插件白名单。

## 5. 注意事项

1. **API 年份差异**：每个 Revit 年份单独编译，新调用的 API 必须存在于最老支持版本（2020）的 RevitAPI.dll；跨年份差异在适配构建时逐一核对。
2. **预览语义**：写操作在 `preview=true` 时不真正执行（`PlanCommandExecutor.Execute` 分流），实现方法在 preview 下返回"将要做什么"，参考现有创建类操作。
3. **回归记录**：新操作完成真机验证后，按 `verification/` 目录惯例补一份回归记录，保持 `[V]` / `[T]` 证据文化。
4. **命名规范**：操作名用小写下划线（`transform_elements`）；查询用 `query_` / `check_` 前缀，创建用 `create_`，修改用 `set_` / `transform_` / `delete_`，与现有命名一致。

## 6. 验收标准

每个新操作合入前须满足：

- [ ] 白名单、事务类别、分发 case、实现、schema、PROTOCOL.md 六处同步更新；
- [ ] `preview=true` 返回计划描述且不修改模型；
- [ ] `preview=false` 真机执行成功，失败时整个计划回滚（写操作）；
- [ ] `"$步骤ID"` 引用可用（涉及元素目标的操作）；
- [ ] 长度参数支持裸数（mm）与带单位字符串（如 `"3.6m"`）；
- [ ] 在 Revit 2020 真机完成回归并记录到 `verification/`。
