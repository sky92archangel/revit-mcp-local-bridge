# Revit Command Bridge 协议

协议标识：`revit-command-bridge/2.0`。所有入口最终写入同一套本地 JSON 队列，并从 outbox 读取统一响应。

2.0 的主入口是 `execute_plan`：客户端把自然语言转换为受控建模计划，Revit 插件负责校验、主线程调度和事务。旧的 `create_wall` 等单命令仍保留兼容，但新接入应优先使用计划入口。机器可读约束在 [schemas/execute-plan.schema.json](./schemas/execute-plan.schema.json)。

## 提交一个计划

先预览，下例不会修改模型：

```json
{
  "operation": "execute_plan",
  "args": {
    "steps": [
      {
        "id": "check_project",
        "operation": "query_document",
        "args": {}
      },
      {
        "id": "support",
        "operation": "create_direct_shape",
        "args": {
          "name": "混凝土支座占位",
          "category": "OST_GenericModel",
          "geometry": [
            {
              "kind": "box",
              "min": { "x": 0, "y": 0, "z": 0 },
              "max": { "x": 3000, "y": 2000, "z": 2500 }
            }
          ]
        }
      },
      {
        "id": "select_support",
        "operation": "select_elements",
        "args": {
          "targets": ["$support"],
          "show": true
        }
      }
    ]
  },
  "preview": true,
  "document_title": "项目1",
  "source": "my-harness"
}
```

确认响应中的计划正确后，将同一 JSON 的 `preview` 改为 `false` 提交。计划中的写步骤会在一个 Revit Transaction 中执行：任一步失败会回滚前面的写入；成功时可在 Revit 中一次 `Ctrl+Z` 撤回。

| 顶层字段 | 类型 | 必填 | 说明 |
| --- | --- | --- | --- |
| `id` | string | 否 | 1–128 个字母、数字、点、下划线或连字符；缺省由客户端生成 UUID |
| `operation` | string | 是 | 推荐 `execute_plan`；也接受兼容操作 |
| `args` | object | 否 | `execute_plan` 需要 `steps` 数组 |
| `preview` | boolean | 否 | `true` 仅校验与返回计划，不写 Revit；默认 `false` |
| `document_title` | string/null | 否 | 只在当前活动文档标题完全匹配时执行 |
| `source` | string | 否 | 调用方标识，用于日志审计 |

长度数值默认单位为毫米；也可传 `"3600mm"`、`"3.6m"`、`"3600毫米"` 或 `"3.6米"`。点统一写为 `{ "x": 0, "y": 0, "z": 0 }`，默认也是毫米，坐标属于当前项目内部坐标系。

## 计划步骤

每个 `steps[]` 结构如下：

```json
{
  "id": "可选且唯一的步骤标识",
  "operation": "受控原子操作",
  "args": {}
}
```

后续步骤的 `element_ids`、`elements` 或 `targets` 可写 `"$步骤ID"`，引用前一步返回的 `element_id` / `element_ids`。预览时新元素没有真实 ID，依赖该 ID 的验证会标记为 `deferred`；实际执行时会解析。

| operation | 写模型 | 关键 args | 说明 |
| --- | --- | --- | --- |
| `query_document` | 否 | 无 | 当前项目、活动视图、只读状态 |
| `query_catalog` | 否 | `kind` | `levels`、`categories`、`views`、`families`、`types`、`mep_types` |
| `query_elements` | 否 | 可选 `category`、`element_ids`、`name_contains` | 查询元素与指定参数；`limit` 1–500 |
| `create_level` | 是 | `elevation_mm` | 可选 `name` |
| `create_grid` | 是 | `start`、`end` | 直线轴网；两点 Z 必须相同 |
| `create_wall` | 是 | `start`、`end` | `level`、`type` / `type_id`、`height_mm`、`thickness_mm` 可选 |
| `create_direct_shape` | 是 | `geometry` | 通用实体：`box`、`cylinder`、`extrusion` |
| `create_mep_curve` | 是 | `kind`、`start`、`end` | `pipe`、`duct`、`conduit`、`cable_tray` |
| `connect_mep` | 是 | `element_a`、`element_b` | `fitting`: `auto`、`direct`、`elbow`、`union`、`tee` |
| `place_family_instance` | 是 | `family`、`type`、`point` | 当前支持非宿主的 OneLevelBased / TwoLevelsBased 族 |
| `create_structural_member` | 是 | `kind`、`family`、`type` | `beam`、`brace` 用 `start/end`；`column` 用 `point` |
| `set_parameters` | 是 | `targets`、`parameters` | 批量设置实例/类型参数 |
| `delete_elements` | 是 | `targets` | 删除指定元素 |
| `select_elements` | 否 | `targets` | 可选 `show=true` 定位到视图 |

`query_catalog` 与 `query_elements` 是计划生成前最重要的两步：先查到真实的族、类型、标高和元素 ID，再写创建或修改步骤，避免猜测项目里有什么。

## 通用实体几何

`create_direct_shape.geometry` 是原语数组。一个 DirectShape 可以放多个原语，适合桁架、异形钢构、混凝土支座、设备占位和无法用已载入族表达的临时几何。

```json
{
  "operation": "create_direct_shape",
  "args": {
    "name": "钢管构件",
    "category": "OST_GenericModel",
    "geometry": [
      {
        "kind": "cylinder",
        "start": { "x": 0, "y": 0, "z": 2500 },
        "end": { "x": 6000, "y": 0, "z": 5500 },
        "diameter_mm": 200
      },
      {
        "kind": "extrusion",
        "profile": [
          { "x": 0, "y": 0, "z": 0 },
          { "x": 500, "y": 0, "z": 0 },
          { "x": 500, "y": 500, "z": 0 },
          { "x": 0, "y": 500, "z": 0 }
        ],
        "direction": { "x": 0, "y": 0, "z": 1 },
        "length_mm": 3000
      }
    ]
  }
}
```

`box` 使用 `min` 和 `max` 两点；`cylinder` 使用任意方向的 `start` / `end` 和 `diameter_mm`；`extrusion` 使用闭合 `profile`、单位方向向量和 `length_mm`。

## 机电与参数

`create_mep_curve` 的 `type` / `type_id` 指向实际 PipeType、DuctType、ConduitType 或 CableTrayType。`pipe` 与 `duct` 还需要已有的 `system_type` / `system_type_id`。不指定时使用项目中排序后的第一个可用类型，因此生产计划建议先查询并显式传入名称或 ID。

```json
{
  "operation": "create_mep_curve",
  "args": {
    "kind": "pipe",
    "type": "标准",
    "system_type": "生活给水",
    "level": "标高 1",
    "start": { "x": 0, "y": 0, "z": 3000 },
    "end": { "x": 6000, "y": 0, "z": 3000 },
    "diameter_mm": 100
  }
}
```

`set_parameters.parameters` 的键是项目中显示的参数名，或 `BIP:RBS_PIPE_DIAMETER_PARAM` 形式的 BuiltInParameter 名。Double 参数的裸数值按 Revit 内部单位解释；长度或角度请显式传单位，避免歧义：

```json
{
  "operation": "set_parameters",
  "args": {
    "targets": ["$pipe_1"],
    "parameters": {
      "注释": "AI 生成管道",
      "BIP:RBS_PIPE_DIAMETER_PARAM": { "value": 100, "unit": "mm" }
    }
  }
}
```

单位对象支持 `internal`、`mm`、`m`、`ft`、`deg`、`rad`。

## 传输层

### MCP

MCP 客户端使用 `revit_execute_plan`，也可用通用 `revit_command`。进程参数：

```powershell
node "$env:LOCALAPPDATA\RevitCommandBridge\2020\scripts\revit-mcp-server.mjs"
```

### REST

REST 只监听本机 `127.0.0.1:8765`：

| 方法与路径 | 结果 |
| --- | --- |
| `GET /health` | REST 网关与 Revit 插件心跳状态 |
| `GET /capabilities` | 协议版本和顶层 operation 列表 |
| `POST /commands` | 排队命令；加 `?wait_seconds=60` 可同步等待 |
| `GET /commands/{id}` | 获取结果；未完成时返回 HTTP 202 |

```powershell
node "$env:LOCALAPPDATA\RevitCommandBridge\2020\scripts\revit-http-gateway.mjs"
```

### 文件队列

根目录默认为 `%LOCALAPPDATA%\RevitCommandBridge`：

```text
inbox/       新请求：{id}.request.json
processing/  Revit 已领取：{id}.processing.json
outbox/      最终结果：{id}.result.json
archive/     完成或失败的原请求
logs/        bridge.log
status.json  Revit 插件状态与心跳
```

客户端必须先写临时文件，再原子重命名为 `.request.json`。Revit 在主线程串行执行，启动时会恢复异常中断时残留的 processing 请求。

## 兼容操作

下列 1.x 操作继续可用：`health`、`list_levels`、`list_wall_types`、`new_project`、`create_level`、`create_grid`、`create_wall`、`create_rectangle_walls`。它们适合已有脚本；新增专业能力应进入 `execute_plan`，不要继续增长单用途顶层命令。
