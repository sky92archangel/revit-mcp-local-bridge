# 原子操作扩展计划

本文档是 `execute_plan` 白名单原子操作的能力扩展计划：现状评估 → 缺口分析 → 优先级路线图 → 参考实现资产 → 扩展流程与验收标准。目标读者是需要在插件端（C#）新增原子操作的开发者。

背景：桥接当前开放约 40 个原子操作（见 [`PlanCommandExecutor.AtomicOperations`](../src/PlanCommandExecutor.cs)）。协议与扩展原则见 [ARCHITECTURE.md](../ARCHITECTURE.md)；完整操作参数见 [PROTOCOL.md](../PROTOCOL.md)。

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
| 无碰撞检查 | Agent 无法程序化发现碰撞（含链接模型间的管综碰撞），自动避障闭环缺一环 |
| 无 MEP System 对象管理与管网拓扑查询 | 只能写参数，不能创建系统实体；Agent 无法感知管网连通关系 |
| 无保温层 | 深化阶段需求 |
| 事务缺少失败预处理器 | 无人值守执行时 Revit 模态警告可能阻塞队列 |
| 参数定义层空白：无法枚举元素全部参数、无法追加参数或重命名参数 / 元素名 | `set_parameters` / `query_elements.parameters` 只能按**已知**参数名读写值，Agent 无法"发现"元素有哪些属性；类型名、族名、参数定义均不可增改（唯一例外：`create_family` 从零建族时可定义参数） |
| 幕墙 / 楼梯 / 栏杆 / 嵌套族 / 钢筋 | ARCHITECTURE.md 已标注为 [T]，按需排期 |

## 3. 优先级路线图

选择标准与现有架构一致：优先"高频 + 可组合 + 参数简单"的原子；"决策型"能力（自动布线、避障路径、管综规则）留给上层 Agent，用 `query_*` 感知 + 原子执行循环实现，不进入桥接。

### P0（第一批，热身闭环）

| 新操作 | 对应 Revit API 入口 | 事务类别 | 难度 |
| --- | --- | --- | --- |
| `create_opening` 扩展：楼板竖直洞口 / 竖井 | `Document.Create.NewVerticalOpening`、`NewShaftOpening` | 写 | 低 |
| `connect_mep` 扩展：`reducer`、`cross` 配件 | `Document.Create.NewTransitionFitting`、`NewCrossFitting`（与现有 `NewElbowFitting` 同族） | 写 | 低 |
| `transform_elements`（move/copy/rotate/mirror 四种 mode） | `ElementTransformUtils.MoveElements / CopyElements / RotateElements / MirrorElements` | 写 | 低 |
| 基础设施：计划事务挂接失败预处理器 | `transaction.GetFailureHandlingOptions().SetFailuresPreprocessor(...)`；警告自动消除、错误文本写入结果 JSON | 基础设施 | 低 |

### P1（第二批，感知与系统）

| 新操作 | 对应 Revit API 入口 | 事务类别 | 难度 |
| --- | --- | --- | --- |
| `check_interferences`（碰撞检查，支持当前文档；链接模型碰撞用几何求交兜底） | `InterferenceChecker.FindInterferences`；跨链接文档用 `ElementIntersectsElementFilter` / 实体求交 | 只读 | 中 |
| `create_mep_system`（创建 / 指派系统） | `PipingSystem.Create`、`MechanicalSystem.Create` + `Add` | 写 | 中 |
| `create_mep_curve` 增加 `slope` 参数 | 无需新 API：按坡度换算终点 Z，纯参数增强 | 写 | 低 |
| `query_catalog(kind=links)`（链接模型清单） | `FilteredElementCollector` + `RevitLinkInstance.GetLinkDocument()` | 只读 | 低 |
| `query_parameters`（枚举元素 / 类型的全部参数：名称、值、单位、存储类型、只读标志） | `Element.Parameters` 遍历，逐项复用现成的 `RevitLookups.ParameterData` | 只读 | 低 |
| `rename_element`（重命名元素 / 类型 / 视图 / 标高等 `Element.Name`） | `Element.Name` 属性赋值 | 写 | 低 |

### P2（第三批，深化表现与拓扑）

| 新操作 | 对应 Revit API 入口 | 事务类别 | 难度 |
| --- | --- | --- | --- |
| `create_insulation`（保温层） | `PipeInsulation.Create`、`DuctInsulation.Create` | 写 | 低 |
| `query_mep_network`（管网连通拓扑：沿 `Connector.AllRefs` 遍历同系统管线与配件） | `MEPCurve.ConnectorManager` + `Connector.AllRefs` | 只读 | 中 |
| `set_element_overrides`（图元图形替换） | `View.SetElementOverrides` | 写 | 中 |
| 视图过滤器 | `ParameterFilterElement.Create` + `View.AddFilter` | 写 | 中 |
| `manage_family_parameters`（对**已有**族追加 / 重命名 / 删除参数定义） | `Document.EditFamily(family)` → 族文档 `FamilyManager.AddParameter / RenameParameter / RemoveParameter` → `LoadFamily` 回写 | 写（跨文档事务） | 高 |

### P3（按需，复杂对象）

| 领域 | 对应 Revit API 入口 | 难度 |
| --- | --- | --- |
| 楼梯 / 栏杆 | `StairsEditScope`、`Railing.Create` | 高 |
| 幕墙 | `CurtainSystem` 系列 | 高 |
| 钢筋（结构深化） | `Autodesk.Revit.DB.Structure.Rebar.Create` | 高 |
| 共享参数绑定 | 共享参数文件 + `Category.GetCategory(...).BoundParameters` | 中 |

## 4. 参考实现资产：sepd-revit-extension 共享库

已分析 `R:\_CODE_\REVIT\sepd-revit-extension\Common.Revit.Extension.Shared\`（作者署名 Haotian Zhou 周昊天）。该库是生产验证过的 Revit 扩展方法集，以下资产可直接作为路线图项的参考实现（模式参考，非直接复制，见 4.3 版权说明）。

该库全部 46 个文件、约 300 个 public 用法的"原子 vs 可组合"逐项分类见 [SEPD-ATOMIC-ANALYSIS.md](./SEPD-ATOMIC-ANALYSIS.md)（结论：18 项需原子化——其中 5 项已在下方路线图、13 项为新提议；其余约 88% 可组合或不适用）。

### 4.1 资产 → 路线图项映射

| 库文件 | 可复用内容 | 服务的路线图项 |
| --- | --- | --- |
| `ConnectorExtension.cs` | 连接件最近配对：`GetNearConnectors`（两组 ConnectorSet 最近点对）、`GetNearConnector`、`CloseConnectorToPoint`；`GetRefsByConnector`（找对端连接件）；`GetConnectorByDescription` | P0 `connect_mep` reducer/cross——多口配件必须先解决"哪两个口相连"的匹配问题 |
| `MEPCurveExtension.cs` | `ConnectMEPCurveElbowFitting`：两管延伸求交 → 裁剪 `LocationCurve` → `Regenerate()` → 交点处找端连接件 → `NewElbowFitting`；`ConnectMEPCurveTeeFitting`（双管 / 三管两版）：最近连接件搜索 → `NewTeeFitting` | P0 reducer/cross 的完整实现范式；可为 `connect_mep` 增加 `extend_to_intersection` 可选项 |
| `ConduitExtension.cs` | 线管版弯头 / 三通（与 MEPCurve 版同构）；`SelectSystemByConduit`：沿 `Connector.AllRefs` 遍历管网找同系统管线 | P2 `query_mep_network` 的遍历骨架 |
| `FailureProcessor.cs` | `ContinueFailureProcessor`（`IFailuresPreprocessor`）：Error 自动尝试 `ResolveFailure`、Warning 直接 `DeleteWarning`；`MyFailuresPreProcessor`：按错误文本匹配指定解决方案 | P0 失败预处理器——无人值守队列不被模态警告卡死的关键 |
| `DocumentExtension.cs` | `GetLinkDocs` / `GetRevitLinkInstances`（按关键字过滤链接模型） | P1 `query_catalog(kind=links)`；`check_interferences` 的跨模型碰撞前置 |
| `GeometryExtension.cs` | `GetSolidByElement` / `GetFaceByElement`（含 `GeometryInstance` 展开） | `check_interferences` 跨链接文档的几何求交兜底；未来几何查询 |
| `FloorExtension.cs` | `GetFloorBoundaryPolygon`：几何法提取楼板最低面边界（含洞口环）；`FloorGeoTH` 楼板顶标高 | 楼板洞口 / 竖井放置校验；读取既有楼板开洞 |
| `FilterRuleExtension.cs` | `HasFilterWithName` / `GetFilterByName`（视图与文档两级过滤器查找） | P2 视图过滤器项 |
| `UnitConverter.cs` | mm/m ↔ ft 全套换算（含 `XYZ`/`UV`、面积、体积、角度） | 单位换算参考（注意版本，见 4.2） |
| `TransformExtension.cs` | `LocalToWorldBy` / `WorldToLocalBy`（族实例局部 ↔ 世界坐标，Z 轴旋转）；最小欧拉旋转矩阵 | `transform_elements` 的坐标换算辅助 |
| `CommonExternalEventHandler.cs` | 单例 `Action<UIApplication>` 队列式外部事件 | 参考；桥接已有队列驱动版 `BridgeEventHandler`，无需替换 |
| `ParamterExtension.cs` / `SharedParameterExtention.cs` / `SchemaExtension.cs` / `FamilyExtension.cs` / `ViewExtension.cs` / `XyzExtension.cs` / `CurveExtension.cs` 等 | 参数读写、共享参数绑定、Extensible Storage、族 / 视图 / 几何大库 | P2 `manage_family_parameters`（`ParamterExtension` / `FamilyExtension`）与 P3 共享参数绑定（`SharedParameterExtention`）实现时深入取用；其余对应项按需 |

### 4.2 版本兼容警示（重要）

| 事项 | 说明 |
| --- | --- |
| 目标版本不同 | 共享库面向 **Revit 2024–2026**（`UnitConverter.cs` 头部注明"仅支持 Revit 2024~2026"）；桥接支持 **2020–2024**。两者交集仅 2024 |
| 单位 API 断代 | 库中 `UnitTypeId` / `ForgeTypeId` / `SpecTypeId` 是 Revit **2021+** API；Revit 2020 需 `DisplayUnitType.DUT_*` 旧体系。桥接 2020 适配包应继续用自有的 `FeetPerMillimeter` 常量（`src/RevitCommandExecutor.cs`），不要直接搬 `UnitConverter` |
| 条件编译先例 | 库内 `FilterRuleExtension.cs` 使用 `#if RLS_REVIT_2026` 按年份切换实现——桥接多年份适配包可借鉴此模式：一份源码 + 年份符号，替代为每个年份维护分支 |
| 移植原则 | 只取**算法模式**（连接件配对、延伸求交、AllRefs 遍历、失败预处理流程），在桥接代码风格（`PlanValues` 取参、`BridgeCommandException` 报错、mm 默认单位）下重写 |

### 4.3 版权与署名

共享库每个文件头部有作者署名（Haotian Zhou 周昊天）。若逐段复制代码，须先确认该库许可证与桥接 [LICENSE](../LICENSE) / [NOTICE.md](../NOTICE.md) 兼容并在 NOTICE 中补署名；仅参考算法模式重写则无此约束。

## 5. 新增一个原子操作的固定流程（6 处落点）

以 `transform_elements` 为例：

1. **登记白名单**：在 `src/PlanCommandExecutor.cs` 的 `AtomicOperations` 数组加名字。
2. **声明事务类别**：写模型操作加入同文件 `WriteOperations` 集合（自动获得 all-or-nothing 事务与 preview 状态）；只读感知型（如 `check_interferences`）两个集合都不进；有外部文件副作用的进 `ExternalOperations`（强制单独成计划）。
3. **注册分发**：在 `src/RevitPlanOperations.cs` 的 `Execute` switch 加 case。
4. **写实现**：按类别放入对应文件——修改类 `src/RevitPlanMutations.cs`、创建类 `src/RevitPlanCreations.cs`、查询类 `src/RevitPlanQueries.cs`、出图类 `src/RevitOutputOperations.cs`。实现时复用现成设施：
   - `context.ResolveElementIds()` 解析 `"$步骤ID"` 引用；
   - `PlanValues` / `BridgeArguments` 取参；
   - `FeetPerMillimeter`（`src/RevitCommandExecutor.cs`）做 mm → feet 换算；
   - 骨架照抄 `CreateMepCurve` / `ConnectMep`；
   - 涉及连接件匹配时，参考 `ConnectorExtension.GetNearConnectors` 的最近配对模式重写。
5. **更新契约**：`schemas/execute-plan.schema.json` 的 operation enum 加名字；可选在 `NormalizeAtomicOperation()` 加中文别名（如 `"移动元素"`）。
6. **文档 + 编译**：[PROTOCOL.md](../PROTOCOL.md) 操作表加一行；`build.ps1 -RevitVersion <year>` 按年份重编译。MCP 端零改动——`steps[].operation` 在 MCP schema 中是自由字符串，守门员是插件白名单。

## 6. 注意事项

1. **API 年份差异**：每个 Revit 年份单独编译，新调用的 API 必须存在于最老支持版本（2020）的 RevitAPI.dll；参考共享库代码时按 4.2 的版本断代表换算。多年份差异建议采用 `#if` 年份符号条件编译（借鉴 `RLS_REVIT_2026` 先例）。
2. **预览语义**：写操作在 `preview=true` 时不真正执行（`PlanCommandExecutor.Execute` 分流），实现方法在 preview 下返回"将要做什么"，参考现有创建类操作。
3. **失败处理**：P0 完成失败预处理器后，新写操作遇到可自动解决的 Error（如"连接件不匹配"）由预处理器统一处置，错误文本进入结果 JSON，不弹模态框。
4. **回归记录**：新操作完成真机验证后，按 `verification/` 目录惯例补一份回归记录，保持 `[V]` / `[T]` 证据文化。
5. **命名规范**：操作名用小写下划线（`transform_elements`）；查询用 `query_` / `check_` 前缀，创建用 `create_`，修改用 `set_` / `transform_` / `delete_` / `rename_` / `manage_`，与现有命名一致。
6. **族文档事务**：`manage_family_parameters` 等涉及 `Document.EditFamily` 的操作，族文档事务须独立于宿主计划事务提交，改动需 `LoadFamily` 回写并处理"覆盖族及参数"确认——实现前先在真机验证事务嵌套与回载对话框行为（可配合 P0 失败预处理器）。

## 7. 验收标准

每个新操作合入前须满足：

- [ ] 白名单、事务类别、分发 case、实现、schema、PROTOCOL.md 六处同步更新；
- [ ] `preview=true` 返回计划描述且不修改模型；
- [ ] `preview=false` 真机执行成功，失败时整个计划回滚（写操作）；
- [ ] `"$步骤ID"` 引用可用（涉及元素目标的操作）；
- [ ] 长度参数支持裸数（mm）与带单位字符串（如 `"3.6m"`）；
- [ ] 在 Revit 2020 真机完成回归并记录到 `verification/`；
- [ ] 若参考 / 移植 sepd-revit-extension 代码，已按 4.3 确认许可并补署名。
