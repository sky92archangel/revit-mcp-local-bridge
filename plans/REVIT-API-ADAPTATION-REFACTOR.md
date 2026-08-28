# `#if` 改统一接口分析

逐一评估 7 处 `#if` 是否可收敛为统一函数/接口出口。

---

## ① `RevitApiExtensions.cs:7-11` — `ElementId.GetValue()`

```csharp
#if REVIT2024_OR_GREATER
    public static long GetValue(this ElementId id) => id.GetValue();
#else
    public static long GetValue(this ElementId id) => id.IntegerValue;
#endif
```

**现状：已是最优。** `#if` 隐藏在扩展方法内部，全项目统一调用 `id.GetValue()`。

→ **不改。**

---

## ② `RevitLookups.cs:291-295` — `ParameterData()` 中获取显示单位类型

```csharp
// 内嵌在 ParameterData() 的 StorageType.Double 分支里：
#if REVIT2022_OR_GREATER
    data["display_unit_type"] = parameter.Definition.GetDataType()?.TypeId;
#else
    data["display_unit_type"] = parameter.Definition.ParameterType.ToString();
#endif
```

**可改造：提取 `GetDisplayUnitType(ParameterDefinition)` 统一方法到 `RevitApiExtensions.cs`：**

```csharp
// RevitApiExtensions.cs 追加
public static string GetDisplayUnitType(this ParameterDefinition def)
{
#if REVIT2022_OR_GREATER
    return def.GetDataType()?.TypeId;
#else
    return def.ParameterType.ToString();
#endif
}
```

调用处变为：
```csharp
data["display_unit_type"] = parameter.Definition.GetDisplayUnitType();
```

**收益：** `ParameterData()` 不再感知版本差异，版本知识聚集到 `RevitApiExtensions`。

→ **建议改造。** 改动范围：`RevitApiExtensions.cs` 加 8 行，`RevitLookups.cs` 减 4 行。

---

## ③ `RevitOutputOperations.cs:1069-1089` — `CreateEqualsRule()` 过滤器规则

```csharp
// 已包裹在私有方法 CreateEqualsRule() 内部：
private static FilterRule CreateEqualsRule(ElementId parameterId, object value, string parameterName)
{
    // ... bool、int 分支已统一 ...
    if (value is double || value is float || value is decimal)
    {
#if REVIT2023_OR_GREATER
        return ParameterFilterRuleFactory.CreateEqualsRule(parameterId, ...);
#else
        return new FilterStringRule(new ParameterValueProvider(parameterId), new FilterStringEquals(), ...);
#endif
    }
#if REVIT2023_OR_GREATER
    return ParameterFilterRuleFactory.CreateEqualsRule(parameterId, ...);
#else
    return new FilterStringRule(new ParameterValueProvider(parameterId), new FilterStringEquals(), ...);
#endif
}
```

**现状：已是最优。** 调用方 `ApplyViewFilter()` 等只看 `CreateEqualsRule()` 签名，不感知版本。

→ **不改。**

---

## ④ `RevitPlanCreations.cs:201-208` — `Floor.Create()` 楼板

```csharp
#if REVIT2022_OR_GREATER
    var floorProfile = new CurveLoop();
    foreach (Curve c in profile)
        floorProfile.Append(c);
    Floor floor = Floor.Create(context.Document, new[] { floorProfile },
        floorType.Id, level.Id, structural, null, 0.0);
#else
    Floor floor = context.Document.Create.NewFloor(profile, floorType, level, structural);
#endif
```

**可改造：提取 `CreateFloor(Document, CurveArray, FloorType, Level, bool)` 统一方法：**

```csharp
// RevitApiExtensions.cs 或独立 helper 类
public static Floor CreateFloor(Document doc, CurveArray profile, FloorType floorType, Level level, bool structural)
{
#if REVIT2022_OR_GREATER
    var loop = new CurveLoop();
    foreach (Curve c in profile)
        loop.Append(c);
    return Floor.Create(doc, new[] { loop }, floorType.Id, level.Id, structural, null, 0.0);
#else
    return doc.Create.NewFloor(profile, floorType, level, structural);
#endif
}
```

调用处变为：
```csharp
Floor floor = RevitApiExtensions.CreateFloor(context.Document, profile, floorType, level, structural);
```

**收益：** 消除 `CurveLoop` 转换与 `#if` 的侵入。`CurveArray → CurveLoop` 转换逻辑也归入同一方法。

→ **建议改造。** 改动范围：新增 1 方法（~12 行），`RevitPlanCreations.cs` 减 8 行。

---

## ⑤ `RevitParameterAdmin.cs:91-211` — `ResolveSpec` / `ResolveGroup` / `AddFamilyParameter`（121 行最大块）

```csharp
#if REVIT2022_OR_GREATER
    public static ForgeTypeId ResolveSpec(string token) { ... }      // 返回 ForgeTypeId
    private static ForgeTypeId ResolveGroup(string token) { ... }    // 返回 ForgeTypeId
    public static FamilyParameter AddFamilyParameter(
        FamilyManager manager, string name, string typeToken, string groupToken, bool isInstance) { ... }
#else
    public static ParameterType ResolveSpec(string token) { ... }    // 返回 ParameterType
    private static BuiltInParameterGroup ResolveGroup(string token) { ... }  // 返回 BuiltInParameterGroup
    public static FamilyParameter AddFamilyParameter(
        FamilyManager manager, Document familyDocument, string name,
        string typeToken, string groupToken, bool isInstance) { ... }
#endif
```

### 难点

`ResolveSpec` 和 `ResolveGroup` 的 **返回类型完全不同**（`ForgeTypeId` 引用类型 vs `ParameterType`/`BuiltInParameterGroup` 值类型枚举），无法直接统一签名。`AddFamilyParameter` 的机制完全不同（R2022+ 用 `manager.AddParameter(string, ForgeTypeId, ForgeTypeId, bool)`；R2020–2021 需走 `ExternalDefinition` + 共享参数文件）。

### 方案对比

#### 方案 A：统一公共入口 + 内部 `#if`（推荐）

保持 `ResolveSpec` / `ResolveGroup` 返回 `object`，并在 `AddFamilyParameter` 内部完成全部差异：

```csharp
// 统一公共方法（无 #if 的签名）
public static FamilyParameter AddFamilyParameter(
    FamilyManager manager, Document familyDocument, string name,
    string typeToken, string groupToken, bool isInstance)
{
    // 内部保留 #if，外部签名不变
#if REVIT2022_OR_GREATER
    ForgeTypeId spec = ResolveSpec(token);
    ForgeTypeId group = ResolveGroup(groupToken);
    return manager.AddParameter(name, group, spec, isInstance);
#else
    ParameterType spec = ResolveSpec(token);
    BuiltInParameterGroup group = ResolveGroup(groupToken);
    var options = new ExternalDefinitionCreationOptions(name, spec);
    var defGroup = familyDocument.Application
        .OpenSharedParameterFile()?.Groups.Create("RevitCommandBridge");
    ExternalDefinition definition = (ExternalDefinition)defGroup?.Definitions.Create(options);
    return manager.AddParameter(definition, group, isInstance);
#endif
}

// ResolveSpec 对外隐藏，返回 object，内部 #if
private static object ResolveSpec(string token) { /* 保留 #if */ }
private static object ResolveGroup(string token) { /* 保留 #if */ }
```

- 公共签名固定为 6 参 `(manager, familyDocument, name, typeToken, groupToken, isInstance)`
- R2022+ 版本忽略 `familyDocument` 参数
- `ResolveSpec` / `ResolveGroup` 改为 `private`，返回 `object`
- **收益：** `RevitPlanMutations.cs:444-460` 的调用处 `#if` 自动消失

#### 方案 B：文件级 `#if` + 分部类

```csharp
// RevitParameterAdmin.cs（公共框架，无 #if）
internal static partial class RevitParameterAdmin
{
    public static FamilyParameter AddFamilyParameter(
        FamilyManager manager, Document familyDocument, string name,
        string typeToken, string groupToken, bool isInstance) { }
}

// RevitParameterAdmin.R2022.cs（#if 编译其一）
#if REVIT2022_OR_GREATER
partial class RevitParameterAdmin { /* ForgeTypeId 实现 */ }
#endif

// RevitParameterAdmin.R20.cs
#if !REVIT2022_OR_GREATER
partial class RevitParameterAdmin { /* ParameterType 实现 */ }
#endif
```

- **收益：** 零 `#if` 在方法体内，版本代码物理隔离
- **问题：** C# 分部类不允许同一方法的不同实现分别标注 `#if`，需拆为不同方法再转发，或用 `partial` method 但需 C# 13

#### 方案 C：接口 + 策略模式

```csharp
internal interface IParameterSpecResolver
{
    object ResolveSpec(string token);
    object ResolveGroup(string token);
    FamilyParameter AddFamilyParameter(FamilyManager manager, Document doc, string name, string typeToken, string groupToken, bool isInstance);
}

internal class ForgeTypeSpecResolver : IParameterSpecResolver { /* R2022+ */ }
internal class LegacySpecResolver : IParameterSpecResolver { /* R2020–2021 */ }
```

- **收益：** 完全无 `#if`，可测试
- **问题：** 过度设计，类层次膨胀，运行时分发增加复杂性，整体只有 2 个 API 版本

→ **推荐方案 A。** 零外部 API 变更，`RevitPlanMutations.cs` 调用处 `#if` 一并消除，改动最小。

---

## ⑥ `RevitPlanMutations.cs:444-460` — `AddFamilyParameter` 调用处

```csharp
case "add":
#if REVIT2022_OR_GREATER
    RevitParameterAdmin.AddFamilyParameter(manager, name, ...);
#else
    RevitParameterAdmin.AddFamilyParameter(manager, familyDocument, name, ...);
#endif
```

**改造前提：** 依赖 ⑤ 改造。一旦 `RevitParameterAdmin.AddFamilyParameter` 统一为固定 6 参签名，此处 `#if` 自然消失，变为：

```csharp
case "add":
    RevitParameterAdmin.AddFamilyParameter(manager, familyDocument, name,
        PlanValues.String(item, "length", "type"),
        PlanValues.String(item, "data", "group", "parameter_group"),
        PlanValues.Boolean(item, false, "is_instance", "instance"));
```

→ **间接消除。** 不需要独立改造，随 ⑤ 一起完成。

---

## 改造汇总

| 序号 | 文件 | 行 | 门槛 | 改造 | 方案 |
|------|------|-----|------|------|------|
| ① | `RevitApiExtensions.cs` | 7-11 | R2024+ | **不改** | 已是最优 |
| ② | `RevitLookups.cs` | 291-295 | R2022+ | **改** | 提取 `ParameterDefinition.GetDisplayUnitType()` 到 `RevitApiExtensions.cs` |
| ③ | `RevitOutputOperations.cs` | 1069-1089 | R2023+ | **不改** | 已包裹在 `CreateEqualsRule()` |
| ④ | `RevitPlanCreations.cs` | 201-208 | R2022+ | **改** | 提取 `RevitApiExtensions.CreateFloor(Doc, CurveArray, FloorType, Level, bool)` |
| ⑤ | `RevitParameterAdmin.cs` | 91-211 | R2022+ | **改** | 统一 6 参公共签名，内部保留 `#if` |
| ⑥ | `RevitPlanMutations.cs` | 444-460 | R2022+ | **被动消除** | 随 ⑤ 改造自动消失 |

改造后，全项目 `#if` 分布变化：

```
改造前: RevitApiExtensions(1) RevitLookups(1) RevitOutputOperations(2) RevitPlanCreations(1) RevitParameterAdmin(3) RevitPlanMutations(1) = 9 处
改造后: RevitApiExtensions(1) RevitOutputOperations(2) RevitParameterAdmin(3,内部) = 5 处（且全部隐藏在统一方法内部）
```

暴露在调用处的 `#if` 从 **4 处减为 0 处**。
