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

确认响应中的计划正确后，将同一 JSON 的 `preview` 改为 `false` 提交。普通计划中的写步骤会在一个 Revit Transaction 中执行：任一步失败会回滚前面的写入；成功时可在 Revit 中一次 `Ctrl+Z` 撤回。`export` 和 `save_document` 会写出外部文件，必须作为只含该步骤的独立计划执行。

| 顶层字段 | 类型 | 必填 | 说明 |
| --- | --- | --- | --- |
| `id` | string | 否 | 1–128 个字母、数字、点、下划线或连字符；缺省由客户端生成 UUID |
| `operation` | string | 是 | 推荐 `execute_plan`；也接受兼容操作 |
| `args` | object | 否 | `execute_plan` 需要 `steps` 数组 |
| `preview` | boolean | 否 | `true` 仅校验与返回计划，不写 Revit；默认 `false` |
| `document_title` | string/null | 否 | 只在当前活动文档标题完全匹配时执行 |
| `source` | string | 否 | 调用方标识，用于日志审计 |

长度数值默认单位为毫米；也可传 `"3600mm"`、`"3.6m"`、`"3600毫米"` 或 `"3.6米"`。点统一写为 `{ "x": 0, "y": 0, "z": 0 }`，默认也是毫米，坐标属于当前项目内部坐标系。

顶层 `new_project` 可传 `save_path: "C:\\...\\项目.rvt"` 与 `overwrite_file: true/false`。提供 `save_path` 时，桥接会创建、保存并激活项目，下一条命令即可继续建模；未传时 Revit 只创建未保存文档，用户可在界面中激活它。

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
| `query_catalog` | 否 | `kind` | `levels`、`categories`、`views`、`sheets`、`schedules`、`view_types`、`title_blocks`、`text_types`、`filled_region_types`、`revisions`、`families`、`types`、`mep_types`、`links`（链接模型清单） |
| `query_elements` | 否 | 可选 `category`、`element_ids`、`name_contains` | 查询元素与指定参数；`limit` 1–500 |
| `query_references` | 否 | `element_ids` 或 `targets` | 查询元素面/边的稳定引用，供 `create_dimension`、`create_tag` 使用 |
| `query_parameters` | 否 | `element_id` | 枚举元素全部参数（名称 / 值 / 单位 / 只读 / BIP）；可选 `name_contains`、`include_read_only` |
| `query_geometry` | 否 | `element_id` | 几何感知：`detail` = `bbox`、`solid_summary`、`faces` |
| `query_room` | 否 | 可选 `point` / `level` | `point` 点找房间；缺省列出房间（含边界顶点） |
| `check_interferences` | 否 | `element_ids` | 碰撞检查；可选 `against_ids`、`include_links`；候选 >500 需显式 `against_ids` |
| `query_mep_network` | 否 | `element_id` | 沿连接件 BFS 遍历管网拓扑（nodes + edges）；可选 `max_depth`（默认 100） |
| `query_view_range` | 否 | `view_id` | 平面视图范围四槽位（top / cut_plane / bottom / view_depth：标高 + 偏移） |
| `create_level` | 是 | `elevation_mm` | 可选 `name` |
| `create_grid` | 是 | `start`、`end` | 直线轴网；两点 Z 必须相同 |
| `create_wall` | 是 | `start`、`end` | `level`、`type` / `type_id`、`height_mm`、`thickness_mm` 可选 |
| `create_floor` | 是 | `boundary` | 原生楼板；`level`、`type` / `type_id`、`offset_mm`、`structural` 可选 |
| `create_room` | 是 | `point` | 在 `level` 放置房间；可选 `name`、`number` |
| `create_space` | 是 | `point` | 在 `level` 放置 MEP 空间；可选 `name`、`number` |
| `create_model_curve` | 是 | `start`、`end` | 创建直线模型线和所需工作平面 |
| `create_direct_shape` | 是 | `geometry` | 通用实体：`box`、`cylinder`、`extrusion` |
| `create_swept_shape` | 是 | `path`、`section` | 路径放样 DirectShape；截面 `shape`: `rect`、`rect_ring`、`circle`、`circle_ring`、`horseshoe`（`width_mm`/`height_mm`/`wall_thickness_mm`） |
| `create_mep_curve` | 是 | `kind`、`start`、`end` | `pipe`、`duct`、`conduit`、`cable_tray`；可选 `slope`（百分比，`slope_unit` 支持 permille） |
| `connect_mep` | 是 | `element_a`、`element_b` | `fitting`: `auto`、`direct`、`elbow`、`union`、`tee`、`reducer`、`cross`（四通用 `element_c`/`element_d`）；可选 `extend_to_intersection=true` 延伸两管到交点再接配件 |
| `create_mep_system` | 是 | `domain`、`name` | `piping` / `mechanical`；可选 `system_type`、`members`（逐个指派成员） |
| `place_family_instance` | 是 | `family`、`type` | 支持非宿主、宿主、面宿主、工作平面、视图、线基和自适应族；按放置类型填写 `point`、`level`、`host_id`、`view_id`、`start/end` 或 `adaptive_points` |
| `load_family` | 是 | `path` | 从 .rfa 加载族（静默覆盖）；可选 `symbol` 激活指定类型；返回全部 `symbol_names` |
| `create_structural_member` | 是 | `kind`、`family`、`type` | `beam`、`brace` 用 `start/end`；`column` 用 `point` |
| `create_view` | 是 | `kind` | `3d`、`floor_plan`、`ceiling_plan`、`structural_plan`；平面类需要 `level` |
| `create_drafting_view` | 是 | 可选 `type_id`、`name` | 创建绘图视图 |
| `create_section_view` | 是 | `origin`、可选 `direction`、`up`、`width_mm`、`height_mm`、`depth_mm` | 创建剖面或详图视图 |
| `create_elevation_view` | 是 | `plan_view_id`、`origin` | 在平面视图中创建立面；`index` 0–3 |
| `create_callout` | 是 | `parent_view_id`、`start`、`end` | 创建详图索引 |
| `duplicate_view` | 是 | `view_id` | `option`: `duplicate`、`as_duplicate`、`without_detailing`、`as_dependent`、`with_detailing`；可选 `view_template` 复制后套样板 |
| `create_view_template` | 是 | `view_id` | 从视图创建样板 |
| `create_sheet` | 是 | 无 | 可选 `title_block_family`、`title_block_type`、`sheet_number`、`name` |
| `place_view_on_sheet` | 是 | `sheet_id`、`view_id`、`point` | 图纸和视图可使用 `"$步骤ID"` 引用 |
| `create_detail_curve` | 是 | `view_id`、`start`、`end` | 在视图中创建详图线 |
| `create_text_note` | 是 | `view_id`、`point`、`text` | 在视图中创建文字注释 |
| `create_dimension` | 是 | `view_id`、`start`、`end`、`references` | 使用 `query_references` 返回的稳定引用创建尺寸 |
| `create_tag` | 是 | `view_id`、`reference`、`point` | 按类别/指定标签类型放置标签 |
| `create_filled_region` | 是 | `view_id`、`filled_region_type_id`、`boundary` | 创建填充区域 |
| `create_revision` | 是 | 可选 `description`、`revision_date`、`issued` | 创建修订记录 |
| `create_revision_cloud` | 是 | `view_id`、`revision_id`、`boundary` | 创建修订云线 |
| `create_schedule` | 是 | `kind` | 普通、材质、关键、视图/图纸/修订明细表；可传 `fields` |
| `place_schedule_on_sheet` | 是 | `sheet_id`、`schedule_id`、`point` | 图纸放置明细表 |
| `set_view_properties` | 是 | `view_id` | 设置比例、裁剪、视图样板、细节级别和显示样式 |
| `create_opening` | 是 | `host_id`、`start`、`end` | `kind=wall`（默认，两点矩形墙洞）；`vertical`（楼板竖直洞口，`corner_1`/`corner_2`）；`shaft`（竖井，`bottom_level`/`top_level`/`boundary`） |
| `set_parameters` | 是 | `targets`、`parameters` | 批量设置实例/类型参数 |
| `duplicate_type` | 是 | `type_id` / `targets`、`new_name` | 复制 ElementType 生成新类型；可选 `parameters` 批量赋值 |
| `manage_schema_data` | 是 | `targets`、`action` | 元素 Extensible Storage：`set` / `get` / `clear` / `transport`；`set` 传 `values`（map&lt;string,string&gt;），`transport` 从 `source_element_id` 搬运 |
| `manage_family_parameters` | 是 | `family_id`、`actions` | 族文档参数 `add` / `rename` / `remove` / `set_formula`（通过 `load_family` 打开并 EditFamily 后回载） |
| `manage_project_parameters` | 是 | `action` | 项目参数 `list` / `add_shared` / `delete`；`add_shared` 需 `name`、`categories`、`group`、`type`、`instance` |
| `create_insulation` | 是 | `targets`、`thickness_mm` | 为 MEP 管件/管线添加保温层或衬里；可选 `type` / `insulation_type` |
| `set_element_overrides` | 是 | `view_id`、`targets`、`overrides` | 视图中设置图元图形替换（颜色 / 线宽 / 半色调 / 透明度 / 表面色） |
| `set_category_overrides` | 是 | `view_id`、`category`、`overrides` | 视图中设置类别图形替换 |
| `manage_view_filters` | 是 | `view_id`、`action` | 视图过滤器 `add` / `remove` / `delete` / `clear`；`add` 可选 `name`、`categories`、`rules`、`hidden` |
| `set_view_range` | 是 | `view_id`、`slots` | 设置平面视图范围四槽位（top / cut_plane / bottom / view_depth：level + offset_mm） |
| `manage_schedule_fields` | 是 | `schedule_id`、`action` | 明细表字段 `add_field` / `remove_field` / `hide_field` / `show_field` / `add_filter` / `sort` / `set_itemized` |
| `manage_graphics_resources` | 是 | `action`、`name` | 图形资源 `line_style` / `fill_pattern` 创建（已存在则复用） |
| `transform_elements` | 是 | `element_ids`、`mode` | `move`（`translation`）、`copy`（`translation`，返回新元素）、`rotate`（`axis_origin`/`axis_direction`/`angle` 度）、`mirror`（`plane_point`/`plane_normal`，返回镜像副本） |
| `rename_element` | 是 | `element_ids` | 单目标用 `name`；批量用 `prefix`（可选 `id_suffix=true` 变为 前缀+ID） |
| `set_element_curve` | 是 | `element_id`、`start`、`end` | 修改线状图元（墙 / 管 / 模型线）走向 |
| `delete_elements` | 是 | `targets` | 删除指定元素 |
| `select_elements` | 否 | `targets` | 可选 `show=true` 定位到视图 |
| `export` | 否* | `format`、`output_path` | 单独执行；支持 image/png/jpg、dwg、dxf、ifc、schedule_csv |
| `save_document` | 否* | 可选 `path`、`overwrite_file` | 单独执行；保存当前 `.rvt` |

`query_catalog` 与 `query_elements` 是计划生成前最重要的两步：先查到真实的族、类型、标高和元素 ID，再写创建或修改步骤，避免猜测项目里有什么。

## 出图、注释与交付

推荐顺序：先 `query_catalog(kind=view_types|title_blocks|text_types|filled_region_types|revisions)`，再创建视图/图纸；尺寸与标签先用 `query_references` 读取目标元素的 `stable_reference`。`create_dimension.references` 至少传两个稳定引用；`create_tag.reference` 传一个稳定引用，`tag_type_id` 可选，不传时由 Revit 按类别选择可用标签。

`create_section_view` 的 `origin` 是剖面框中心，`direction` 是视线方向，`up` 是纸面向上方向，长度参数控制框宽、高、深。`create_elevation_view` 的 `plan_view_id` 是承载立面符号的平面视图。`set_view_properties` 支持：

```json
{
  "operation": "set_view_properties",
  "args": {
    "view_id": 12345,
    "scale": 100,
    "detail_level": "Fine",
    "display_style": "HLR",
    "crop_active": true,
    "crop_visible": false,
    "crop_box": {
      "min": { "x": -5000, "y": -5000, "z": -1000 },
      "max": { "x": 5000, "y": 5000, "z": 5000 }
    },
    "view_template_id": 23456
  }
}
```

设置 `clear_view_template=true` 可清除当前视图样板，且不能与 `view_template_id` 同时传入。

`create_schedule.kind` 支持 `regular`、`material_takeoff`、`key`、`view_list`、`sheet_list`、`revision`。普通/材质/关键明细表需要 `category`，`fields` 是项目中可用的字段显示名；先预览，再按实际项目语言和样板微调字段名。

导出示例必须单独提交：

```json
{
  "operation": "execute_plan",
  "preview": false,
  "args": {
    "steps": [
      {
        "operation": "export",
        "args": {
          "format": "png",
          "active_view": true,
          "output_path": "C:\\RCB-Exports\\current-view"
        }
      }
    ]
  }
}
```

`image/png/jpg` 可传 `view_ids` 或 `active_view=true`；`dwg/dxf` 需要 `view_ids` 或 `active_view=true`；`ifc` 可选 `filter_view_id`；`schedule_csv` 需要 `schedule_id`，可选 `delimiter` 与 `title`。插件仅写调用方指定的输出目录，不上传文件。

## 族文件工作流

族文件是独立 Revit 文档，不能与项目建模计划混在一个项目 Transaction 内。因此使用顶层操作：

1. `list_family_templates` 查询本机 `.rft` 样板；不需要打开项目。
2. `create_family` 创建 `.rfa`，写入参数、类型和实体，保存后可自动载入当前项目并放置。
3. `load_family` 载入已有 `.rfa`。

`create_family` 的高频参数：

| args 字段 | 说明 |
| --- | --- |
| `family_name` | 必填；族名称和默认文件名 |
| `template_path` | 可选 `.rft` 绝对路径；缺省自动选择本机“公制常规模型 / Metric Generic Model” |
| `save_path` | 可选 `.rfa` 输出路径；缺省保存到文档目录 `RevitCommandBridge\\Families` |
| `category` | 可选 `OST_GenericModel`、`OST_MechanicalEquipment` 等可由样板支持的类别 |
| `parameters` | 参数项：`name`、`type`、`instance`、`group`、`default`、`formula` |
| `types` | 类型项：`name`、`values`；未传时创建“默认”类型 |
| `geometry` | `box`、`cylinder`、`extrusion` 原语数组 |
| `load_into_project` | 默认 `true`；保存后自动载入当前项目 |
| `place` | 可选放置参数，例如 `{ "point": {"x":0,"y":0,"z":0}, "level":"标高 1" }` |

创建族前请先保存当前 `.rvt` 项目；Revit 新建族时会临时打开族文档，桥接在完成后自动切回原项目。

支持的普通参数类型：`length`、`area`、`volume`、`angle`、`number`、`text`、`multiline_text`、`integer`、`yesno`、`material`、`url`。族参数长度裸数按 mm，面积裸数按 mm²，体积裸数按 mm³，角度裸数按度。

预览示例：

```json
{
  "operation": "create_family",
  "preview": true,
  "args": {
    "family_name": "RCB_设备基础",
    "category": "OST_GenericModel",
    "parameters": [
      { "name": "宽度", "type": "length", "default": "1200mm" },
      { "name": "说明", "type": "text", "instance": true, "default": "设备基础" }
    ],
    "types": [
      { "name": "默认", "values": { "宽度": "1200mm" } }
    ],
    "geometry": [
      { "kind": "box", "min": {"x": -600, "y": -400, "z": 0}, "max": {"x": 600, "y": 400, "z": 300} }
    ],
    "load_into_project": true
  }
}
```

完成预览后将 `preview` 改为 `false`。若目标 `.rfa` 已存在，显式传 `overwrite_file=true` 才会覆盖。

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

REST 默认监听本机 `127.0.0.1:8765`（Revit 2020）。版本端口映射：`2020=8765`、`2021=8766`……`2026=8771`。`REVIT_BRIDGE_PORT` 环境变量可覆盖：

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

下列 1.x 操作继续可用：`health`、`list_levels`、`list_wall_types`、`new_project`、`create_level`、`create_grid`、`create_wall`、`create_rectangle_walls`。族文档使用专用顶层操作：`list_family_templates`、`create_family`、`load_family`；其余新增专业能力进入 `execute_plan`，不要继续增长单用途顶层命令。
