# Revit API 版本差异统一出口分析

> **版本边界总结**：
> - Revit 2020–2021: `ParameterType` 枚举 / `BuiltInParameterGroup` 枚举 / `DisplayUnitType` 枚举
> - Revit 2022+: ForgeTypeId (`SpecTypeId`, `GroupTypeId`, `GetUnitTypeId()`) 引入
> - Revit 2024+: `ElementId.Value` 返回值变为 `long`
> - Revit 2025+: `Floor.Create()` 等静态工厂方法引入

---

## 0. 工程现状（2026-08）

| 项目 | 路径 | 特点 |
|------|------|------|
| **新工程** | `revit-mcp-local-bridge/` | 统一 csproj，支持 R20~R26 全部配置，Nice3point NuGet 引用 |
| **旧工程（参考）** | `revit-mcp-local-bridge-old/` | 独立 PowerShell 编译，`#if REVIT_FORGE_UNITS` + 反射，无 csproj |

### 新工程编译配置

| 配置 | 框架 | Revit API 版本 | 条件编译符号 |
|------|------|----------------|-------------|
| `Debug R20` / `Release R20` | `net48` | 2020 | 无 |
| `Debug R21` / `Release R21` | `net48` | 2021 | 无 |
| `Debug R22` / `Release R22` | `net48` | 2022 | `REVIT2022_OR_GREATER` |
| `Debug R23` / `Release R23` | `net48` | 2023 | `+REVIT2023_OR_GREATER` |
| `Debug R24` / `Release R24` | `net48` | 2024 | `+REVIT2024_OR_GREATER` |
| `Debug R25` / `Release R25` | `net8.0-windows` | 2025 | `+REVIT2024_OR_GREATER` |
| `Debug R26` / `Release R26` | `net8.0-windows` | 2026 | `+REVIT2024_OR_GREATER` |

Adapter 入口文件位于 `src/Adapter/AdapterEntry{20..26}.cs`，每个编译配置只编译对应的一个入口。

Revit API 引用通过 `Nice3point.Revit.Api.RevitAPI` / `RevitAPIUI` NuGet 包自动获取对应版本 DLL，不再依赖本地 `depandency/` 目录。

---

## 1. ElementId 值访问 — 统一 `long` 出口

| 版本 | API | 返回类型 |
|------|-----|---------|
| ≤2023 | `id.IntegerValue` | `int` |
| ≥2024 | `id.Value` | `long` |

**策略**：对外只暴露 `long`，旧版本 `int` 在方法内部自动提升为 `long`。  
调用方统一使用 `long`，无需关心版本差异。需 `int` 时自行 `(int)id.GetValue()` 转型。

### 方案：单个扩展方法 + `#if`

```csharp
// src/Utils/RevitApiExtensions.cs
#if REVIT2024_OR_GREATER
public static long GetValue(this ElementId id) => id.Value;        // long
#else
public static long GetValue(this ElementId id) => id.IntegerValue; // int 隐式提升为 long
#endif
```

### 替换规则

| 原写法 | 替换为 | 说明 |
|--------|--------|------|
| `id.IntegerValue` / `id.Value` | `id.GetValue()` | 统一返回 long |
| `(int)id.Value` | `(int)id.GetValue()` | 需要 int 时调用方自己转型 |
| `id.Value == InvalidElementId.Value` | `id == ElementId.InvalidElementId` | 直接比较 ElementId 引用 |
| `Math.Min(id1.Value, id2.Value)` | `Math.Min(id1.GetValue(), id2.GetValue())` | long 计算 |
| `ids.Select(id => id.Value).ToArray()` | `ids.Select(id => id.GetValue()).ToArray()` | 返回 long[] |
| `.OrderBy(e => e.Id.Value)` | `.OrderBy(e => e.Id.GetValue())` | 排序 |

**注意**：`id.Value == ElementId.InvalidElementId.Value` 应改为 `id == ElementId.InvalidElementId`（直接比较 ElementId 引用），语义更清晰且避免 int/long 转换问题。

### 涉及文件

`RevitPlanCreations.cs` / `RevitPlanMutations.cs` / `RevitPlanQueries.cs`  
`RevitOutputOperations.cs` / `RevitLookups.cs`  
`RevitFamilyOperations.cs` / `RevitCommandExecutor.cs`  
`PlanCommandExecutor.cs`

---

## 2. FamilyManager.AddParameter — 族参数创建

| 版本 | API 签名 |
|------|----------|
| ≤2021 | `manager.AddParameter(name, BuiltInParameterGroup, ParameterType, isInstance)` |
| ≥2022 | `manager.AddParameter(name, ForgeTypeId, ForgeTypeId, isInstance)` |

### 方案：辅助方法 + `#if`（API 签名不同，需编译时选择）

```csharp
#if REVIT2022_OR_GREATER
public static FamilyParameter AddParameter(
    this FamilyManager manager, string name,
    string groupToken, string specToken, bool isInstance)
{
    return manager.AddParameter(name,
        GroupTypeIdFromToken(groupToken),
        SpecTypeIdFromToken(specToken),
        isInstance);
}
#else
public static FamilyParameter AddParameter(
    this FamilyManager manager, string name,
    string groupToken, string specToken, bool isInstance)
{
    return manager.AddParameter(name,
        BuiltInParameterGroupFromToken(groupToken),
        ParameterTypeFromToken(specToken),
        isInstance);
}
#endif
```

**现状**：
- 新工程 R22+ 编译时直接使用 `SpecTypeId`/`GroupTypeId`（`RevitParameterAdmin.cs:98-131`）
- 旧工程使用 `#if REVIT_FORGE_UNITS` + 反射（因当时无编译期引用）
- 统一后应通过扩展方法隐藏版本差异，调用方只需传字符串 token

---

## 3. 参数类型规格 — SpecTypeId vs ParameterType

| 版本 | 解析方式 |
|------|---------|
| ≤2021 | `ParameterType.Text`, `ParameterType.Length`, `ParameterType.YesNo` 等枚举 |
| ≥2022 | `SpecTypeId.Length`, `SpecTypeId.String.Text`, `SpecTypeId.Boolean.YesNo` 等 |

### 方案：`RevitParameterResolver.cs` 统一字符串映射 + `#if`

```csharp
public static object ResolveSpec(string token)
{
#if REVIT2022_OR_GREATER
    return token switch { "length" => SpecTypeId.Length, ... };
#else
    return token switch { "length" => ParameterType.Length, ... };
#endif
}
```

**涉及文件**：
- 旧工程：`RevitFamilyOperations.cs:692-725`（`ParseParameterType`, `ParseBuiltInParameterGroup`）
- 新工程：`RevitParameterAdmin.cs:94-131`（`ResolveSpec`, `ResolveGroup`）

---

## 4. 显示单位类型 — DisplayUnitType vs GetUnitTypeId

| 版本 | API |
|------|-----|
| ≤2021 | `parameter.DisplayUnitType.ToString()` (返回 `DisplayUnitType` 枚举名) |
| ≥2022 | `parameter.GetUnitTypeId().TypeId` (返回 `ForgeTypeId` 标识串) |

### 方案：统一辅助方法 + `#if`

```csharp
public static string GetUnitTypeIdString(this Parameter parameter)
{
#if REVIT2022_OR_GREATER
    return parameter.GetUnitTypeId()?.TypeId;
#else
    return parameter.DisplayUnitType.ToString();
#endif
}
```

**调用处**：`RevitLookups.cs:ParameterData()` — 两项目都有

---

## 5. Floor.Create — NewFloor vs Floor.Create 静态工厂

| 版本 | API |
|------|-----|
| ≤2023 | `doc.Create.NewFloor(curveArray, floorType, level, structural)` |
| ≥2024 | `Floor.Create(doc, curveLoops, floorTypeId, levelId, structural, null, 0.0)` |

### 方案：辅助方法 + `#if`

```csharp
#if REVIT2024_OR_GREATER
public static Floor CreateFloor(this Document doc, CurveArray profile,
    ElementId floorTypeId, ElementId levelId, bool structural, double offset)
{
    var loops = new CurveLoop();
    foreach (Curve c in profile) loops.Append(c);
    return Floor.Create(doc, new[] { loops }, floorTypeId, levelId, structural, null, offset);
}
#else
public static Floor CreateFloor(this Document doc, CurveArray profile,
    ElementId floorTypeId, ElementId levelId, bool structural, double offset)
{
    FloorType floorType = doc.GetElement(floorTypeId) as FloorType;
    Level level = doc.GetElement(levelId) as Level;
    return doc.Create.NewFloor(profile, floorType, level, structural);
}
#endif
```

**涉及文件**：`RevitPlanCreations.cs`（旧:176 `NewFloor` / 新:204 `Floor.Create`）

---

## 6. 其他 doc.Create.New* 方法（未来观察点）

以下在新旧项目中**当前保持一致**，但 Revit 后续版本可能迁移到静态工厂：

| 方法 | 代码位置 | 未来风险 |
|------|---------|---------|
| `doc.Create.NewRoom` | RevitPlanCreations.cs | 低 |
| `doc.Create.NewSpace` | RevitPlanCreations.cs | 低 |
| `doc.Create.NewModelCurve` | RevitPlanCreations.cs | 低 |
| `doc.Create.NewOpening(wall, s, e)` | RevitPlanCreations.cs | 中 |
| `doc.Create.NewOpening(floor, curves)` | RevitPlanCreations.cs (垂直洞口) | 中 — 可能改为 `Opening.CreateVertical` |
| `doc.Create.NewDetailCurve` | RevitOutputOperations.cs | 低 |
| `doc.Create.NewDimension` | RevitOutputOperations.cs | 低 |
| `doc.Create.NewFamilyInstance` (9+ overloads) | RevitPlanCreations.cs | **高** — 大量重载，未来可能迁移 |
| `doc.Create.NewFitting` (Elbow/Tee/Cross/Transition) | RevitPlanCreations.cs | 低 |

**建议**：暂时不动，后续版本迁移时在 `RevitElementFactory` 中逐步添加包装。

---

## 7. `#if` vs 统一方法 选择指南

| 差异类型 | 案例 | 推荐方案 | 理由 |
|---------|------|---------|------|
| **类型变化** | `ElementId.Value`: `int` → `long`（内部消化） | **`#if` 在统一方法内，对外只暴露 `int`** | 调用方不感知 long，所有转型在方法内部完成 |
| **签名不同** | `AddParameter` 不同重载 | **`#if` 在辅助方法内** | 入参类型完全不同 |
| **枚举→类** | `ParameterType` → `SpecTypeId` | **`#if` + 返回 `object`** | 或返回通用标识字符串 |
| **方法名不同** | `DisplayUnitType` → `GetUnitTypeId` | **统一方法名包装** | 语义一致，内部 `#if` |
| **行为变化** | `NewFloor` → `Floor.Create` | **统一工厂方法 + `#if`** | 入参和构建逻辑不同 |
| **纯映射逻辑** | 字符串 `"length"` → 各版本类型 | **统一 Resolve 类** | 映射逻辑相同，仅返回类型不同 |

---

## 8. 总体架构建议

```
src/
├── *.cs                              ← 共享业务逻辑（不变）
├── Adapter/
│   ├── AdapterEntry20.cs
│   ├── AdapterEntry21.cs
│   ├── ...                           ← 版本适配入口（IExternalApplication）
│   └── AdapterEntry26.cs
└── Utils/                            ← NEW: 版本差异统一出口
    ├── RevitApiExtensions.cs          ← ElementId.GetValue/.GetIntValue + 通用辅助方法
    ├── RevitParameterResolver.cs      ← Spec/Group 字符串映射（含 #if）
    └── RevitElementFactory.cs         ← 元素创建工厂（Floor.Create 等）

RevitCommandBridge.csproj             ← 统一项目文件，14 个配置 (Debug/Release R20~R26)
```

---

## 9. 完整差异清单

| # | API 差异 | 分界版本 | 旧写法 (≤分界) | 新写法 (≥分界) | 推荐出口 |
|---|---------|---------|---------------|---------------|---------|
| 1 | `ElementId` 值获取 | Revit 2024 | `id.IntegerValue` | `id.Value` (long) | `id.GetValue()` (统一 long) |
| 2 | 族参数添加签名 | Revit 2022 | `AddParameter(n, BIPG, PT, b)` | `AddParameter(n, FGT, FGT, b)` | 扩展方法 + `#if` |
| 3 | 参数类型规格 | Revit 2022 | `ParameterType.Length` | `SpecTypeId.Length` | `ResolveSpec(token)` + `#if` |
| 4 | 参数分组规格 | Revit 2022 | `BuiltInParameterGroup.PG_*` | `GroupTypeId.*` | `ResolveGroup(token)` + `#if` |
| 5 | 显示单位类型 | Revit 2022 | `parameter.DisplayUnitType` | `parameter.GetUnitTypeId()` | `GetUnitTypeIdString()` + `#if` |
| 6 | Floor 创建 | Revit 2024 | `doc.Create.NewFloor(...)` | `Floor.Create(...)` | 工厂方法 + `#if` |
| 7 | doc.Create.New* 系列 | 持续演进 | `doc.Create.NewRoom` 等 | 静态工厂 (未来) | 暂不动，设观察点 |
| 8 | Opening 垂直/竖井 | Revit 2025+ | `doc.Create.NewOpening` | `Opening.CreateVertical` | 未来 `#if` 扩展 |
| 9 | `Category.GetCategory` | 无变化 | `Category.GetCategory(doc, id)` | 相同 | 无需处理 |
| 10 | 共享参数 API | 无变化 | `app.SharedParametersFilename` | 相同 | 无需处理 |

---

## 10. 实施优先级

| 优先级 | 差异 | 修改量 | 影响面 |
|--------|------|--------|--------|
| **P0** | ElementId `.GetIntValue()` 统一方法（对外 int，内部消化 long） | 新建 1 文件，修改 ~230 处 | 全部文件，数据类型正确性 |
| **P1** | `RevitParameterResolver.cs` 统一 Spec/Group 解析 | 新建 1 文件，修改 ~30 处 | RevitParameterAdmin, RevitFamilyOperations |
| **P1** | `GetUnitTypeIdString()` 统一方法 | 新建 1 行，修改 1 处 | RevitLookups.cs |
| **P2** | `CreateFloor()` 工厂方法 | 新建方法，修改 1 处 | RevitPlanCreations.cs |
| **P3** | doc.Create.New* 观察/预留 | 0 处 | 未来兼容性预留 |
