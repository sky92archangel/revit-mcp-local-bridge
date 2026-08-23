# 原子操作扩展计划（技术实现版）

本文档是 `execute_plan` 白名单原子操作的能力扩展计划：现状评估 → 缺口分析 → 优先级路线图 → **逐项技术设计与实现步骤（含代码骨架、JSON 契约、数据结构）** → 参考实现资产 → 扩展流程与验收标准。目标读者是需要在插件端（C#）新增原子操作的开发者。

> 本版已将 [SEPD-ATOMIC-ANALYSIS.md](./SEPD-ATOMIC-ANALYSIS.md) 的结论（13 项新提议原子 + 5 项路线图重合项 + 88% 可组合论断）**合并进第 3 节路线图**，并在第 5–8 节为每一批操作给出可直接开工的实现规格。

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
| 无法加载现成 .rfa 族文件 | `create_family` 只能从零建模，标准件库（阀门、套管、消火栓……）的另一条主路径缺失 |
| 无几何感知（solid / face / bbox） | Agent 只能靠 `query_elements` 的 `bounding_box` 做粗判断，无法支持最近楼板 / 净高分析等组合配方（SEPD 分析中大量 B 类配方的公共依赖） |
| 视图范围、类别图形替换、明细表字段、Extensible Storage 等特殊对象 | 均为"参数体系之外"的对象，无法用 `set_parameters` 组合（SEPD A 类判定规则 ③） |
| 幕墙 / 楼梯 / 栏杆 / 嵌套族 / 钢筋 | ARCHITECTURE.md 已标注为 [T]，按需排期 |

## 3. 优先级路线图（已合并 SEPD 13 项新提议）

选择标准与现有架构一致：优先"高频 + 可组合 + 参数简单"的原子；"决策型"能力（自动布线、避障路径、管综规则）留给上层 Agent，用 `query_*` 感知 + 原子执行循环实现，不进入桥接。SEPD 分析证实该哲学：约 300 个生产用法中约 88% 无需新原子（68% 可组合 + 20% 不适用），仅 18 项需原子化。

### 3.1 P0（第一批，热身闭环）

| 新操作 | 对应 Revit API 入口 | 事务类别 | 难度 | 详细设计 |
| --- | --- | --- | --- | --- |
| `create_opening` 扩展：楼板竖直洞口 / 竖井 | `Document.Create.NewVerticalOpening`、`NewShaftOpening` | 写 | 低 | §5.1 |
| `connect_mep` 扩展：`reducer`、`cross` 配件 + `extend_to_intersection` | `Document.Create.NewTransitionFitting`、`NewCrossFitting`（与现有 `NewElbowFitting` 同族） | 写 | 低 | §5.2 |
| `transform_elements`（move/copy/rotate/mirror 四种 mode） | `ElementTransformUtils.MoveElements / CopyElements / RotateElements / MirrorElements` | 写 | 低 | §5.3 |
| 基础设施：计划事务挂接失败预处理器 | `transaction.GetFailureHandlingOptions().SetFailuresPreprocessor(...)`；警告自动消除、错误文本写入结果 JSON | 基础设施 | 低 | §5.4 |

### 3.2 P1（第二批，感知与系统）

| 新操作 | 对应 Revit API 入口 | 事务类别 | 难度 | 详细设计 |
| --- | --- | --- | --- | --- |
| `check_interferences`（碰撞检查，支持当前文档；链接模型碰撞用几何求交兜底） | `InterferenceChecker.FindInterferences`；跨链接文档用 `ElementIntersectsElementFilter` / 实体求交 | 只读 | 中 | §6.1 |
| `create_mep_system`（创建 / 指派系统） | `PipingSystem.Create`、`MechanicalSystem.Create` + `Add` | 写 | 中 | §6.2 |
| `create_mep_curve` 增加 `slope` 参数 | 无需新 API：按坡度换算终点 Z，纯参数增强 | 写 | 低 | §6.3 |
| `query_catalog(kind=links)`（链接模型清单） | `FilteredElementCollector` + `RevitLinkInstance.GetLinkDocument()` | 只读 | 低 | §6.4 |
| `query_parameters`（枚举元素 / 类型的全部参数：名称、值、单位、存储类型、只读标志） | `Element.Parameters` 遍历，逐项复用现成的 `RevitLookups.ParameterData` | 只读 | 低 | §6.5 |
| `rename_element`（重命名元素 / 类型 / 视图 / 标高等 `Element.Name`） | `Element.Name` 属性赋值 | 写 | 低 | §6.6 |
| `load_family`（SEPD ☆#1，从 .rfa 加载族 / 类型） | `Document.LoadFamily(path, IFamilyLoadOptions)` + 静默覆盖回调 | 写 | 低 | §6.7 |
| `query_geometry`（SEPD ☆#2，元素几何感知） | `Element.get_Geometry(Options)` 遍历（含 `GeometryInstance` 展开） | 只读 | 中 | §6.8 |
| `set_element_curve`（SEPD ☆#9，修改线状图元路径） | `(element.Location as LocationCurve).Curve = curve` | 写 | 低 | §6.9 |
| `query_room`（SEPD ☆#11，房间边界 + 点找房间） | `SpatialElementBoundaryOptions`、`Document.GetRoomAtPoint` | 只读 | 低 | §6.10 |
| `duplicate_view` 选项扩展（SEPD ☆#3） | 现有实现已覆盖基本复制；补 `ViewDuplicateOption`（`as_duplicate` / `with_detailing` / `without_detailing`）显式参数 | 写 | 低 | §6.11 |

### 3.3 P2（第三批，深化表现与拓扑）

| 新操作 | 对应 Revit API 入口 | 事务类别 | 难度 | 详细设计 |
| --- | --- | --- | --- | --- |
| `create_insulation`（保温层） | `PipeInsulation.Create`、`DuctInsulation.Create` | 写 | 低 | §7.1 |
| `query_mep_network`（管网连通拓扑） | `MEPCurve.ConnectorManager` + `Connector.AllRefs` 广度优先遍历 | 只读 | 中 | §7.2 |
| `set_element_overrides` + `set_category_overrides`（SEPD ☆#4 与原项合并设计：图元 / 类别两级图形替换） | `View.SetElementOverrides` / `View.SetCategoryOverrides` + `OverrideGraphicSettings` | 写 | 中 | §7.3 |
| `manage_view_filters`（视图过滤器创建 / 挂接 / 移除 / 清除，吸收 SEPD 建议合并删除语义） | `ParameterFilterElement.Create` + `View.AddFilter / RemoveFilter` | 写 | 中 | §7.4 |
| `query_view_range` / `set_view_range`（SEPD ☆#5，平面视图范围读写） | `ViewPlan.GetViewRange()` + `PlanViewRange.SetLevelId / SetOffset` + `ViewPlan.SetViewRange` | 读 / 写 | 中 | §7.5 |
| `manage_schedule_fields`（SEPD ☆#6，明细表字段增删 / 标题 / 显隐 / 过滤） | `ScheduleDefinition.AddField / GetField`、`ScheduleFilter` | 写 | 中 | §7.6 |
| `manage_schema_data`（SEPD ☆#8，Extensible Storage 读写） | `SchemaBuilder` + `Element.SetEntity / GetEntity` | 写 | 中 | §7.7 |
| `create_swept_shape`（SEPD ☆#10，路径放样实体：矩形 / 圆形 / 马蹄形截面工厂内置于插件） | `GeometryCreationUtilities.CreateSweptGeometry` + `DirectShape` | 写 | 中 | §7.8 |
| `create_view` 相机扩展（SEPD ☆#12） | `View3D.SetOrientation(ViewOrientation3D)`；`create_drafting_view` 已存在无需新增 | 写 | 低 | §7.9 |
| `manage_family_parameters`（对**已有**族追加 / 重命名 / 删除参数定义） | `Document.EditFamily(family)` → 族文档 `FamilyManager.AddParameter / RenameParameter / RemoveParameter` → `LoadFamily` 回写 | 写（跨文档事务） | 高 | §7.10 |

### 3.4 P3（按需，复杂对象）

| 领域 | 对应 Revit API 入口 | 难度 | 详细设计 |
| --- | --- | --- | --- |
| `manage_project_parameters`（SEPD ☆#7，项目 / 共享参数完整 CRUD，原"共享参数绑定"项升级） | 共享参数文件 + `Category.BoundParameters` / `DefinitionBindingMap` | 高 | §8.1 |
| `manage_graphics_resources`（SEPD ☆#13，线型 / 填充样式 get-or-create） | `Category.LineStyles`、`FilledRegionType` 属性 | 低 | §8.2 |
| 楼梯 / 栏杆 | `StairsEditScope`、`Railing.Create` | 高 | — |
| 幕墙 | `CurtainSystem` 系列 | 高 | — |
| 钢筋（结构深化） | `Autodesk.Revit.DB.Structure.Rebar.Create` | 高 | — |

## 4. 通用实现规约（所有新原子必须遵守）

以下规约是现有 40 个操作的事实标准，新原子照抄，避免两套风格。

### 4.1 结果封套（result envelope）

```csharp
var data = new Dictionary<string, object>
{
    { "operation_echo", ... }        // 描述性回显字段（可选）
};
if (context.Preview)
{
    return data;                     // preview：返回"将要做什么"，绝不触碰 API
}
// ...真实执行...
data["element_id"] = created.Id.IntegerValue;   // 单元素目标（供 "$stepId" 引用）
data["element_ids"] = new[] { created.Id.IntegerValue };  // 批量目标
return data;
```

- 创建 / 修改类：必须写 `element_id` 或 `element_ids`（`PlanExecutionContext.ResolveElementIds` 从这两个键解析 `$引用`，见 `src/PlanCommandExecutor.cs:459`）。
- 查询类：返回 `items`（数组）或单对象；列表附带 `count`。
- 所有错误用 `throw new BridgeCommandException("中文消息，含参数名。")`——`ExecuteSteps` 会包装为"计划步骤"id"（operation）失败：..."并触发整个计划回滚。

### 4.2 preview 语义与 deferred 模式

写操作实现分两段：**校验 + 组装 data → `if (context.Preview) return data;` → 执行**。校验段只做参数检查和只读查询（类型解析、名称查重），保证 preview 能拦截绝大多数错误。

依赖前置步骤 ID 的操作（如 `connect_mep`、`create_opening`），在 preview 下前置元素还没有真实 ID，必须照抄 deferred 模式：

```csharp
ElementId hostId = context.ResolveSingleElementId(step.Arguments, "host_id", "host");
if (hostId.IntegerValue == ElementId.InvalidElementId.IntegerValue)
{
    return new Dictionary<string, object>
    {
        { "deferred", true },
        { "reason", "preview 中前置元素引用尚无真实 ID。" }
    };
}
```

### 4.3 单位与角度

- 长度：入口一律 mm。取参用 `PlanValues.Millimeters(args, 默认值, "xxx_mm", "xxx")`（兼容裸数与 `"3.6m"` 字符串），写模型前 `PlanValues.ToFeet(mm)`。
- 角度（`transform_elements` rotate、相机朝向等新引入）：计划契约统一**度**，内部换算弧度。需在 `PlanValues` 新增：

```csharp
public static double AngleDegrees(IDictionary<string, object> values, double defaultValue, params string[] names)
{
    return Number(values, defaultValue, names);
}

public static double ToRadians(double degrees)
{
    return degrees * Math.PI / 180.0;
}
```

- 方向 / 位移向量：复用 `PlanValues.Point(args, "translation")` 得 `XYZ`（mm→feet 由 `Point` 内部完成，与 `create_wall.start` 一致）。

### 4.4 中文别名

`NormalizeAtomicOperation()`（`src/PlanCommandExecutor.cs:282`）为每个新操作加别名，保持 MCP 端中文计划可读：

```csharp
case "变换元素":
case "移动元素": return "transform_elements";
case "重命名元素": return "rename_element";
case "加载族": return "load_family";
case "查询几何": return "query_geometry";
case "查询参数": return "query_parameters";
case "碰撞检查": return "check_interferences";
case "创建机电系统": return "create_mep_system";
case "创建保温层": return "create_insulation";
case "查询管网": return "query_mep_network";
case "修改线型": return "set_element_curve";
case "查询房间": return "query_room";
case "设置视图范围": return "set_view_range";
case "查询视图范围": return "query_view_range";
case "管理明细表字段": return "manage_schedule_fields";
case "管理视图过滤器": return "manage_view_filters";
```

### 4.5 版本适配：`#if` 符号条件编译

仓库已有先例：`RevitLookups.ParameterData`（`src/RevitLookups.cs:261`）用 `#if REVIT_FORGE_UNITS` 切换 `GetUnitTypeId()`（2021+）与 `DisplayUnitType`（2020）。所有新操作的版本差异（见 §11.1 清单）一律沿用该模式，符号由 `build.ps1 -RevitVersion <year>` 注入，禁止按年份维护分支。

## 5. P0 详细设计与实现步骤

### 5.1 `create_opening` 扩展：楼板竖直洞口 / 竖井

**契约**（在现有 `host_id/start/end` 基础上增加按宿主类型分流）：

```json
{ "id": "opening_slab", "operation": "create_opening", "args": {
    "host_id": "$slab_step",
    "kind": "vertical",
    "corner_1": { "x": 1000, "y": 1000 },
    "corner_2": { "x": 3000, "y": 2500 }
} }
```

```json
{ "id": "shaft", "operation": "create_opening", "args": {
    "kind": "shaft",
    "bottom_level": "F2",
    "top_level": "F5",
    "boundary": [
        { "x": 0, "y": 0 }, { "x": 4000, "y": 0 },
        { "x": 4000, "y": 3000 }, { "x": 0, "y": 3000 }
    ]
} }
```

**实现骨架**（`src/RevitPlanCreations.cs` 改造 `CreateOpening`，现有 `src/RevitPlanCreations.cs:443`）：

```csharp
string kind = PlanValues.String(step.Arguments, "wall", "kind", "opening_type").ToLowerInvariant();
switch (kind)
{
    case "wall":
        // 现有两点矩形洞口逻辑保持不变
        break;
    case "vertical":
        Floor floor = context.Document.GetElement(hostId) as Floor;
        if (floor == null)
        {
            throw new BridgeCommandException("create_opening(kind=vertical) 的 host_id 必须指向楼板。");
        }
        XYZ c1 = PlanValues.Point(step.Arguments, "corner_1");
        XYZ c2 = PlanValues.Point(step.Arguments, "corner_2");
        // 两点为洞口矩形在世界坐标下的对角点（XY 平面），XY 各分量不得相等
        Opening opening = context.Document.Create.NewVerticalOpening(floor, c1, c2);
        break;
    case "shaft":
        Level bottomLevel = RevitLookups.ResolveLevel(context.Document, step.Arguments, "bottom_level");
        Level topLevel = RevitLookups.ResolveLevel(context.Document, step.Arguments, "top_level");
        CurveLoop profile = BuildCurveLoop(step.Arguments, "create_opening.boundary");
        Opening shaft = context.Document.Create.NewShaftOpening(bottomLevel, topLevel, profile);
        break;
}
```

**实现步骤**：

1. 抽取 `BuildClosedProfile`（现有 `src/RevitPlanCreations.cs:482`）中的共面 / 闭环校验为通用 `BuildCurveLoop(arguments, fieldName)` 返回 `CurveLoop`（竖井 `NewShaftOpening` 收 `CurveLoop` 而非 `CurveArray`）。
2. `RevitLookups.ResolveLevel` 现签名按 `level` / `level_id` / `level_name` 取——增加 `params string[] fieldNames` 透传重载，供 `bottom_level` / `top_level` 复用。
3. 校验：竖井 `boundary` ≥ 3 点且投影不自交（暂用相邻不重合 + 共面校验，自交检测可后置）；`vertical` 两对角点 X、Y 均不得相等。
4. **真机核对**（Revit 2020 API 文档）：`NewVerticalOpening` 两点的平面语义（是对角点还是同边两点）——写入 `verification/` 记录后再定稿契约。
5. 放置校验参考：`FloorExtension.GetFloorBoundaryPolygon` 的"几何法提取楼板最低面边界（含洞口环）"模式，可用于后续"洞口是否落在板内"校验（本期可只做包围盒粗校验）。

### 5.2 `connect_mep` 扩展：`reducer` / `cross` / `extend_to_intersection`

**契约**：

```json
{ "id": "reducer_1", "operation": "connect_mep", "args": {
    "element_a": "$pipe_dn100", "element_b": "$pipe_dn65", "fitting": "reducer"
} }
```

```json
{ "id": "cross_1", "operation": "connect_mep", "args": {
    "element_a": "$main_in", "element_b": "$main_out",
    "element_c": "$branch_1", "element_d": "$branch_2",
    "fitting": "cross"
} }
```

```json
{ "id": "elbow_auto_extend", "operation": "connect_mep", "args": {
    "element_a": "$pipe_1", "element_b": "$pipe_2",
    "fitting": "elbow", "extend_to_intersection": true
} }
```

**实现骨架**（扩展 `ConnectMep` 的 switch，`src/RevitPlanCreations.cs:769`）：

```csharp
case "reducer":
    fittingElement = context.Document.Create.NewTransitionFitting(firstConnector, secondConnector);
    break;
case "cross":
    ElementId cId = context.ResolveSingleElementId(step.Arguments, "element_c", "third");
    ElementId dId = context.ResolveSingleElementId(step.Arguments, "element_d", "fourth");
    Element third = RequireElement(context.Document, cId, "element_c");
    Element fourth = RequireElement(context.Document, dId, "element_d");
    Connector thirdConnector = FindConnector(third, step.Arguments, "connector_c_index", first);
    Connector fourthConnector = FindConnector(fourth, step.Arguments, "connector_d_index", second);
    fittingElement = context.Document.Create.NewCrossFitting(
        firstConnector, secondConnector, thirdConnector, fourthConnector);
    data["element_c"] = cId.IntegerValue;
    data["element_d"] = dId.IntegerValue;
    break;
```

错误消息同步更新：`"connect_mep.fitting 仅支持 auto、direct、elbow、union、tee、reducer、cross。"`。

**`extend_to_intersection`（可选布尔，默认 false）**——移植 `MEPCurveExtension.ConnectMEPCurveElbowFitting` 的算法模式：

```
1. 取两管 LocationCurve（Extension 曲线，非 CenterLine）
2. 两线 Intersect(Curve, IntersectionResultArray) 求交点；平行 / 不相交则报错
3. 按交点截断：Line.CreateBound(保留端点, 交点) 赋回 locationCurve.Curve（两管各一次）
4. context.Document.Regenerate()
5. 在交点附近找端部 Connector（CloseConnectorToPoint 模式：遍历 ConnectorManager.Connectors，
   取 Origin 距交点最近且 ConnectorType == EndConnector）
6. NewElbowFitting / NewTeeFitting / NewCrossFitting
```

连接件匹配复用现有 `FindConnector` 私有方法；多口配件（cross）必须显式传 `connector_*_index` 或按最近配对（`ConnectorExtension.GetNearConnectors` 模式）：

```csharp
private static Tuple<Connector, Connector> FindNearConnectorPair(ConnectorSet a, ConnectorSet b)
{
    Connector bestA = null;
    Connector bestB = null;
    double bestDistance = double.MaxValue;
    foreach (Connector candidateA in a)
    {
        foreach (Connector candidateB in b)
        {
            double distance = candidateA.Origin.DistanceTo(candidateB.Origin);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                bestA = candidateA;
                bestB = candidateB;
            }
        }
    }
    if (bestA == null)
    {
        throw new BridgeCommandException("找不到可配对的连接件。");
    }
    return Tuple.Create(bestA, bestB);
}
```

**注意**：步骤 3 修改 `LocationCurve` 与步骤 6 创建配件必须在**同一事务**内完成——这正是该操作必须原子化（SEPD A 类规则 ②）的原因，桥接的计划级 all-or-nothing 事务天然满足。

### 5.3 `transform_elements`

**契约**：

```json
{ "id": "move_pipes", "operation": "transform_elements", "args": {
    "element_ids": ["$pipes_step"], "mode": "move",
    "translation": { "x": 300, "y": 0, "z": 0 }
} }
```

```json
{ "id": "copy_riser", "operation": "transform_elements", "args": {
    "element_ids": [123456], "mode": "copy",
    "translation": { "x": 8400, "y": 0, "z": 0 }
} }
```

```json
{ "id": "rotate_valves", "operation": "transform_elements", "args": {
    "element_ids": ["$valves"], "mode": "rotate",
    "axis_origin": { "x": 0, "y": 0, "z": 0 },
    "axis_direction": "z", "angle": 90
} }
```

```json
{ "id": "mirror_wing", "operation": "transform_elements", "args": {
    "element_ids": ["$left_wing"], "mode": "mirror",
    "plane_point": { "x": 0, "y": 21000, "z": 0 }, "plane_normal": { "x": 0, "y": 1, "z": 0 }
} }
```

**实现骨架**（新文件归属：`src/RevitPlanMutations.cs`）：

```csharp
public static Dictionary<string, object> TransformElements(PlanStep step, PlanExecutionContext context)
{
    IList<ElementId> targets = context.ResolveElementIds(step.Arguments, "element_ids", "elements", "targets");
    if (targets.Count == 0)
    {
        throw new BridgeCommandException("transform_elements 至少需要一个 element_ids 目标。");
    }
    string mode = PlanValues.String(step.Arguments, null, "mode").Trim().ToLowerInvariant();
    var data = new Dictionary<string, object>
    {
        { "mode", mode },
        { "target_count", targets.Count }
    };
    if (context.Preview)
    {
        return data;
    }

    Document document = context.Document;
    switch (mode)
    {
        case "move":
            XYZ translation = PlanValues.Point(step.Arguments, "translation");
            ElementTransformUtils.MoveElements(document, targets, translation);
            data["translation"] = PlanValues.PointData(translation);
            break;
        case "copy":
            XYZ offset = PlanValues.Point(step.Arguments, "translation");
            ICollection<ElementId> copied = ElementTransformUtils.CopyElements(document, targets, offset);
            data["element_ids"] = copied.Select(id => id.IntegerValue).ToArray();
            data["copied_count"] = copied.Count;
            break;
        case "rotate":
            XYZ origin = PlanValues.Point(step.Arguments, "axis_origin");
            XYZ direction = ResolveAxisDirection(step.Arguments);          // "x"/"y"/"z" 或向量
            Line axis = Line.CreateUnbound(origin, direction);
            double angle = PlanValues.ToRadians(PlanValues.Number(step.Arguments, 0.0, "angle", "angle_deg"));
            ElementTransformUtils.RotateElements(document, targets, axis, angle);
            data["angle"] = angle;
            break;
        case "mirror":
            XYZ point = PlanValues.Point(step.Arguments, "plane_point");
            XYZ normal = PlanValues.Point(step.Arguments, "plane_normal");
            Plane plane = Plane.CreateByNormalAndOrigin(normal.Normalize(), point);
            ICollection<ElementId> mirrored = ElementTransformUtils.MirrorElements(document, targets, plane, true);
            data["element_ids"] = mirrored.Select(id => id.IntegerValue).ToArray();
            break;
        default:
            throw new BridgeCommandException("transform_elements.mode 仅支持 move、copy、rotate、mirror。");
    }
    return data;
}
```

**实现步骤**：

1. `move` / `rotate` / `mirror`（mirror=true 变副本）返回**新元素 ID**，与 `move` 只回显参数区分——`copy` 与 `mirror` 的 `element_ids` 指向新副本，可直接被 `$copy_riser` 引用。
2. `ResolveAxisDirection`：接受 `"x"|"y"|"z"` 字符串或 `{x,y,z}` 向量字典（转 `XYZ`，Normalize 后交 `Line.CreateUnbound`）。
3. **真机核对**：Revit 2020 `MirrorElements` 是否存在四参重载（`..., bool mirror)` 返回副本 ID）；若只有三参版（原地镜像），则 `copy` 模式 = `CopyElements` + `MirrorElements` 两步组合，实现层消化、契约不变。
4. 边界：旋转角度为 0、位移为零向量时直接报错（无意义操作，宁可拒绝也不产生空事务噪音）；镜像法向量长度 < 1e-9 报错。

### 5.4 基础设施：失败预处理器

**目标**：无人值守队列不因模态警告卡死；Error 能自动解决则解决，不能解决则回滚并把错误文本带回结果 JSON。

**新文件 `src/BridgeFailurePreprocessor.cs`**（参考 `FailureProcessor.ContinueFailureProcessor` 模式重写）：

```csharp
using System.Collections.Generic;
using Autodesk.Revit.DB;

namespace RevitCommandBridge
{
    internal sealed class BridgeFailurePreprocessor : IFailuresPreprocessor
    {
        public List<string> Messages { get; private set; }

        public BridgeFailurePreprocessor()
        {
            Messages = new List<string>();
        }

        public FailureProcessingResult PreprocessFailures(FailuresAccessor failuresAccessor)
        {
            foreach (FailureMessageAccessor failure in failuresAccessor.GetFailureMessages())
            {
                FailureSeverity severity = failure.GetSeverity();
                string description = failure.GetDescriptionText() ?? string.Empty;
                Messages.Add(severity.ToString() + ": " + description);
                if (severity == FailureSeverity.Warning)
                {
                    failuresAccessor.DeleteWarning(failure);
                }
            }
            if (Messages.Exists(message => message.StartsWith(FailureSeverity.DocumentCorruption.ToString())))
            {
                return FailureProcessingResult.ProceedWithRollBack;
            }
            if (failuresAccessor.IsTransactionResolutionPossible())
            {
                return FailureProcessingResult.Continue;
            }
            return FailureProcessingResult.ProceedWithCommit;
        }
    }
}
```

**挂接点**：`PlanCommandExecutor.Execute` 的事务开启后（`src/PlanCommandExecutor.cs:147` 附近）：

```csharp
using (Transaction transaction = new Transaction(document, "RCB 通用建模计划"))
{
    transaction.Start();
    var preprocessor = new BridgeFailurePreprocessor();
    FailureHandlingOptions failureOptions = transaction.GetFailureHandlingOptions();
    failureOptions.SetFailuresPreprocessor(preprocessor);
    failureOptions.SetForcedModalHandling(false);
    transaction.SetFailureHandlingOptions(failureOptions);
    // ...现有 ExecuteSteps / Commit / RollBack...
    if (preprocessor.Messages.Count > 0)
    {
        data["failure_messages"] = preprocessor.Messages.ToArray();
    }
}
```

**验收**：构造已知触发"墙重叠警告"的计划 → 执行 → Revit 不弹框、结果 JSON 含 `failure_messages`、模型正常提交。

## 6. P1 详细设计与实现步骤

### 6.1 `check_interferences`

**双引擎架构**：

```
输入: element_ids（候选集，支持 $引用）
     + optional: against_ids（对照集，缺省为候选集两两互查）
     + optional: include_links=true 时对照集扩展为链接模型元素

引擎 A（同文档）: InterferenceChecker.FindInterferences(candidates, targets) → InterferenceReport
引擎 B（跨链接）: 对每个候选取 Solid（GetSolidByElement 模式，GeometryInstance 用 GetInstanceGeometry 展开）
                → ElementIntersectsElementFilter(solidElement) 在宿主文档收集链接内元素
                → BooleanOperationsUtils.ExecuteBooleanOperation(Intersect) 体积 > 0 确认
```

**结果数据结构**：

```json
{
  "count": 2,
  "interferences": [
    {
      "element_a": 112233,
      "element_b": 112344,
      "document_a": "current",
      "document_b": "current",
      "category_a": "管道",
      "category_b": "风管",
      "distance_mm": 0
    },
    {
      "element_a": 112233,
      "element_b": 556677,
      "document_a": "current",
      "document_b": "link:结构模型.rvt",
      "category_a": "管道",
      "category_b": "结构框架",
      "distance_mm": 0,
      "overlap_volume_mm3": 1250000
    }
  ]
}
```

**实现要点**：

1. 只读操作：`AtomicOperations` 登记，但**不进** `WriteOperations` / `ExternalOperations`（同 `query_elements`）。
2. `InterferenceChecker` 只处理同一 Document 的 ElementId；链接文档元素 ID 与宿主不互通——跨链接一律走引擎 B。
3. 引擎 B 的大模型性能护栏：候选集 > 500 个元素时要求显式 `against_ids`（否则报错提示缩小范围），避免全模型布尔求交。
4. 可选 `clearance_mm`（默认 0）：对求交为空的对，用两 solid 最近距离（`ClosestDistance` 逐面对算或 `Solid.ClosestDistanceTo`——2020 无该 API，用 `ComputeClosestDistance` 的 `DistanceTo` 曲面近似或跳过）补"净距不足"报告。**第一期只做实碰（distance=0），净距检查列入二期**，避免几何 API 深坑。
5. 放置在 `src/RevitPlanQueries.cs`。

### 6.2 `create_mep_system`

**契约**：

```json
{ "id": "sys_j2", "operation": "create_mep_system", "args": {
    "domain": "piping",
    "system_type": "循环供水",
    "name": "J2-给水-低区",
    "members": ["$pipe_1", "$pipe_2", "$valve_1"]
} }
```

**实现骨架**（`src/RevitPlanCreations.cs`）：

```csharp
string domain = PlanValues.String(step.Arguments, null, "domain").ToLowerInvariant();
string name = PlanValues.String(step.Arguments, step.Id, "name", "system_name");
Element created;
switch (domain)
{
    case "piping":
        PipingSystemType pipingType = ResolveSystemType<PipingSystemType>(context.Document, step.Arguments, "system_type");
        created = PipingSystem.Create(context.Document, pipingType.Id, name);
        break;
    case "mechanical":
        MechanicalSystemType mechanicalType = ResolveSystemType<MechanicalSystemType>(context.Document, step.Arguments, "system_type");
        created = MechanicalSystem.Create(context.Document, mechanicalType.Id, name);
        break;
    default:
        throw new BridgeCommandException("create_mep_system.domain 仅支持 piping、mechanical。");
}
IList<ElementId> members = context.ResolveElementIds(step.Arguments, "members", "element_ids");
foreach (ElementId memberId in members)
{
    ((dynamic)created).Add(memberId);   // 实际按类型拆分：PipingSystem.Add / MechanicalSystem.Add
}
data["element_id"] = created.Id.IntegerValue;
data["member_count"] = members.Count;
```

（实现时不用 `dynamic`，按 domain 分支调用强类型 `Add`；`ResolveSystemType<T>` 已在 `RevitPlanCreations` 私有方法中存在。）

**要点**：`Add` 会校验成员与系统的域匹配，不匹配抛出的 API 异常包装为 `BridgeCommandException`；`members` 空时允许创建空系统（`element_ids` 仍返回系统自身 ID，后续可扩展 `manage_mep_system` 的 `add`/`remove` 动作）。

### 6.3 `create_mep_curve` 增加 `slope`

**契约**：`"slope": 0.3`（百分比，正 = 沿 start→end 抬升；可选 `slope_unit: "percent" | "permille"`，默认 percent）。

**实现**（改造 `CreateMepCurve`，`src/RevitPlanCreations.cs:645`）：

```csharp
double? slopePercent = null;
object rawSlope = PlanValues.Get(step.Arguments, "slope", "slope_percent");
if (rawSlope != null)
{
    string unit = PlanValues.String(step.Arguments, "percent", "slope_unit").ToLowerInvariant();
    double value = PlanValues.Number(step.Arguments, 0.0, "slope", "slope_percent");
    slopePercent = unit == "permille" ? value / 10.0 : value;
}
if (slopePercent.HasValue)
{
    double horizontal = new XYZ(start.X, start.Y, 0).DistanceTo(new XYZ(end.X, end.Y, 0));
    if (horizontal < 1e-6)
    {
        throw new BridgeCommandException("slope 不能用于竖直管线（start/end 水平投影重合）。");
    }
    double rise = horizontal * slopePercent.Value / 100.0;
    end = new XYZ(end.X, end.Y, start.Z + PlanValues.ToFeet(rise));
    data["slope_percent"] = slopePercent.Value;
    data["end"] = PlanValues.PointData(end);
}
```

注意 `rise` 单位换算：`horizontal` 已是 feet，`rise` 用 feet 系数内联换算或统一先转 mm 再回来——实现时保持与 `PlanValues` 语义一致并补一条注释级说明到 PROTOCOL.md。

### 6.4 `query_catalog(kind=links)`

**结果数据结构**：

```json
{ "kind": "links", "count": 2, "items": [
    { "element_id": 889900, "name": "结构模型.rvt", "status": "Loaded",
      "is_linked_file": true, "has_link_document": true,
      "instance_transform_origin": {"x": 0, "y": 0, "z": 0} }
] }
```

**实现骨架**（`src/RevitPlanQueries.cs` 的 `QueryCatalog` 加分支）：

```csharp
case "links":
    foreach (RevitLinkInstance link in new FilteredElementCollector(document)
        .OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>())
    {
        RevitLinkType linkType = document.GetElement(link.GetTypeId()) as RevitLinkType;
        items.Add(new Dictionary<string, object>
        {
            { "element_id", link.Id.IntegerValue },
            { "name", link.Name },
            { "status", linkType == null ? null : linkType.GetLinkedFileStatus().ToString() },
            { "has_link_document", link.GetLinkDocument() != null }
        });
    }
    break;
```

`GetLinkDocument() == null` 表示链接未载入 / 卸载状态——`check_interferences` 跨链接前先查此清单。

### 6.5 `query_parameters`

**契约**：

```json
{ "id": "params_1", "operation": "query_parameters", "args": {
    "element_id": 123456, "include_read_only": true, "name_contains": "管径"
} }
```

**结果数据结构**（值复用 `RevitLookups.ParameterData`，`src/RevitLookups.cs:249`）：

```json
{ "element_id": 123456, "count": 42, "parameters": [
    { "name": "直径", "group": "PG_MECHANICAL",
      "storage_type": "Double", "read_only": false, "display": "100.0mm",
      "internal_value": 0.328, "display_unit_type": "DUT_MILLIMETERS" }
] }
```

**实现骨架**：

```csharp
ElementId targetId = context.ResolveSingleElementId(step.Arguments, "element_id", "element", "id");
Element element = context.Document.GetElement(targetId);
if (element == null)
{
    throw new BridgeCommandException("query_parameters 找不到目标元素。");
}
string nameFilter = PlanValues.String(step.Arguments, null, "name_contains", "name_like");
bool includeReadOnly = PlanValues.Boolean(step.Arguments, true, "include_read_only");
var parameters = new List<Dictionary<string, object>>();
foreach (Parameter parameter in element.Parameters)
{
    if (parameter.Definition == null) { continue; }
    if (!includeReadOnly && parameter.IsReadOnly) { continue; }
    if (!string.IsNullOrWhiteSpace(nameFilter) &&
        parameter.Definition.Name.IndexOf(nameFilter, StringComparison.OrdinalIgnoreCase) < 0) { continue; }
    var item = new Dictionary<string, object>
    {
        { "name", parameter.Definition.Name },
        { "group", parameter.Definition.ParameterGroup.ToString() }
    };
    foreach (KeyValuePair<string, object> pair in RevitLookups.ParameterData(parameter))
    {
        item[pair.Key] = pair.Value;
    }
    parameters.Add(item);
}
```

**归档文件**：`src/RevitPlanQueries.cs`。BIP 参数额外带 `built_in_id`（`(parameter.Definition as InternalDefinition).BuiltInParameter` 非 null 时输出 `toString()`），方便 Agent 直接拼 `set_parameters` 的 `BIP:` 键。

### 6.6 `rename_element`

**契约**：

```json
{ "id": "rename_1", "operation": "rename_element", "args": {
    "element_ids": ["$view_1"], "name": "给排水-二层平面",
    "prefix_mode": null
} }
```

**实现**（`src/RevitPlanMutations.cs`）：核心是 `element.Name = newName;`（写事务内）。两种模式：

- 单目标：`name` 必填。
- 批量模式（吸收 `FamilyExtension.RenameByPrefixId` 模式）：`prefix: "ZS-"` + 可选 `id_suffix: true` → 新名 = `prefix + 原名` 或 `prefix + ElementId`。批量时 `element_ids` 逐个改名，返回 `renamed` 映射数组 `[{ "element_id": 1, "old_name": "...", "new_name": "..." }]`。

校验：与 `CreateLevel` 相同的查重策略（同类别下 `FilteredElementCollector` 按名匹配报错），Revit 自身对重名也会抛异常——预检给出更友好的中文错误。

### 6.7 `load_family`（SEPD ☆#1）

**契约**：

```json
{ "id": "load_valve", "operation": "load_family", "args": {
    "path": "R:\\Lib\\阀门\\闸阀.rfa",
    "symbol": "DN100", "overwrite": true
} }
```

**静默覆盖回调**（新文件 `src/BridgeFamilyLoadOptions.cs`，参考 `FamilyLoadOptions.MyFamilyLoadOptions` 模式）：

```csharp
internal sealed class BridgeFamilyLoadOptions : IFamilyLoadOptions
{
    public bool OnFamilyFound(bool familyInUse, out bool overwriteParameterValues)
    {
        overwriteParameterValues = true;
        return true;
    }

    public bool OnSharedFamilyFound(Family sharedFamily, bool familyInUse,
        out FamilySource source, out bool overwriteParameterValues)
    {
        source = FamilySource.Family;
        overwriteParameterValues = true;
        return true;
    }
}
```

**实现骨架**（`src/RevitPlanCreations.cs`）：

```csharp
string path = PlanValues.String(step.Arguments, null, "path", "family_path", "file");
if (string.IsNullOrWhiteSpace(path) || !System.IO.File.Exists(path))
{
    throw new BridgeCommandException("load_family.path 不存在：" + path);
}
var data = new Dictionary<string, object> { { "path", path } };
if (context.Preview) { return data; }

Family family;
if (!context.Document.LoadFamily(path, new BridgeFamilyLoadOptions(), out family))
{
    throw new BridgeCommandException("Revit 拒绝加载族文件：" + path);
}
data["family"] = family.Name;
data["element_id"] = family.Id.IntegerValue;
data["element_ids"] = new[] { family.Id.IntegerValue };
data["symbol_names"] = family.Symbols.Cast<FamilySymbol>().Select(symbol => symbol.Name).ToArray();

string symbolName = PlanValues.String(step.Arguments, null, "symbol", "type", "type_name");
if (!string.IsNullOrWhiteSpace(symbolName))
{
    FamilySymbol symbol = family.Symbol(symbolName);   // 不存在则报错列出可选值
    if (!symbol.IsActive)
    {
        symbol.Activate();
    }
    data["symbol_id"] = symbol.Id.IntegerValue;
}
```

**注意**：`family.Symbols` 在 2021+ 改名 `FamilySymbolTypes`，此处按 2020 写、2021+ 包 `#if`（与 `REVIT_FORGE_UNITS` 同一符号族）。`Activate` 需在事务内——`load_family` 归入 `WriteOperations`。

### 6.8 `query_geometry`（SEPD ☆#2）

**契约**：

```json
{ "id": "geo_1", "operation": "query_geometry", "args": {
    "element_id": 123456, "detail": "bbox",
    "include": ["solid_summary", "faces_top"]
} }
```

`detail` 三档：`bbox`（默认，最便宜）/ `solid_summary`（体积、面积、面数）/ `faces`（含法向量的面列表）。**不返回全量三角网**——契约刻意保持"简化几何"（顶点 / 包围盒 / 面法向 / 面积），大几何让 Agent 走导出路径。

**实现骨架**（`src/RevitPlanQueries.cs`；solid 提取按 `GeometryExtension.GetSolidByElement` 模式重写）：

```csharp
var options = new Options
{
    DetailLevel = ViewDetailLevel.Fine,
    ComputeReferences = false,
    IncludeNonVisibleObjects = false
};
GeometryElement geometryElement = element.get_Geometry(options);
foreach (GeometryObject geometryObject in geometryElement)
{
    GeometryInstance instance = geometryObject as GeometryInstance;
    if (instance != null)
    {
        foreach (GeometryObject instanceObject in instance.GetInstanceGeometry())
        {
            AccumulateGeometry(instanceObject, solids);   // 族实例展开
        }
    }
    else
    {
        AccumulateGeometry(geometryObject, solids);
    }
}
// bbox: element.get_BoundingBox(null) → 转 mm 输出 { min, max, center, size }
// solid_summary: solid.Volume / solid.Faces.Size / SurfaceArea（内部 feet³ → mm³ 用 Math.Pow 换算）
// faces: PlanarFace.FaceNormal + Area
```

**依赖顺序**：此操作是 SEPD B 类配方的公共依赖（`FloorExtension.GetCloseFloor`、`GetCloseFloorU/D`、`BeamExtension` 标高推算等组合都靠它），应与 `query_catalog` 联动排期。

### 6.9 `set_element_curve`（SEPD ☆#9）

**契约**：

```json
{ "id": "reshape_pipe", "operation": "set_element_curve", "args": {
    "element_id": 123456, "start": { "x": 0, "y": 0, "z": 3200 }, "end": { "x": 6000, "y": 0, "z": 3200 }
} }
```

**实现**（`src/RevitPlanMutations.cs`）：

```csharp
LocationCurve location = element.Location as LocationCurve;
if (location == null)
{
    throw new BridgeCommandException("set_element_curve 目标必须是线状图元（墙 / 管道 / 线管 / 桥架 / 模型线）。");
}
Line newCurve = Line.CreateBound(start, end);
if (!location.IsCurveBoundIllustrative && !location.Curve.ApproximatelyEquals(newCurve)) { /* 占位注释：无此 API，直接赋值 */ }
location.Curve = newCurve;
```

（实际实现直接 `location.Curve = newCurve;`。）**注意**：已连接的 MEP 曲线改线会破坏连接或触发自动调整——依赖 P0 失败预处理器吃掉相关警告；改线后管长参数由 Revit 重算，结果 JSON 回显新 `length_mm`。

### 6.10 `query_room`（SEPD ☆#11）

**契约**（两种 mode 二选一）：

```json
{ "id": "rooms_f2", "operation": "query_room", "args": { "level": "F2" } }
```

```json
{ "id": "room_at", "operation": "query_room", "args": { "point": { "x": 5000, "y": 8000, "z": 4200 } } }
```

**结果数据结构**：

```json
{ "mode": "by_level", "count": 12, "rooms": [
    { "element_id": 991001, "name": "办公室", "number": "201",
      "level": "F2", "area_mm2": 18000000,
      "boundary": [ [ {"x":0,"y":0}, {"x":6000,"y":0}, {"x":6000,"y":3000} ] ] }
] }
```

**实现要点**（`src/RevitPlanQueries.cs`）：

```csharp
SpatialElementBoundaryOptions boundaryOptions = new SpatialElementBoundaryOptions();
IList<IList<BoundarySegment>> loops = room.GetBoundarySegments(boundaryOptions);
// 每段取 GetCurve() 端点 → 边界环顶点序列（内部 feet → mm）

Room roomAtPoint = document.GetRoomAtPoint(point, phase);   // phase 缺省取视图/最后阶段
```

`GetRoomAtPoint` 收 feet——沿用 `PlanValues.Point` 换算；未命中返回 `room: null` 而非报错（Agent 需要区分"点不在任何房间"）。

### 6.11 `duplicate_view` 选项扩展（SEPD ☆#3）

现有 `duplicate_view`（`src/RevitOutputOperations.cs`）已实现基本复制。扩展点：显式 `option` 参数映射 `ViewDuplicateOption`：

| 契约值 | `ViewDuplicateOption` | 语义 |
| --- | --- | --- |
| `as_duplicate`（默认） | `AsDuplicate` | 关联复制（随源视图更新） |
| `with_detailing` | `WithDetailing` | 独立副本带注释 |
| `without_detailing` | `WithoutDetailing` | 独立副本不带注释（仅平面类可用） |

调用点改为 `view.Duplicate(parsedOption)`；`WithoutDetailing` 对非平面视图抛 `BridgeCommandException` 说明原因。另补 `view_template` 可选参数：复制后 `view.ViewTemplateId` 赋值，覆盖"复制后套样板"这一高频组合。

## 7. P2 详细设计与实现步骤

### 7.1 `create_insulation`

**契约**：

```json
{ "id": "insul_1", "operation": "create_insulation", "args": {
    "element_ids": ["$pipes_step"], "thickness_mm": 40, "type": "玻璃棉"
} }
```

**实现**（`src/RevitPlanCreations.cs`）：

```csharp
double thickness = PlanValues.ToFeet(PlanValues.RequireMillimeters(step.Arguments, "thickness_mm", "thickness"));
string typeName = PlanValues.String(step.Arguments, null, "type", "insulation_type");
foreach (ElementId targetId in targets)
{
    Element target = context.Document.GetElement(targetId);
    Pipe pipe = target as Pipe;
    if (pipe != null)
    {
        ElementId insulationTypeId = ResolveInsulationTypeId<PipeInsulationType>(context.Document, typeName);
        PipeInsulation created = PipeInsulation.Create(context.Document, pipe.Id, insulationTypeId, thickness);
        resultIds.Add(created.Id);
        continue;
    }
    Duct duct = target as Duct;
    if (duct != null)
    {
        ElementId insulationTypeId = ResolveInsulationTypeId<DuctInsulationType>(context.Document, typeName);
        DuctInsulation created = DuctInsulation.Create(context.Document, duct.Id, insulationTypeId, thickness);
        resultIds.Add(created.Id);
        continue;
    }
    throw new BridgeCommandException("create_insulation 目标必须是管道或风管：" + targetId.IntegerValue);
}
```

`ResolveInsulationTypeId<T>`：`FilteredElementCollector` 按 `typeof(T)` + 名称匹配；`typeName` 缺省取第一个可用类型（仿 `ResolveViewFamilyType` 的排序兜底模式，`src/RevitPlanCreations.cs:523`）。

### 7.2 `query_mep_network`

**契约**：

```json
{ "id": "net_1", "operation": "query_mep_network", "args": {
    "element_id": "$pipe_seed", "max_depth": 100
} }
```

**算法**：沿 `Connector.AllRefs` 广度优先遍历（`ConduitExtension.SelectSystemByConduit` 骨架 + 环 / 深度护栏）：

```csharp
var visited = new HashSet<ElementId>();
var edges = new List<Dictionary<string, object>>();
var queue = new Queue<ElementId>();
queue.Enqueue(seedId);
int depth = 0;
while (queue.Count > 0 && visited.Count < maxDepth)
{
    ElementId currentId = queue.Dequeue();
    if (!visited.Add(currentId)) { continue; }
    Element current = document.GetElement(currentId);
    ConnectorManager manager = GetConnectorManager(current);   // MEPCurve → .ConnectorManager；
                                                              // FamilyInstance → .MEPModel?.ConnectorManager
    if (manager == null) { continue; }
    foreach (Connector connector in manager.Connectors)
    {
        foreach (Connector reference in connector.AllRefs)
        {
            Element owner = reference.Owner;
            if (owner == null || visited.Contains(owner.Id)) { continue; }
            edges.Add(new Dictionary<string, object>
            {
                { "from", currentId.IntegerValue },
                { "to", owner.Id.IntegerValue },
                { "at", PlanValues.PointData(connector.Origin) }
            });
            queue.Enqueue(owner.Id);
        }
    }
}
```

**结果数据结构**：

```json
{
  "seed": 123456,
  "node_count": 17,
  "nodes": [
    { "element_id": 123456, "class": "Pipe", "category": "管道", "system_name": "J2-给水" },
    { "element_id": 123501, "class": "FamilyInstance", "category": "管道配件", "family": "闸阀" }
  ],
  "edges": [ { "from": 123456, "to": 123501, "at": {"x":6000,"y":0,"z":3200} } ]
}
```

`nodes` 元数据在遍历后统一补齐（class / category / `system_name`（BIP `RBS_PIPING_SYSTEM_NAME_PARAM` 族键）），Agent 可直接构建连通图做下游推理。护栏：`max_depth` 默认 100，防止遍历整栋楼管网超时。

### 7.3 `set_element_overrides` + `set_category_overrides`（合并设计）

两个操作共享 `OverrideGraphicSettings` 组装逻辑（SEPD 建议的同族合并），抽公共私有方法：

```csharp
private static OverrideGraphicSettings BuildOverrides(IDictionary<string, object> arguments)
{
    var overrides = new OverrideGraphicSettings();
    Dictionary<string, object> color = PlanValues.Dictionary(arguments, "color", false) as Dictionary<string, object>;
    // 实际取 PlanValues.Get(arguments, "color") 后用 PlanValues.Dictionary 解包 {r,g,b}（0-255）
    object rawColor = PlanValues.Get(arguments, "color", "line_color");
    if (rawColor != null)
    {
        Dictionary<string, object> rgb = PlanValues.Dictionary(rawColor, "color");
        overrides.SetProjectionLineColor(new Color(
            PlanValues.Integer(rgb, 0, "r"), PlanValues.Integer(rgb, 0, "g"), PlanValues.Integer(rgb, 0, "b")));
    }
    int? lineWeight = PlanValues.Get(arguments, "line_weight", "projection_line_weight") == null
        ? (int?)null : PlanValues.Integer(arguments, 6, "line_weight", "projection_line_weight");
    if (lineWeight.HasValue) { overrides.SetProjectionLineWeight(lineWeight.Value); }
    if (PlanValues.Boolean(arguments, false, "halftone")) { overrides.SetHalftone(true); }
    if (PlanValues.Boolean(arguments, false, "visible") == false) { overrides.SetSurfaceTransparency(100); }
    return overrides;
}
```

**契约差异**：

| 操作 | 目标参数 | API 调用 |
| --- | --- | --- |
| `set_element_overrides` | `element_ids` + `view_id`（缺省当前视图） | `view.SetElementOverrides(id, overrides)` |
| `set_category_overrides` | `category`（名称 / BIC） + `view_id` | `view.SetCategoryOverrides(categoryId, overrides)` |

**注意**：透填充图案颜色用 `SetSurfaceForegroundPatternColor`（需同时 `SetSurfaceForegroundPatternId` 指向实体填充，可用 `FillPatternElement` 查找——这与 P3 `manage_graphics_resources` 衔接）。归档：`src/RevitOutputOperations.cs`（图形表现属出图域）。

### 7.4 `manage_view_filters`

**契约**（动作式，吸收 SEPD "合并删除语义"建议）：

```json
{ "id": "filter_1", "operation": "manage_view_filters", "args": {
    "action": "add",
    "view_id": 445566,
    "name": "AI-临警管道",
    "categories": ["管道"],
    "rules": [ { "parameter": "系统缩写", "equals": "J2" } ],
    "overrides": { "color": {"r": 255, "g": 0, "b": 0}, "line_weight": 6, "halftone": false }
} }
```

`action` 枚举：`add`（get-or-create：先用 `FilterRuleExtension.GetFilterByName` 模式查重）| `remove`（从视图摘除 `View.RemoveFilter`）| `delete`（删除 `ParameterFilterElement`）| `clear`（移除视图全部过滤器）。

**实现骨架**（`src/RevitOutputOperations.cs`）：

```csharp
var rules = new List<FilterRule>();
foreach (Dictionary<string, object> ruleSpec in PlanValues.DictionaryList(
    PlanValues.Get(step.Arguments, "rules"), "manage_view_filters.rules"))
{
    string parameterName = PlanValues.String(ruleSpec, null, "parameter");
    ElementId parameterId = FindParameterId(document, categories, parameterName);
    object equalsValue = PlanValues.Get(ruleSpec, "equals", "value");
    rules.Add(ParameterFilterRuleFactory.CreateEqualsRule(parameterId, equalsValue));
}
var elementFilter = new ElementParameterFilter(rules, false);
ParameterFilterElement filter = ParameterFilterElement.Create(document, name, categoryIds, elementFilter);
view.AddFilter(filter.Id);
view.SetFilterVisibility(filter.Id, true);
view.SetFilterOverrides(filter.Id, BuildOverrides(step.Arguments));
```

`FindParameterId`：候选类别 `FilteredElementCollector` 首个元素的 `LookupParameter(parameterName).Id`（BIP `GetElement` 兜底）。**版本注意**：`ParameterFilterRuleFactory.CreateEqualsRule` 在 2020 有效、更高版本逐步弃用——若未来支持包上 2025+，用 `FilterStringRule` / `FilterNumericEquals` 组合并 `#if` 切换。

### 7.5 `query_view_range` / `set_view_range`（SEPD ☆#5）

**`query_view_range` 结果**（视图范围是参数体系之外的特殊对象，无法用 `query_elements.parameters` 组合——A 类规则 ③）：

```json
{ "view_id": 445566, "ranges": {
    "top":            { "level": "F3", "offset_mm": 0 },
    "cut_plane":      { "level": "F2", "offset_mm": 1200 },
    "bottom":         { "level": "F2", "offset_mm": 0 },
    "view_depth":     { "level": "F2", "offset_mm": 0 },
    "top_clip":       null
} }
```

**`set_view_range` 契约**：只需传要改的槽位（缺省槽位保持不变）：

```json
{ "id": "vr_1", "operation": "set_view_range", "args": {
    "view_id": 445566,
    "cut_plane": { "level": "F2", "offset_mm": 1500 },
    "bottom": { "level": "F2", "offset_mm": -300 }
} }
```

**实现骨架**：

```csharp
ViewPlan viewPlan = view as ViewPlan;
if (viewPlan == null) { throw new BridgeCommandException("set_view_range 仅适用于平面视图。"); }
PlanViewRange range = viewPlan.GetViewRange();
foreach (槽位 in 传入的 ranges)
{
    PlanViewRangeType rangeType = ParseRangeType(槽位);   // Top/CutPlane/Bottom/ViewDepth/TopClip...
    if (槽位.level != null) { range.SetLevelId(rangeType, ResolveLevel(槽位.level).Id); }
    if (槽位.offset_mm != null) { range.SetOffset(rangeType, PlanValues.ToFeet(槽位.offset_mm)); }
}
viewPlan.SetViewRange(range);
```

槽位名映射表：`top`→`Top`、`cut_plane`→`CutPlane`、`bottom`→`Bottom`、`view_depth`→`ViewDepth`、`top_clip`→`TopClip`（部分槽位仅剖面视图适用，非法组合抛错）。

### 7.6 `manage_schedule_fields`（SEPD ☆#6）

**契约**（动作式）：

```json
{ "id": "sch_1", "operation": "manage_schedule_fields", "args": {
    "schedule_id": 556677, "action": "add_field",
    "field": { "parameter": "管径", "heading": "DN", "is_instance": true }
} }
```

```json
{ "id": "sch_2", "operation": "manage_schedule_fields", "args": {
    "schedule_id": 556677, "action": "add_filter",
    "filter": { "parameter": "系统缩写", "equals": "J2" }
} }
```

`action` 枚举与 API 映射：

| action | API |
| --- | --- |
| `add_field` | `definition.AddField(ScheduleFieldType.Instance / Element, parameterId)` + `field.ColumnHeading` |
| `remove_field` | `definition.RemoveField(fieldId)`（按 heading / parameter 名定位） |
| `hide_field` / `show_field` | `field.IsHidden = true/false` |
| `add_filter` | `definition.AddFilter(new ScheduleFilter(fieldId, ScheduleFilterType.Equal, value))` |
| `set_itemized` | `definition.IsItemized = bool` |
| `sort` | `definition.AddSortGroupField(new ScheduleSortGroupField(fieldId))` |

**实现要点**：字段定位兜底顺序 = `field_id` → `heading` → `parameter` 名；`ScheduleFilterType.Equal` 的值类型必须与参数存储类型一致（int / double / string / ElementId 四分支），否则 Revit 抛异常——包装成含字段名的中文错误。归档 `src/RevitOutputOperations.cs`。

### 7.7 `manage_schema_data`（SEPD ☆#8）

**定位**：AI 桥接的"私有元数据"通道——给元素打来源会话、Agent 标记等，不污染参数体系。Schema GUID 由桥接固定持有（写入常量），**不开放用户自定义 Schema 结构**（防 GUID 漂移）：

```csharp
internal static class BridgeSchemas
{
    // schema GUID 一经发布不可更改，否则旧数据读不回
    public const string AiMetadataGuid = "8F1D5B2A-6C4E-4E7A-9D3B-1A2B3C4D5E6F";
    public static Schema AiMetadata { get { ... SchemaBuilder 惰性构建 ... } }
}
```

**契约**：

```json
{ "id": "mark_1", "operation": "manage_schema_data", "args": {
    "element_ids": ["$pipes_step"], "action": "set",
    "values": { "ai_source": "kilo-session-42", "ai_step": "pipe_7" }
} }
```

```json
{ "id": "read_1", "operation": "manage_schema_data", "args": {
    "element_id": 123456, "action": "get"
} }
```

**实现骨架**：

```csharp
switch (action)
{
    case "set":
        Entity entity = element.GetEntity(BridgeSchemas.AiMetadata);
        if (!entity.IsValid()) { entity = new Entity(BridgeSchemas.AiMetadata); }
        foreach (键值 in values) { entity.Set(键, 值按字段类型转换); }
        element.SetEntity(entity);
        break;
    case "get":
        Entity stored = element.GetEntity(BridgeSchemas.AiMetadata);
        data["values"] = stored.IsValid()
            ? BridgeSchemas.AiMetadata.ListFields().ToDictionary(field => field.Name, field => stored.Get<object>(field))
            : null;
        break;
    case "clear":
        element.DeleteEntity(BridgeSchemas.AiMetadata);
        break;
}
```

`SchemaTransportBy`（跨元素搬运）模式以 `action: "transport"` + `source_element_id` / `target_element_ids` 实现——读源 Entity 直接 SetEntity 到目标。注意 `element.SetEntity` 需在写事务内（归 `WriteOperations`）。

### 7.8 `create_swept_shape`（SEPD ☆#10）

**契约**：

```json
{ "id": "tunnel_1", "operation": "create_swept_shape", "args": {
    "path": [ { "x": 0, "y": 0, "z": -8000 }, { "x": 50000, "y": 0, "z": -8000 } ],
    "section": { "shape": "horseshoe", "width_mm": 5400, "height_mm": 5500 }
} }
```

`section.shape` 工厂枚举（截面数学留在插件内，来源 `CurveLoopExtension` 系列）：`rect` / `circle` / `horseshoe`（马蹄） / `rect_ring` / `circle_ring`（含 `wall_thickness_mm`）。

**实现骨架**（`src/RevitPlanCreations.cs`；截面工厂独立 `src/RevitSectionFactory.cs`）：

```csharp
CurveLoop sweepPath = BuildPathCurveLoop(step.Arguments);          // path 点列 → CurveLoop
IList<CurveLoop> profiles = new List<CurveLoop> { RevitSectionFactory.CreateSection(sectionSpec) };
Solid solid = GeometryCreationUtilities.CreateSweptGeometry(sweepPath, 0, 0, profiles);
IList<GeometryObject> geometry = new List<GeometryObject> { solid };
DirectShape shape = DirectShape.CreateElement(context.Document, categoryId);
if (!shape.IsValidShape(geometry))
{
    throw new BridgeCommandException("放样几何无效（截面可能自交或与路径平面冲突）。");
}
shape.SetShape(geometry);
```

校验：路径 ≥ 2 点且相邻不共点；截面放置在路径起点法平面（`CreateSweptGeometry` 的 profile location 参数按 2020 API 语义核对）；`horseshoe` 工厂 = 下部矩形 + 上部半圆拼 `CurveLoop`（弧段 `Arc.Create`）。

### 7.9 `create_view` 相机扩展（SEPD ☆#12）

现有 `create_view` 已覆盖 `3d/floor_plan/ceiling_plan/structural_plan` + `perspective`（`src/RevitPlanCreations.cs:288`）；`create_drafting_view` 已存在。扩展仅补**相机朝向**：

```json
{ "id": "cam_1", "operation": "create_view", "args": {
    "kind": "3d", "name": "机房视角-1",
    "eye": { "x": 20000, "y": -15000, "z": 8000 },
    "forward": { "x": -0.6, "y": 0.7, "z": -0.2 }, "up": { "x": 0, "y": 0, "z": 1 }
} }
```

```csharp
if (family == ViewFamily.ThreeDimensional && eye != null)
{
    View3D view3D = (View3D)view;
    XYZ eyePoint = PlanValues.Point(step.Arguments, "eye");
    XYZ forward = PlanValues.Point(step.Arguments, "forward").Normalize();
    XYZ up = PlanValues.Point(step.Arguments, "up").Normalize();
    view3D.SetOrientation(new ViewOrientation3D(eyePoint, forward, up));
}
```

校验：`forward` 与 `up` 点积 ≈ 0（不正交报错）；缺省 `up` 取 Z 轴。

### 7.10 `manage_family_parameters`

**契约**（动作式，跨文档事务）：

```json
{ "id": "fam_1", "operation": "manage_family_parameters", "args": {
    "family_id": 778899,
    "actions": [
        { "action": "add", "name": "接口口径", "type": "length", "is_instance": false, "group": "PG_MECHANICAL" },
        { "action": "set_formula", "name": "接口口径", "formula": "管径 + 2 * 壁厚" },
        { "action": "rename", "name": "备注", "new_name": "AI备注" },
        { "action": "remove", "name": "临时参数" }
    ]
} }
```

**实现骨架**（`src/RevitPlanMutations.cs`；流程 = `EditFamily` → 族文档独立事务 → `LoadFamily` 回写）：

```csharp
Family family = context.Document.GetElement(familyId) as Family;
if (family == null) { throw new BridgeCommandException("manage_family_parameters.family_id 必须指向族。"); }
Document familyDoc = context.Document.EditFamily(family);
using (Transaction familyTransaction = new Transaction(familyDoc, "RCB manage_family_parameters"))
{
    familyTransaction.Start();
    FamilyManager manager = familyDoc.FamilyManager;
    foreach (动作 in actions)
    {
        switch (动作.action)
        {
            case "add":
                FamilyParameter added = manager.AddParameter(
                    动作.name, ParseGroup(动作.group), ParseParameterType(动作.type), 动作.is_instance);
                break;
            case "rename":
                manager.RenameParameter(manager.get_Parameter(动作.name), 动作.new_name);
                break;
            case "remove":
                manager.RemoveParameter(manager.get_Parameter(动作.name));
                break;
            case "set_formula":
                manager.get_Parameter(动作.name).Formula = 动作.formula;
                break;
        }
    }
    familyTransaction.Commit();
}
familyDoc.LoadFamily(context.Document, new BridgeFamilyLoadOptions());
```

**版本断代**：`FamilyManager.AddParameter` 第三参 2020 为 `ParameterType` 枚举、2021+ 为 `ForgeTypeId`——`ParseParameterType` 内部 `#if REVIT_FORGE_UNITS` 切换。**事务嵌套风险**（§11.6）：族文档事务与宿主计划事务并行打开，回载对话框由 `BridgeFamilyLoadOptions` 静默——实现前先做真机 POC（先改无参数族，再改被实例引用的族），记录行为后定稿。

## 8. P3 详细设计

### 8.1 `manage_project_parameters`（SEPD ☆#7）

项目 / 共享参数完整 CRUD。**前置条件**：项目须已配置共享参数文件（`application.SharedParametersFilename`），否则报错提示（路径由用户在 Revit 设置，桥接不代管——避免写盘副作用）。

**动作映射**：

| action | 2020 API（ForgeTypeId 断代前的旧体系） |
| --- | --- |
| `add_shared`（定义 + 绑定） | `definitionFile.Groups.Create(name)` → `group.CreateExternalDefinition(name, ParameterType.XXX)` → `application.Create.NewCategorySet/Insert` → `NewTypeBinding / NewInstanceBinding` → `document.ParameterBindings.Insert(definition, binding, group)` |
| `delete` | `document.ParameterBindings.Remove(definition)` |
| `list`（只读辅助，实为 query 域） | 遍历 `Category.BoundParameters` / `DefinitionBindingMapForUI` |

**版本断代重灾区**：`ParameterType` 枚举（2020）vs `ForgeTypeId/SpecTypeId`（2021+）——所有 spec 转换集中在独立 `src/RevitSpecMap.cs`，`#if REVIT_FORGE_UNITS` 双实现，禁止散落。建议此操作落地前先真机确认 2020 的绑定行为（绑定后参数立即出现在既有元素上）。

### 8.2 `manage_graphics_resources`（SEPD ☆#13）

get-or-create 语义的线型 / 填充样式工具（低优先，也可并入未来标注 / 填充区域操作的参数）：

```csharp
// 线型：类别线集合内按名查找，缺省用 CurveElement 创建法（在 drafting 视图画线设线样式再删线）
// 填充：FilteredElementCollector.OfClass(FillPatternElement) 按名匹配；缺省 FillPattern.Create + Name 赋值
```

`LineStyleExtention.GetOrCreateLineStyle` 的"画线改样式再删线"技巧是 Revit 无直接线型创建 API 的标准绕行——移植时注意必须发生在事务内且失败预处理器在位（删除临时线可能触发警告）。

## 9. 参考实现资产：sepd-revit-extension 共享库

已分析 `R:\_CODE_\REVIT\sepd-revit-extension\Common.Revit.Extension.Shared\`（作者署名 Haotian Zhou 周昊天）。该库是生产验证过的 Revit 扩展方法集，以下资产可直接作为路线图项的参考实现（模式参考，非直接复制，见 9.3 版权说明）。

该库全部 46 个文件、约 300 个 public 用法的"原子 vs 可组合"逐项分类见 [SEPD-ATOMIC-ANALYSIS.md](./SEPD-ATOMIC-ANALYSIS.md)（结论：18 项需原子化——其中 5 项已在路线图、13 项已合并入 §3 路线图；其余约 88% 可组合或不适用）。

### 9.1 资产 → 路线图项映射

| 库文件 | 可复用内容 | 服务的路线图项 |
| --- | --- | --- |
| `ConnectorExtension.cs` | 连接件最近配对：`GetNearConnectors`（两组 ConnectorSet 最近点对）、`GetNearConnector`、`CloseConnectorToPoint`；`GetRefsByConnector`（找对端连接件）；`GetConnectorByDescription` | §5.2 `connect_mep` reducer/cross——多口配件必须先解决"哪两个口相连"的匹配问题；§7.2 `query_mep_network` |
| `MEPCurveExtension.cs` | `ConnectMEPCurveElbowFitting`：两管延伸求交 → 裁剪 `LocationCurve` → `Regenerate()` → 交点处找端连接件 → `NewElbowFitting`；`ConnectMEPCurveTeeFitting`（双管 / 三管两版）：最近连接件搜索 → `NewTeeFitting` | §5.2 `extend_to_intersection` 的完整实现范式 |
| `ConduitExtension.cs` | 线管版弯头 / 三通（与 MEPCurve 版同构）；`SelectSystemByConduit`：沿 `Connector.AllRefs` 遍历管网找同系统管线 | §7.2 `query_mep_network` 的遍历骨架（§7.2 代码即按此重写） |
| `FailureProcessor.cs` | `ContinueFailureProcessor`（`IFailuresPreprocessor`）：Error 自动尝试 `ResolveFailure`、Warning 直接 `DeleteWarning`；`MyFailuresPreProcessor`：按错误文本匹配指定解决方案 | §5.4 失败预处理器——无人值守队列不被模态警告卡死的关键 |
| `DocumentExtension.cs` | `GetLinkDocs` / `GetRevitLinkInstances`（按关键字过滤链接模型）；`LoadFamilyBy / GetOrLoadSymbolByName` 系 | §6.4 `query_catalog(links)`；§6.1 跨模型碰撞前置；§6.7 `load_family` 的 get-or-load 模式 |
| `GeometryExtension.cs` | `GetSolidByElement` / `GetFaceByElement`（含 `GeometryInstance` 展开） | §6.8 `query_geometry`；§6.1 `check_interferences` 跨链接几何求交兜底 |
| `FloorExtension.cs` | `GetFloorBoundaryPolygon`：几何法提取楼板最低面边界（含洞口环）；`FloorGeoTH` 楼板顶标高 | §5.1 洞口 / 竖井放置校验；读取既有楼板开洞 |
| `FilterRuleExtension.cs` | `HasFilterWithName` / `GetFilterByName`（视图与文档两级过滤器查找）；`FastIntersectWith / SolidIntersectWith / FaceIntersect` | §7.4 `manage_view_filters` 的 get-or-create 查重；§6.1 求交实现 |
| `UnitConverter.cs` | mm/m ↔ ft 全套换算（含 `XYZ`/`UV`、面积、体积、角度） | 单位换算参考（注意版本，见 9.2） |
| `TransformExtension.cs` | `LocalToWorldBy` / `WorldToLocalBy`（族实例局部 ↔ 世界坐标，Z 轴旋转）；最小欧拉旋转矩阵 | §5.3 `transform_elements` 的坐标换算辅助；§6.8 族实例几何展开 |
| `RoomExtension.cs` | `GetRoomBoundaryPoints / GetRoomByPoint` | §6.10 `query_room` |
| `ViewExtension.cs` | `GetViewRange*` 系列、`SetCategoryColor / SetCategoryLineWeight` 系列、`DuplicateByDetail` | §7.5 视图范围；§7.3 类别覆盖；§6.11 复制选项 |
| `ScheduleExtension.cs` | `AddFieldToSchedule / SetFilter / SetTitleColumnHeadText` | §7.6 `manage_schedule_fields` |
| `SchemaExtension.cs` | `SchemaInit / SetSchemaData / SchemaTransportBy` | §7.7 `manage_schema_data`（transport 模式亮点） |
| `CurveLoopExtension.cs` / `SweepExtension.cs` | 截面轮廓工厂（矩形 / 圆 / 马蹄 / 环）、`CreateSweptShape` | §7.8 `create_swept_shape` |
| `SharedParameterExtention.cs` / `ParamterExtension.cs` / `FamilyExtension.cs` | 共享参数绑定、参数 CRUD、族改名 / 几何提取 | §8.1 `manage_project_parameters`；§6.6 `rename_element`（`RenameByPrefixId` 批量模式）；§7.10 `manage_family_parameters` |
| `CommonExternalEventHandler.cs` | 单例 `Action<UIApplication>` 队列式外部事件 | 参考；桥接已有队列驱动版 `BridgeEventHandler`，无需替换 |
| 其余（`XyzExtension` / `CurveExtension` / `PlaneExtension` / `XyzExtension` 几何数学、`TextExtension` 等） | 纯几何 / 字符串计算 | SEPD 分析定级 B/C 类：客户端可算或插件内部工具，不新增原子 |

### 9.2 版本兼容警示（重要）

| 事项 | 说明 |
| --- | --- |
| 目标版本不同 | 共享库面向 **Revit 2024–2026**（`UnitConverter.cs` 头部注明"仅支持 Revit 2024~2026"）；桥接支持 **2020–2024**。两者交集仅 2024 |
| 单位 API 断代 | 库中 `UnitTypeId` / `ForgeTypeId` / `SpecTypeId` 是 Revit **2021+** API；Revit 2020 需 `DisplayUnitType.DUT_*` 旧体系。桥接 2020 适配包应继续用自有的 `FeetPerMillimeter` 常量（`src/RevitCommandExecutor.cs`），不要直接搬 `UnitConverter`。仓库已有 `#if REVIT_FORGE_UNITS` 先例（`src/RevitLookups.cs:261`），所有断代点集中走该符号 |
| 条件编译先例 | 库内 `FilterRuleExtension.cs` 使用 `#if RLS_REVIT_2026` 按年份切换实现——与桥接 `REVIT_FORGE_UNITS` 模式互证：一份源码 + 编译符号，替代按年份维护分支 |
| 移植原则 | 只取**算法模式**（连接件配对、延伸求交、AllRefs 遍历、失败预处理流程），在桥接代码风格（`PlanValues` 取参、`BridgeCommandException` 报错、mm 默认单位）下重写 |

### 9.3 版权与署名

共享库每个文件头部有作者署名（Haotian Zhou 周昊天）。若逐段复制代码，须先确认该库许可证与桥接 [LICENSE](../LICENSE) / [NOTICE.md](../NOTICE.md) 兼容并在 NOTICE 中补署名；仅参考算法模式重写则无此约束。

## 10. 新增一个原子操作的固定流程（6 处落点）

以 `transform_elements` 为例，每处给出精确落点与示例 diff：

### 10.1 登记白名单

`src/PlanCommandExecutor.cs` 的 `AtomicOperations` 数组（`src/PlanCommandExecutor.cs:19`）按字母序插入：

```csharp
"select_elements",
"transform_elements",   // ← 新增
"export",
```

### 10.2 声明事务类别

写模型操作加入 `WriteOperations` 集合（自动获得 all-or-nothing 事务与 preview 状态）；只读感知型（如 `check_interferences` / `query_parameters`）两个集合都不进；有外部文件副作用的进 `ExternalOperations`（强制单独成计划）：

```csharp
"delete_elements",
"transform_elements",   // ← 写操作，进 WriteOperations
```

### 10.3 注册分发

`src/RevitPlanOperations.cs` 的 `Execute` switch 加 case：

```csharp
case "transform_elements":
    return RevitPlanMutations.TransformElements(step, context);
```

### 10.4 写实现

按类别放入对应文件——修改类 `src/RevitPlanMutations.cs`、创建类 `src/RevitPlanCreations.cs`、查询类 `src/RevitPlanQueries.cs`、出图类 `src/RevitOutputOperations.cs`。实现时复用现成设施：

- `context.ResolveElementIds()` / `ResolveSingleElementId()` 解析 `"$步骤ID"` 引用；
- `PlanValues` / `BridgeArguments` 取参（§4.3）；
- `FeetPerMillimeter`（`src/RevitCommandExecutor.cs`）做 mm → feet 换算；
- 骨架照抄 `CreateMepCurve`（`src/RevitPlanCreations.cs:645`）/ `ConnectMep`（`:742`）；
- 涉及连接件匹配时，参考 `ConnectorExtension.GetNearConnectors` 的最近配对模式重写（§5.2）；
- preview 分流 + deferred 模式照 §4.1 / §4.2。

### 10.5 更新契约

`schemas/execute-plan.schema.json` 的 operation enum 加名字；`NormalizeAtomicOperation()` 加中文别名（§4.4 对照表）：

```csharp
case "变换元素":
case "移动元素": return "transform_elements";
```

### 10.6 文档 + 编译

[PROTOCOL.md](../PROTOCOL.md) 操作表加一行（含参数表与 preview 行为说明）；`build.ps1 -RevitVersion <year>` 按年份重编译。MCP 端零改动——`steps[].operation` 在 MCP schema 中是自由字符串，守门员是插件白名单。

## 11. 注意事项

1. **API 年份差异**：每个 Revit 年份单独编译，新调用的 API 必须存在于最老支持版本（2020）的 RevitAPI.dll；参考共享库代码时按 9.2 的版本断代表换算。多年份差异一律用 `#if REVIT_FORGE_UNITS`（或新增年份符号）条件编译，禁止维护年份分支。本文档标记的"真机核对"点（§5.1 NewVerticalOpening 语义、§5.3 MirrorElements 重载、§7.10 族文档事务嵌套、§8.1 2020 绑定行为）必须在写实现前完成。
2. **预览语义**：写操作在 `preview=true` 时不真正执行（`PlanCommandExecutor.Execute` 分流），实现方法在 preview 下返回"将要做什么"（§4.1 封套），参考现有创建类操作。
3. **失败处理**：P0 完成失败预处理器后，新写操作遇到可自动解决的 Error（如"连接件不匹配"）由预处理器统一处置，错误文本进入结果 JSON `failure_messages`，不弹模态框。
4. **回归记录**：新操作完成真机验证后，按 `verification/` 目录惯例补一份回归记录，保持 `[V]` / `[T]` 证据文化。
5. **命名规范**：操作名用小写下划线（`transform_elements`）；查询用 `query_` / `check_` 前缀，创建用 `create_`，修改用 `set_` / `transform_` / `delete_` / `rename_` / `manage_`，与现有命名一致。动作式操作（`manage_*`）的 `action` 枚举值统一用 `snake_case` 单词（`add_field`、`add_filter`）。
6. **族文档事务**：`manage_family_parameters` 等涉及 `Document.EditFamily` 的操作，族文档事务须独立于宿主计划事务提交，改动需 `LoadFamily` 回写并由 `BridgeFamilyLoadOptions` 静默处理"覆盖族及参数"确认（§6.7）——实现前先在真机验证事务嵌套与回载对话框行为。
7. **只读操作防误写**：`check_interferences` / `query_parameters` / `query_geometry` / `query_room` / `query_mep_network` 等只读原子的实现里禁止出现任何 `Transaction`、`Set` 赋值、`Delete` 调用；几何读取用 `Options { ComputeReferences = false }` 降低成本。
8. **性能护栏**：`query_geometry` / `check_interferences` / `query_mep_network` 均需数量上限参数（`max_depth` / 候选数上限），超限报中文错误而非超时。
9. **新助手集中管理**：§4.3 的 `PlanValues.ToRadians` / `AngleDegrees`、§5.1 的 `BuildCurveLoop`、§5.2 的 `FindNearConnectorPair`、§6.7 的 `BridgeFamilyLoadOptions`、§7.8 的 `RevitSectionFactory` 均为一次性新增的公共设施，放进对应既有文件（`PlanValues.cs` / `RevitPlanCreations.cs`）或最小新文件，避免散落。

## 12. 验收标准

每个新操作合入前须满足：

- [ ] 白名单、事务类别、分发 case、实现、schema、PROTOCOL.md 六处同步更新（§10 流程）；
- [ ] `preview=true` 返回计划描述且不修改模型；依赖前置 ID 的操作实现 deferred 模式（§4.2）；
- [ ] `preview=false` 真机执行成功，失败时整个计划回滚（写操作）；
- [ ] `"$步骤ID"` 引用可用（涉及元素目标的操作）；返回新元素的（copy / mirror / load_family）`element_ids` 指向新元素；
- [ ] 长度参数支持裸数（mm）与带单位字符串（如 `"3.6m"`）；角度参数契约统一为度；
- [ ] 只读操作在无写事务路径下验证（`transaction_mode: read_only`）；
- [ ] 中文别名已加入 `NormalizeAtomicOperation`；
- [ ] 在 Revit 2020 真机完成回归并记录到 `verification/`；涉及版本断代的操作在 2024 再编译一次确认 `#if` 分支；
- [ ] 若参考 / 移植 sepd-revit-extension 代码，已按 9.3 确认许可并补署名；
- [ ] 动作式操作（`manage_*`）的非法 `action` / 非法参数组合给出含参数名的中文错误；
- [ ] 失败预处理器在位后：构造的警告场景不弹模态框，`failure_messages` 出现在结果 JSON。

批次级验收（每完成一个 P 批次）：

- [ ] 该批次操作可串联成一个真实深化场景计划（如 P0：楼板洞口 + 变径 + 四通 + 管段旋转 + 无警告提交），端到端走通并归档场景 JSON 到 `verification/`。
