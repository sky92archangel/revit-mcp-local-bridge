# Revit API 版本差异统一出口分析

> **版本边界总结**：
> - Revit 2020–2021: `ParameterType` 枚举 / `BuiltInParameterGroup` 枚举 / `DisplayUnitType` 枚举
> - Revit 2022+: ForgeTypeId (`SpecTypeId`, `GroupTypeId`, `GetUnitTypeId()`) 引入
> - Revit 2024+: `ElementId.Value` 返回值变为 `long`
> - Revit 2025+: `Floor.Create()` 等静态工厂方法引入

---

## 0. 工程现状（2026-08）

本工程（`revit-mcp-local-bridge/`）使用统一 `.csproj` + Nice3point NuGet，支持 R20~R26 全部 14 个配置。旧工程已废弃（`revit-mcp-local-bridge-old/`，独立 PowerShell 编译 + `#if REVIT_FORGE_UNITS`）。

### 编译配置

| 配置 | 框架 | Revit API 版本 | 条件编译符号 |
|------|------|----------------|-------------|
| `Debug R20` / `Release R20` | `net48` | 2020 | 无 |
| `Debug R21` / `Release R21` | `net48` | 2021 | 无 |
| `Debug R22` / `Release R22` | `net48` | 2022 | `REVIT2022_OR_GREATER` |
| `Debug R23` / `Release R23` | `net48` | 2023 | `+REVIT2023_OR_GREATER` |
| `Debug R24` / `Release R24` | `net48` | 2024 | `+REVIT2024_OR_GREATER` |
| `Debug R25` / `Release R25` | `net8.0-windows` | 2025 | `+REVIT2025_OR_GREATER` |
| `Debug R26` / `Release R26` | `net8.0-windows` | 2026 | `+REVIT2025_OR_GREATER` |

Adapter 入口文件位于 `src/Adapter/AdapterEntry{20..26}.cs`（另有 `AdapterEntry27.cs` 代码已完成但未在 `.csproj` 中配置编译），每个编译配置只编译对应的一个入口。

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
- R22+ 编译时直接使用 `SpecTypeId`/`GroupTypeId`（`RevitParameterAdmin.cs:98-131`，`#if REVIT2022_OR_GREATER` 切换）
- 通过扩展方法隐藏版本差异，调用方只需传字符串 token

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
- `RevitParameterAdmin.cs:94-131`（`ResolveSpec`, `ResolveGroup`）
- `RevitFamilyOperations.cs:692-725`（`ParseParameterType`, `ParseBuiltInParameterGroup`，反射回退）

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

**调用处**：`RevitLookups.cs:ParameterData()` — `#if REVIT2022_OR_GREATER` 切换

---

## 5. Floor.Create — NewFloor vs Floor.Create 静态工厂

| 版本 | API |
|------|-----|
| ≤2021 | `doc.Create.NewFloor(curveArray, floorType, level, structural)` |
| ≥2022 | `Floor.Create(doc, curveLoops, floorTypeId, levelId, structural, null, 0.0)` |

### 方案：辅助方法 + `#if`（已实施）

`RevitApiExtensions.cs:22-32` 已实现统一扩展方法：

```csharp
public static Floor CreateFloor(Document doc, CurveArray profile,
    FloorType floorType, Level level, bool structural)
{
#if REVIT2022_OR_GREATER
    var loop = new CurveLoop();
    foreach (Curve c in profile) loop.Append(c);
    return Floor.Create(doc, new[] { loop }, floorType.Id, level.Id, structural, null, 0.0);
#else
    return doc.Create.NewFloor(profile, floorType, level, structural);
#endif
}
```

**调用处**：`RevitPlanCreations.cs` 调用 `RevitApiExtensions.CreateFloor(context.Document, profile, floorType, level, structural)`，无 `#if` 暴露。

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
├── *.cs                              ← 共享业务逻辑（32 个文件）
├── Adapter/
│   ├── AdapterEntry20.cs .. 27.cs     ← 版本适配入口（IExternalApplication）
│                                       每个配置只编译对应的一个入口
└── Utils/
    └── RevitApiExtensions.cs          ← ElementId.GetValue + CreateFloor + 通用辅助方法

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
| 6 | Floor 创建 | Revit 2022 | `doc.Create.NewFloor(...)` | `Floor.Create(...)` | `RevitApiExtensions.CreateFloor()` 已实现 |
| 7 | doc.Create.New* 系列 | 持续演进 | `doc.Create.NewRoom` 等 | 静态工厂 (未来) | 暂不动，设观察点 |
| 8 | Opening 垂直/竖井 | Revit 2025+ | `doc.Create.NewOpening` | `Opening.CreateVertical` | 未来 `#if` 扩展 |
| 9 | `Category.GetCategory` | 无变化 | `Category.GetCategory(doc, id)` | 相同 | 无需处理 |
| 10 | 共享参数 API | 无变化 | `app.SharedParametersFilename` | 相同 | 无需处理 |

---

## 10. 实施状态（2026-08）

| 优先级 | 差异 | 状态 | 实现位置 |
|--------|------|------|---------|
| **P0** | ElementId `.GetValue()` 统一方法（对外 long，内部 `#if`） | ✅ 已完成 | `RevitApiExtensions.cs:7-11` |
| **P1** | `AddFamilyParameter` 统一 6 参签名 | ✅ 已完成 | `RevitParameterAdmin.cs`，内部 `#if` R2022+ |
| **P1** | `GetDisplayUnitType()` 统一方法 | ✅ 已完成 | `RevitApiExtensions.cs:15-19` |
| **P2** | `CreateFloor()` 工厂方法 | ✅ 已完成 | `RevitApiExtensions.cs:22-32` |
| **P3** | doc.Create.New* 观察/预留 | ⏳ 未动 | 未来兼容性预留 |
