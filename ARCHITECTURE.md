# 通用 Revit AI 执行底座

这不是"给每一种构件写一个按钮"的插件。它把任意客户端生成的建模意图收敛成 `execute_plan`，再由受控原子操作在当前 Revit 项目内执行。

JSON 计划、CLI、REST 和 MCP 契约不绑定任何模型厂商，也不绑定 Revit 年份；每个 Revit 年份使用对应 API 编译出的适配 DLL，并使用独立本地队列。

## 日常工作方式

1. 在 Revit 中打开或新建项目。
2. 让 Codex、WorkBuddy、DeepSeek Harness 或其它 Agent 先调用查询步骤，认识当前项目的标高、族、类型与元素。
3. Agent 生成一个 `execute_plan`，先以 `preview=true` 提交。
4. 预览正确后，以同一计划 `preview=false` 执行。
5. 所有普通写步骤要么作为一个 Revit Transaction 提交，要么整组回滚；Revit 的一次撤销即可撤回。
6. `export` 与 `save_document` 具有外部文件副作用，必须单独提交，不能和建模事务混用。

```mermaid
flowchart LR
    A["自然语言 / 任意 Agent"] --> B["统一 JSON 建模计划"]
    B --> C["MCP / REST / CLI"]
    C --> D["本地文件队列"]
    D --> E["Revit ExternalEvent 主线程"]
    E --> F["受控原子操作"]
    F --> G["Revit Transaction + 结果 JSON"]
```

## 原子能力边界

| 范围 | 原子操作 | 典型用途 |
| --- | --- | --- |
| 项目理解 | `query_document`、`query_catalog`、`query_elements`、`query_references` | 查标高、族/类型、视图、现有构件/参数及可用于标注的稳定几何引用 |
| 建筑基础 | `create_level`、`create_grid`、`create_wall`、`create_floor`、`create_opening`、`create_model_curve` | 标高、轴网、直墙、楼板、墙洞口、模型线 |
| 空间与数据 | `create_room`、`create_space`、`set_parameters` | 房间、MEP 空间及项目数据写入 |
| 异形和临时几何 | `create_direct_shape` | 盒体、任意方向圆管、拉伸体；可组合出桁架、支座、设备占位几何 |
| 机电线性构件 | `create_mep_curve`、`connect_mep` | 管道、风管、线管、桥架及两/三接口连接 |
| 族和结构 | `list_family_templates`、`create_family`、`load_family`、`place_family_instance`、`create_structural_member` | `.rft` 样板、`.rfa` 参数/类型/几何、载入、非宿主/宿主/面宿主/工作平面/视图/线基/自适应族、梁、斜撑、柱 |
| 出图视图 | `create_view`、`create_drafting_view`、`create_section_view`、`create_elevation_view`、`create_callout`、`duplicate_view`、`create_view_template` | 平面/3D/绘图/剖面/立面/详图、复制、样板 |
| 注释与图纸 | `create_detail_curve`、`create_text_note`、`create_dimension`、`create_tag`、`create_filled_region`、`create_revision`、`create_revision_cloud`、`create_sheet`、`place_view_on_sheet` | 详图线、文字、尺寸、标签、填充、修订云线和图纸布置 |
| 表格与交付 | `create_schedule`、`place_schedule_on_sheet`、`set_view_properties`、`export`、`save_document` | 明细表、图纸明细表、视图比例/裁剪/样板、图像/DWG/DXF/IFC/CSV 导出、保存项目 |
| 修改与呈现 | `set_parameters`、`delete_elements`、`select_elements` | 批量改参数、删除、定位到视图 |

这组能力可以组合出未来的大多数重复工作：例如"创建 24 根不同标高的管道、补阀门、连接、写系统编号、选中复核"是一个计划，不是 24 个新插件命令。

## 已知边界

| 状态 | 范围 | 说明 |
| --- | --- | --- |
| [V] | Revit 2020 二进制 | 使用本机 RevitAPI 20.0.0.377 编译；每个 Revit 年份使用独立 `%LOCALAPPDATA%\RevitCommandBridge\<year>` 队列。|
| [V] | 族文件工作流 | `list_family_templates`、`create_family`、`load_family` 支持样板查询、参数/类型/实体、保存、载入和可选放置。|
| [V] | 族放置 | `place_family_instance` 已覆盖 OneLevelBased、TwoLevelsBased、OneLevelBasedHosted、WorkPlaneBased、ViewBased、CurveBased、CurveBasedDetail、CurveDrivenStructural 与 Adaptive。|
| [T] | 特定受限宿主族 | Revit 样板本身的宿主规则、连接器、嵌套族、幕墙与楼梯等专用对象需要按具体 API 原子操作继续增加。|
| [T] | 复杂 MEP 自动布线与碰撞绕行 | 当前支持直线管段与连接；自动路径、管综规则、避障属于上层规划器能力。|
| [T] | Revit 2021–2024 DLL 包 | 构建脚本可按目标年份调用 .NET Framework 适配构建；需要以目标年份 RevitAPI 再构建和真机验证。|

## 为什么不让 AI 直接跑 C# 或 Dynamo

AI 可以产生错误的任意代码。桥接只接收白名单原子操作和 JSON 参数，因此能检查目标文档、预览、事务、长度单位、元素 ID，并留下队列和结果记录。Dynamo 可以作为独立工具使用，但不是这套底座的前置条件。

完整请求格式见 [PROTOCOL.md](./PROTOCOL.md)。
