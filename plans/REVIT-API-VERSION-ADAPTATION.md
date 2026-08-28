# Revit API 版本适配分析

## 条件编译符号定义

`RevitCommandBridge.csproj:35-46` 按配置定义递增符号：

| 配置 | 定义符号 |
|------|----------|
| R20, R21 | (无) |
| R22 | `REVIT2022_OR_GREATER` |
| R23 | `REVIT2022_OR_GREATER`, `REVIT2023_OR_GREATER` |
| R24 | `REVIT2022_OR_GREATER`, `REVIT2023_OR_GREATER`, `REVIT2024_OR_GREATER` |
| R25, R26 | `REVIT2022_OR_GREATER`, `REVIT2023_OR_GREATER`, `REVIT2024_OR_GREATER`, `REVIT2025_OR_GREATER` |

> `REVIT2025_OR_GREATER` 已定义但未被任何 `#if` 使用。

---

## 一、`#if` 预编译分支（7 处）

### 1. `ElementId` 值获取 — `src/RevitApiExtensions.cs:7-11`

```csharp
#if REVIT2024_OR_GREATER
    public static long GetValue(this ElementId id) => id.GetValue();
#else
    public static long GetValue(this ElementId id) => id.IntegerValue;
#endif
```

| 版本 | API |
|------|-----|
| R2024+ | `ElementId.GetValue()` |
| R2020–R2023 | `ElementId.IntegerValue`（已废弃） |

向全项目提供统一的 `id.GetValue()` 扩展方法，避免每个调用处写 `#if`。  
调用方（15+ 文件）：`RevitCommandExecutor.cs`、`RevitLookups.cs`、`RevitPlanQueries.cs` 等。

---

### 2. 参数显示单位类型 — `src/RevitLookups.cs:291-295`

```csharp
case StorageType.Double:
    data["internal_value"] = parameter.AsDouble();
#if REVIT2022_OR_GREATER
    data["display_unit_type"] = parameter.Definition.GetDataType()?.TypeId;
#else
    data["display_unit_type"] = parameter.Definition.ParameterType.ToString();
#endif
```

| 版本 | API |
|------|-----|
| R2022+ | `ParameterDefinition.GetDataType()` 返回 `ForgeTypeId` |
| R2020–R2021 | `ParameterDefinition.ParameterType` 返回 `ParameterType` 枚举 |

---

### 3. 参数过滤器规则创建 — `src/RevitOutputOperations.cs:1069-1078, 1080-1089`

两处相同的 `#if REVIT2023_OR_GREATER`，处理不同值类型：

```csharp
#if REVIT2023_OR_GREATER
    return ParameterFilterRuleFactory.CreateEqualsRule(parameterId,
        Convert.ToString(value, CultureInfo.InvariantCulture));
#else
    return new FilterStringRule(new ParameterValueProvider(parameterId),
        new FilterStringEquals(),
        Convert.ToString(value, CultureInfo.InvariantCulture), false);
#endif
```

| 版本 | API |
|------|-----|
| R2023+ | `ParameterFilterRuleFactory.CreateEqualsRule(ElementId, string)` |
| R2020–R2022 | `FilterStringRule` + `FilterStringEquals` 显式构造 |

---

### 4. 楼板创建 — `src/RevitPlanCreations.cs:201-208`

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

| 版本 | API |
|------|-----|
| R2022+ | `Floor.Create(Document, IList<CurveLoop>, ElementId, ElementId, bool, ElementId, double)` — 需要手动构造 `CurveLoop` |
| R2020–R2021 | `Document.Create.NewFloor(CurveArray, FloorType, Level, bool)` |

---

### 5. `ResolveSpec` / `ResolveGroup` / `AddFamilyParameter` — `src/RevitParameterAdmin.cs:91-211`

这是最大的 `#if` 块（121 行），整个方法签名和返回类型完全不同：

#### 5a. `ResolveSpec()` R2022+ / R2020–2021 双实现

| 版本 | 签名 | 返回值 |
|------|------|--------|
| R2022+ | `public static ForgeTypeId ResolveSpec(string token)` | `SpecTypeId.Length` / `SpecTypeId.Number` 等 |
| R2020–R2021 | `public static ParameterType ResolveSpec(string token)` | `ParameterType.Length` / `ParameterType.Integer` 等 |

#### 5b. `ResolveGroup()` R2022+ / R2020–2021 双实现

| 版本 | 签名 | 返回值 |
|------|------|--------|
| R2022+ | `private static ForgeTypeId ResolveGroup(string token)` | `GroupTypeId.Geometry` / `GroupTypeId.Data` 等 |
| R2020–R2021 | `private static BuiltInParameterGroup ResolveGroup(string token)` | `BuiltInParameterGroup.PG_GEOMETRY` 等 |

#### 5c. `AddFamilyParameter()` 签名差异

```csharp
#if REVIT2022_OR_GREATER
    public static FamilyParameter AddFamilyParameter(
        FamilyManager manager, string name, string typeToken, string groupToken, bool isInstance)
    {
        ForgeTypeId spec = ResolveSpec(typeToken);
        ForgeTypeId group = ResolveGroup(groupToken);
        return manager.AddParameter(name, group, spec, isInstance);
    }
#else
    public static FamilyParameter AddFamilyParameter(
        FamilyManager manager, Document familyDocument, string name,
        string typeToken, string groupToken, bool isInstance)
    {
        ParameterType spec = ResolveSpec(typeToken);
        BuiltInParameterGroup group = ResolveGroup(groupToken);
        var options = new ExternalDefinitionCreationOptions(name, spec);
        var defGroup = familyDocument.Application.OpenSharedParameterFile()
            ?.Groups.Create("RevitCommandBridge");
        ExternalDefinition definition = (ExternalDefinition)defGroup?.Definitions.Create(options);
        return manager.AddParameter(definition, group, isInstance);
    }
#endif
```

| 版本 | 参数数 | 机制 |
|------|--------|------|
| R2022+ | 5 参（无 Document） | `manager.AddParameter(string, ForgeTypeId, ForgeTypeId, bool)` |
| R2020–R2021 | 6 参（含 Document） | `manager.AddParameter(ExternalDefinition, BuiltInParameterGroup, bool)` + 共享参数文件 |

---

### 6. `AddFamilyParameter` 调用 — `src/RevitPlanMutations.cs:444-460`

```csharp
case "add":
#if REVIT2022_OR_GREATER
    RevitParameterAdmin.AddFamilyParameter(manager, name,
        PlanValues.String(item, "length", "type"),
        PlanValues.String(item, "data", "group", "parameter_group"),
        PlanValues.Boolean(item, false, "is_instance", "instance"));
#else
    RevitParameterAdmin.AddFamilyParameter(manager, familyDocument, name,
        PlanValues.String(item, "length", "type"),
        PlanValues.String(item, "data", "group", "parameter_group"),
        PlanValues.Boolean(item, false, "is_instance", "instance"));
#endif
```

R2020–R2021 版本多传 `familyDocument` 参数以支持 `ExternalDefinition` 机制。

---

## 二、运行时版本检测

### 7. `BridgeBuildInfo.SetApiYear()` / `RevitVersion` — `src/BridgeBuildInfo.cs:21-43`

```csharp
private static int _forcedApiYear;

public static void SetApiYear(int year) => _forcedApiYear = year;

public static string RevitVersion
{
    get
    {
        if (_forcedApiYear > 0)
            return _forcedApiYear.ToString();
        Version apiVersion = typeof(Element).Assembly.GetName().Version;
        return apiVersion == null ? "unknown" : (2000 + apiVersion.Major).ToString();
    }
}
```

运行时版本入口，`SetApiYear()` 由各 AdapterEntry 在 `OnStartup` 时调用，确保运行时 `RevitVersion` 属性返回正确的年份。

使用此属性的位置：
- `src/BridgeRuntime.cs:46,221` — heartbeat/dispose 状态 payload
- `src/RevitCommandBridgeApp.cs:94` — shutdown 状态
- `src/RevitCommandExecutor.cs:103` — health 检查响应
- `src/RevitPlanQueries.cs:35` — query_document 响应
- `src/RevitCommandBridgeApp.cs:319-323` — MCP 配置文件的版本路径构造

### 8. AdapterEntry 继承模式 — `src/Adapter/AdapterEntry{20..27}.cs`

每个文件 18 行，相同模式。以 R26 为例：

```csharp
public sealed class RevitCommandBridgeApp26 : RevitCommandBridgeApp
{
    public override Result OnStartup(UIControlledApplication application)
    {
        BridgeBuildInfo.SetApiYear(2026);
        return base.OnStartup(application);
    }
}
```

| 文件 | 版本 | 类名 |
|------|------|------|
| `AdapterEntry20.cs` | 2020 | `RevitCommandBridgeApp20` |
| `AdapterEntry21.cs` | 2021 | `RevitCommandBridgeApp21` |
| `AdapterEntry22.cs` | 2022 | `RevitCommandBridgeApp22` |
| `AdapterEntry23.cs` | 2023 | `RevitCommandBridgeApp23` |
| `AdapterEntry24.cs` | 2024 | `RevitCommandBridgeApp24` |
| `AdapterEntry25.cs` | 2025 | `RevitCommandBridgeApp25` |
| `AdapterEntry26.cs` | 2026 | `RevitCommandBridgeApp26` |
| `AdapterEntry27.cs` | 2027 | `RevitCommandBridgeApp27` |

> `AdapterEntry27.cs` 代码已完成但 `.csproj` 中无对应编译配置（R27 未定义）。需补充后方可构建。

---

## 三、反射适配

### 9. `RevitFamilyOperations.AddFamilyParameter()` — 运行时 ForgeTypeId 反射 — `src/RevitFamilyOperations.cs:674-711`

```csharp
private static FamilyParameter AddFamilyParameter(FamilyManager manager, FamilyParameterSpec spec)
{
    Assembly assembly = manager.GetType().Assembly;
    // 动态查找 ForgeTypeId 类型
    Type forgeType = assembly.GetType("Autodesk.Revit.DB.ForgeTypeId", false);
    // 反射查找 AddParameter(string, ForgeTypeId, ForgeTypeId, bool) 重载
    MethodInfo overload = manager.GetType().GetMethods()
        .FirstOrDefault(method =>
        {
            if (!string.Equals(method.Name, "AddParameter", StringComparison.Ordinal))
                return false;
            ParameterInfo[] parameters = method.GetParameters();
            return parameters.Length == 4 &&
                   parameters[0].ParameterType == typeof(string) &&
                   parameters[1].ParameterType == forgeType &&
                   parameters[2].ParameterType == forgeType &&
                   parameters[3].ParameterType == typeof(bool);
        });
    // ...
    return (FamilyParameter)overload.Invoke(manager,
        new[] { (object)spec.Name, group, parameterType, spec.IsInstance });
}
```

通过 `assembly.GetType("Autodesk.Revit.DB.ForgeTypeId")` 动态发现类型，避免编译时 `#if`。  
仅在 R2022+ 上有效，R2020–R2021 无 `ForgeTypeId` 会抛异常（实际运行时不会被旧版本调用）。

### 10. `ResolveForgeTypeId()` 与 `ResolveForgeSpecTypeId()` — `src/RevitFamilyOperations.cs:717-757`

```csharp
private static object ResolveForgeTypeId(Assembly assembly, string typeName, string memberName)
{
    Type type = assembly.GetType("Autodesk.Revit.DB." + typeName, false);
    PropertyInfo property = type.GetProperty(memberName, BindingFlags.Public | BindingFlags.Static);
    if (property != null) return property.GetValue(null, null);
    FieldInfo field = type.GetField(memberName, BindingFlags.Public | BindingFlags.Static);
    if (field != null) return field.GetValue(null);
    throw new BridgeCommandException("缺少 ForgeTypeId 成员：" + typeName + "." + memberName);
}
```

通过反射读取 `SpecTypeId.Length` / `GroupTypeId.Geometry` 等静态属性，字符串枚举覆盖：

```
length → SpecTypeId.Length
text   → SpecTypeId.String.Text
yesno  → SpecTypeId.Boolean.YesNo
number → SpecTypeId.Number
angle  → SpecTypeId.Angle
slope  → SpecTypeId.Slope
url    → SpecTypeId.String.Url
area   → SpecTypeId.Area
volume → SpecTypeId.Volume
force  → SpecTypeId.Force
```

---

## 四、已注释的 API 差异

### 11. `Revision.NumberType` 在 R2026 中移除 — `src/RevitOutputOperations.cs:519`

```csharp
// revision.NumberType = numberType; // 在 Revit 2026 中已移除
```

`Revision.NumberType` 属性在 Revit 2026 API 中被移除，代码中注释标记，没有替代的 `#if` 处理。

---

## 五、适配策略总览

| 策略 | 使用处 | 适用场景 |
|------|--------|----------|
| `#if` 预编译 | 7 处分支 | API 签名/类型在编译期已知差异 |
| 反射 | 2 个方法（`AddFamilyParameter`、`ResolveForgeTypeId`） | API 类型名/属性名在编译期跨版本一致，仅需运行时发现 |
| 继承 + `SetApiYear` | 8 个 AdapterEntry | 每个 Revit 版本需独立编译的 `IExternalApplication` 入口 |
| 注释标记 | 1 处（`NumberType`） | API 被移除且业务影响可控，未做向后兼容 |

## 六、版本差异履历

| Revit API 变更 | 影响版本 | 处理方式 | 文件 |
|---------------|----------|----------|------|
| `ElementId.IntegerValue` → `GetValue()` | R2024+ | `#if REVIT2024_OR_GREATER` + 扩展方法 | `RevitApiExtensions.cs:7-11` |
| `ParameterType` → `ForgeTypeId`（参数单位） | R2022+ | `#if REVIT2022_OR_GREATER` | `RevitLookups.cs:291-295` |
| `BuiltInParameterGroup` → `GroupTypeId` | R2022+ | `#if REVIT2022_OR_GREATER` 双实现 | `RevitParameterAdmin.cs:91-211` |
| `doc.Create.NewFloor()` → `Floor.Create()` | R2022+ | `#if REVIT2022_OR_GREATER` | `RevitPlanCreations.cs:201-208` |
| `FilterStringRule` → `ParameterFilterRuleFactory.CreateEqualsRule()` | R2023+ | `#if REVIT2023_OR_GREATER` | `RevitOutputOperations.cs:1069-1089` |
| `FamilyManager.AddParameter(ExternalDefinition,...)` → `(string, ForgeTypeId, ForgeTypeId, bool)` | R2022+ | 反射 + `#if` 调用分支 | `RevitFamilyOperations.cs:674-711` / `RevitParameterAdmin.cs` |
| `Revision.NumberType` 移除 | R2026+ | 注释标记 | `RevitOutputOperations.cs:519` |
