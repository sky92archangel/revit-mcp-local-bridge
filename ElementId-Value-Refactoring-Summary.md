# ElementId.Value 统一重构总结文档

## 背景

**Revit 2024+** 中 `ElementId.Value` 返回类型从 `int` 变为 `long`。  
当代码需要同时兼容多版本 Revit（2018-2024 前 vs 2024+）时，直接使用 `.Value` 或 `.IntegerValue` 会导致编译错误或不兼容。

**参考项目** (`commandset\Utils\ElementIdExtensions.cs`) 已实现的统一出口：

```csharp
// 返回 long
#if REVIT2024_OR_GREATER
public static long GetValue(this ElementId id) => id.Value;
#else
public static long GetValue(this ElementId id) => id.IntegerValue;
#endif

// 返回 int (用于序列化、int 上下文)
#if REVIT2024_OR_GREATER
public static int GetIntValue(this ElementId id) => (int)id.Value;
#else
public static int GetIntValue(this ElementId id) => id.IntegerValue;
#endif
```

**当前项目** (`revit-mcp-local-bridge`) 仅目标 Revit 2026+，`ElementId.Value` 为 `long`，没有 `#if` 条件编译。引入扩展方法后仍需修改所有 `.Value` 调用点。

---

## 替换规则

| 当前写法 | 应替换为 | 说明 |
|---------|---------|------|
| `id.Value` (用于 `long` 上下文) | `id.GetValue()` | 返回值类型 `long` |
| `(int)id.Value` | `id.GetIntValue()` | 返回值类型 `int` |
| `id.Value` 在错误消息字符串拼接中 | `id.GetValue()` | `long.ToString()` 行为一致 |
| `id.Value == ElementId.InvalidElementId.Value` | `id == ElementId.InvalidElementId` | 或 `id.GetValue() == ElementId.InvalidElementId.GetValue()` |
| `ids.Select(id => id.Value)` → `long[]` | `ids.Select(id => id.GetValue())` | 返回 `long[]` |
| `ids.Select(id => (int)id.Value)` → `int[]` | `ids.Select(id => id.GetIntValue())` | 返回 `int[]` |
| `ids.GroupBy(id => (int)id.Value)` | `ids.GroupBy(id => id.GetIntValue())` | 按 int 分组 |
| `element.Id.Value` 用于排序 | `element.Id.GetValue()` | 或直接用 `element.Id` (`ElementId` 实现了 `IComparable`) |
| `type.Category.Id.Value == categoryId.Value` | `type.Category.Id == categoryId` | 直接比较 `ElementId` |

**特殊规则**：
- `ElementId.InvalidElementId.Value` → 直接使用 `ElementId.InvalidElementId` 进行相等比较
- `new ElementId(BuiltInCategory.OST_TitleBlocks).Value` → `.GetValue()` 或直接存 `ElementId` 引用

---

## 各文件替换统计

| 文件 | `(int)id.Value` (→GetIntValue) | `id.Value` (→GetValue) | `== InvalidElementId` (→id 直接比较) | 合计约 |
|------|:-:|:-:|:-:|:-:|
| `src/RevitOutputOperations.cs` | 0 | ~70 | ~8 | ~80 |
| `src/RevitPlanCreations.cs` | 0 | ~44 | ~10 | ~54 |
| `src/RevitPlanMutations.cs` | 0 | ~37 | ~5 | ~42 |
| `src/RevitPlanQueries.cs` | 0 | ~32 | ~6 | ~38 |
| `src/RevitFamilyOperations.cs` | 6 | 2 | 0 | 8 |
| `src/RevitCommandExecutor.cs` | 6 | 0 | 0 | 6 |
| `src/RevitLookups.cs` | 4 | 0 | 0 | 4 |
| `src/PlanCommandExecutor.cs` | 1 | 0 | 0 | 1 |
| **总计** | **17** | **~185** | **~29** | **~230+** |

---

## 详细分类与示例

### 类型 A: `(int)id.Value` → `id.GetIntValue()` (17 处)

涉及 4 个文件，全部是显式 `(int)` 转型：

**`src/RevitCommandExecutor.cs`** (6 处)
```
line 241: { "id", (int)level.Id.Value }
line 264: { "id", (int)wallType.Id.Value }
line 307: plan["id"] = (int)created.Id.Value;
line 349: plan["id"] = (int)created.Id.Value;
line 422: walls.Select(wall => (int)wall.Id.Value).ToArray()
line 486: plan["id"] = (int)created.Id.Value;
```

**`src/RevitFamilyOperations.cs`** (8 处)
```
line 110: data["family_id"] = (int)family.Id.Value;
line 113: .Select(id => (int)id.Value).ToArray()
line 271: plan["family_id"] = (int)loadedFamily.Id.Value;
line 274: .Select(id => (int)id.Value).ToArray()
line 956: { "type_id", (int)symbol.Id.Value }
line 982: (int)byId.Family.Id.Value != (int)family.Id.Value  (2处)
```

`RevitLookups.cs` 还有一处特殊：`(int)parameter.AsElementId().Value` → `parameter.AsElementId().GetIntValue()`

### 类型 B: `id.Value` (long 上下文) → `id.GetValue()` (~185 处)

涉及 5 个文件，最广泛：

**`RevitPlanCreations.cs`** — 典型模式：
```csharp
data["element_id"] = level.Id.Value;        // → .GetValue()
data["element_ids"] = new[] { wall.Id.Value }; // → .GetValue()
```

**`RevitOutputOperations.cs`** — 大量相似模式：
```csharp
data["element_id"] = view.Id.Value;
data["element_ids"] = new[] { view.Id.Value };
data["element_id"] = created.Id.Value;
```

**`RevitPlanQueries.cs`** — 特殊模式：
```csharp
// 排序（直接用 ElementId IComparable 或 .GetValue()）
.OrderBy(element => element.Id.Value)

// long 计算（必须 long）
long low = Math.Min(currentId.Value, owner.Id.Value);
long high = Math.Max(currentId.Value, owner.Id.Value);

// 直接访问
document.ActiveView.Id.Value

// Lambda 隐式 long
family.GetFamilySymbolIds().Select(id => id.Value).ToArray()
ids.Select(id => (long)id.Value).ToArray()
```

**`RevitPlanMutations.cs`** — 错误消息拼接：
```csharp
"找不到 element_id=" + id.Value + "。"
"元素 " + id.Value + " 找不到参数..."
```

### 类型 C: `id.Value == ElementId.InvalidElementId.Value` → `id == ElementId.InvalidElementId` (~29 处)

跨多个文件，统一模式：
```csharp
// 当前写法
if (sheetId.Value == ElementId.InvalidElementId.Value)
if (viewId.Value == ElementId.InvalidElementId.Value)

// 应改为（ElementId 的直接相等比较）
if (sheetId == ElementId.InvalidElementId)
if (viewId == ElementId.InvalidElementId)
```

`ElementId` 类型是引用类型，重写了 `Equals`，直接比较即可。  
如果用扩展方法，也可写成 `sheetId.GetValue() == ElementId.InvalidElementId.GetValue()`，但不推荐。

**涉及文件**：
- `RevitPlanCreations.cs` — sheetId, viewId, hostId, aId, bId
- `RevitPlanMutations.cs` — sourceId, familyId, id
- `RevitOutputOperations.cs` — revisionId, sheetId, scheduleId, viewId, templateId, id
- `RevitPlanQueries.cs` — targetId, seedId, viewId, levelId

### 类型 D: `Category.Id.Value` 比较 → `category.Id == categoryId` (4 处)

```csharp
// RevitPlanCreations.cs:833
symbol.Category.Id.Value != new ElementId(BuiltInCategory.OST_TitleBlocks).Value
// → symbol.Category.Id != new ElementId(BuiltInCategory.OST_TitleBlocks)

// RevitPlanQueries.cs:1308
type.Category.Id.Value == categoryId.Value
// → type.Category.Id == categoryId
```

`categoryId` 已是 `ElementId`，直接比较即可。

### 类型 E: 分组 key → `id.GetIntValue()` (1 处)

```csharp
// PlanCommandExecutor.cs:552
ids.GroupBy(id => (int)id.Value).Select(group => group.First()).ToList()
// → ids.GroupBy(id => id.GetIntValue()).Select(group => group.First()).ToList()
```

---

## TagWallsEventHandler.FindWallTagType 模式说明

**参考项目** 中存在如下双分支模式（当前项目暂没有此代码，作为未来参考）：

```csharp
// 当前 (双分支 #if/#else)
#if REVIT2024_OR_GREATER
    ElementId tagId = tag.Id;  // tag.Id 是 ElementId 类型
    // 使用 tag.Id.Value 和 wall.Id.Value
    long tagVal = tag.Id.Value;
    long wallVal = wall.Id.Value;
#else
    int tagVal = tag.Id.GetIntValue();
    int wallVal = wall.Id.GetIntValue();
#endif

// 统一后 (单分支)
    ElementId tagId = tag.Id;
    int tagVal = tag.Id.GetIntValue();
    int wallVal = wall.Id.GetIntValue();
```

这是典型的需要从 `#if/#else` 双分支转为单分支 + `.GetIntValue()` 的模式。

---

## 实施建议

1. **在 `src/` 下创建 `ElementIdExtensions.cs`**，定义 `GetValue()` 和 `GetIntValue()` 扩展方法
2. **当前项目仅目标 Revit 2026+**，所以扩展方法内可以直接 `=> id.Value` 和 `=> (int)id.Value`（无需 `#if`）
3. **如果需要向后兼容旧 Revit**，加 `#if REVIT2024_OR_GREATER` 条件编译（参考 commandset 项目）
4. **按文件修改顺序建议**（由简到繁）：
   1. `RevitCommandExecutor.cs` — 6 处，全部 `(int)` → `GetIntValue()`
   2. `RevitLookups.cs` — 4 处，全部 `(int)` → `GetIntValue()`
   3. `PlanCommandExecutor.cs` — 1 处
   4. `RevitFamilyOperations.cs` — 8 处，混合模式
   5. `RevitPlanMutations.cs` — ~42 处，error msg + data dict
   6. `RevitPlanQueries.cs` — ~38 处，含排序/计算/比较
   7. `RevitPlanCreations.cs` — ~54 处，data dict 批量替换
   8. `RevitOutputOperations.cs` — ~80 处，量最大

**正则搜索**：`(\(int\))?[\w.]+\.(Id\s*\.\s*)?Value` → 配合 `ElementId.InvalidElementId` 排除后可定位全部调用点。
