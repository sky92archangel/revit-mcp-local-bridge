import readline from "node:readline";
import path from "node:path";
import {
  BridgeClientError,
  COMMAND_PROTOCOL,
  enqueueCommand,
  isBridgeRunning,
  readBridgeStatus,
  resolveBridgeRoot,
  supportedOperations,
  waitForCommandResult,
} from "./bridge-client.mjs";

const MCP_PROTOCOL_VERSION = "2025-03-26";
const SUPPORTED_PROTOCOL_VERSIONS = new Set(["2024-11-05", "2025-03-26", "2025-06-18"]);
const rootDirectory = resolveBridgeRoot(process.env.REVIT_COMMAND_BRIDGE_ROOT);
const revitVersion = process.env.REVIT_COMMAND_BRIDGE_VERSION || path.basename(rootDirectory);
const controlFields = new Set(["preview", "document_title", "documentTitle", "wait_seconds", "source"]);

const input = readline.createInterface({
  input: process.stdin,
  crlfDelay: Infinity,
});

for await (const line of input) {
  if (!line.trim()) {
    continue;
  }

  await handleLine(line);
}

async function handleLine(line) {
  let request;
  try {
    request = JSON.parse(line);
  } catch (error) {
    writeMessage(jsonRpcError(null, -32700, "Parse error", error.message));
    return;
  }

  if (!isRecord(request) || request.jsonrpc !== "2.0" || typeof request.method !== "string") {
    writeMessage(jsonRpcError(request && request.id !== undefined ? request.id : null, -32600, "Invalid Request"));
    return;
  }

  const isNotification = request.id === undefined;
  try {
    const result = await dispatch(request.method, request.params ?? {});
    if (!isNotification) {
      writeMessage({ jsonrpc: "2.0", id: request.id, result });
    }
  } catch (error) {
    if (!isNotification) {
      const rpcError = error instanceof McpRpcError
        ? jsonRpcError(request.id, error.code, error.message, error.data)
        : jsonRpcError(request.id, -32603, "Internal error", error instanceof Error ? error.message : String(error));
      writeMessage(rpcError);
    }
    console.error(error);
  }
}

async function dispatch(method, params) {
  switch (method) {
    case "initialize":
      return initialize(params);
    case "notifications/initialized":
      return {};
    case "ping":
      return {};
    case "tools/list":
      return { tools: toolDefinitions() };
    case "tools/call":
      return callTool(params);
    default:
      throw new McpRpcError(-32601, `Method not found: ${method}`);
  }
}

function initialize(params) {
  if (!isRecord(params)) {
    throw new McpRpcError(-32602, "initialize 参数必须是对象。");
  }

  const requestedVersion = String(params.protocolVersion || "");
  return {
    protocolVersion: SUPPORTED_PROTOCOL_VERSIONS.has(requestedVersion) ? requestedVersion : MCP_PROTOCOL_VERSION,
    capabilities: {
      tools: {
        listChanged: false,
      },
    },
    serverInfo: {
      name: "revit-command-bridge",
      version: `0.5.0-revit${revitVersion}`,
    },
    instructions: "通过受控 Revit 命令操作当前打开项目。优先使用 revit_execute_plan，把建筑、结构、机电、房间空间、注释、明细表、视图图纸、参数等原子步骤组合为一个 all_or_nothing 计划；族文件使用 revit_create_family / revit_load_family。export/save_document 必须单独执行。写入先 preview=true，确认后再提交 preview=false。",
  };
}

async function callTool(params) {
  if (!isRecord(params) || typeof params.name !== "string") {
    throw new McpRpcError(-32602, "tools/call 缺少 name。");
  }

  const argumentsValue = params.arguments ?? {};
  if (!isRecord(argumentsValue)) {
    throw new McpRpcError(-32602, "tools/call arguments 必须是对象。");
  }

  if (params.name === "revit_command") {
    const operation = String(argumentsValue.operation || "").trim();
    if (!operation) {
      return toolFailure("revit_command 缺少 operation。");
    }
    return invokeRevit({
      operation,
      args: argumentsValue.args ?? {},
      preview: argumentsValue.preview ?? false,
      document_title: argumentsValue.document_title ?? argumentsValue.documentTitle ?? null,
      source: "mcp",
    }, argumentsValue.wait_seconds);
  }

  const directOperation = directToolOperation(params.name);
  if (directOperation == null) {
    return toolFailure(`未知 Revit 工具：${params.name}`);
  }

  const args = {};
  for (const [key, value] of Object.entries(argumentsValue)) {
    if (!controlFields.has(key)) {
      args[key] = value;
    }
  }
  return invokeRevit({
    operation: directOperation,
    args,
    preview: argumentsValue.preview ?? false,
    document_title: argumentsValue.document_title ?? argumentsValue.documentTitle ?? null,
    source: "mcp",
  }, argumentsValue.wait_seconds);
}

async function invokeRevit(command, waitSecondsValue) {
  const waitSeconds = parseWaitSeconds(waitSecondsValue ?? 60);
  const status = await readBridgeStatus({ rootDirectory });
  if (!isBridgeRunning(status)) {
    return toolFailure("Revit 命令桥未运行。先启动 Revit，打开项目，然后在 Revit 功能区点击“启动桥接”。", { status });
  }

  let queued;
  try {
    queued = await enqueueCommand(command, { rootDirectory, source: "mcp" });
  } catch (error) {
    return toolFailure(error instanceof Error ? error.message : String(error));
  }

  let result;
  try {
    result = await waitForCommandResult(queued.id, {
      rootDirectory,
      waitMilliseconds: waitSeconds * 1000,
    });
  } catch (error) {
    return toolFailure(`读取 Revit 结果失败：${error instanceof Error ? error.message : String(error)}`);
  }

  if (result == null) {
    return toolSuccess({
      ok: true,
      state: "queued",
      id: queued.id,
      operation: queued.request.operation,
      message: "命令已排队，尚未在等待期内完成。可稍后通过 REST GET /commands/{id} 查询。",
    });
  }

  return result.ok ? toolSuccess(result) : toolFailure(result.message || "Revit 命令失败。", result);
}

function toolDefinitions() {
  const controls = controlProperties();
  return [
    {
      name: "revit_command",
      description: "调用 Revit Command Bridge 的受控命令。优先 operation=execute_plan；兼容旧的查询和墙体命令。写操作建议先 preview=true。",
      inputSchema: {
        type: "object",
        properties: {
          operation: { type: "string", enum: supportedOperations() },
          args: { type: "object", description: "各 operation 的参数对象；长度默认使用 mm，可传数值或如 '3.6m' 的字符串。" },
          ...controls,
        },
        required: ["operation"],
        additionalProperties: false,
      },
    },
    directTool("revit_health", "health", "读取桥接状态与当前 Revit 项目文档信息。", {}, []),
    directTool("revit_list_family_templates", "list_family_templates", "列出本机 Revit 族样板；不需要打开项目。未传 template_root 时自动读取 Revit 默认族样板目录。", {
      template_root: { type: "string", description: "可选 .rft 族样板根目录。" },
      limit: { type: "integer", minimum: 1, maximum: 1000, default: 200, description: "最多返回的族样板数量。" },
    }, []),
    directTool("revit_create_family", "create_family", "从 .rft 样板创建可参数化 .rfa 族；支持参数、类型、box/cylinder/extrusion 实体，保存后可载入并放置到当前项目。", familyProperties(), ["family_name"]),
    directTool("revit_load_family", "load_family", "将已有 .rfa 族载入当前项目。", {
      family_path: { type: "string", description: "要载入的 .rfa 文件绝对路径。" },
      overwrite_parameter_values: { type: "boolean", default: false, description: "同名族已存在时是否覆盖已有类型参数值。" },
    }, ["family_path"]),
    directTool("revit_execute_plan", "execute_plan", "执行通用 Revit 建模/出图计划。每个 steps 项是受控原子操作；支持查询、建筑/结构/机电、房间空间、模型线、视图/图纸/明细表、文字标注、族实例、参数、删除和选中。普通写步骤作为一个 all_or_nothing Revit 事务执行；export 或 save_document 必须单独作为一个计划执行。先用 preview=true。", {
      steps: {
        type: "array",
        minItems: 1,
        maxItems: 500,
        items: {
          type: "object",
          properties: {
            id: { type: "string", description: "可选步骤 ID；后续 element_ids 可用 $步骤ID 引用。" },
            operation: { type: "string", description: "原子操作，例如 query_catalog、create_direct_shape、create_mep_curve、place_family_instance、set_parameters。" },
            args: { type: "object", description: "该原子操作的参数。长度默认 mm；点使用 {x,y,z}。", additionalProperties: true },
          },
          required: ["operation"],
          additionalProperties: false,
        },
        description: "串行执行的受控原子步骤数组。",
      },
    }, ["steps"]),
    directTool("revit_list_levels", "list_levels", "列出当前项目的标高。", {}, []),
    directTool("revit_list_wall_types", "list_wall_types", "列出当前项目的基本墙类型和厚度。", {}, []),
    directTool("revit_new_project", "new_project", "创建一个未保存的 Revit 项目。可提供 template_path；未提供时创建公制空项目。", {
      template_path: { type: "string", description: "可选 .rte 项目样板的绝对路径。" },
      save_path: { type: "string", description: "可选 .rvt 输出路径；提供后会保存并激活项目，后续可立刻继续建模。" },
      overwrite_file: { type: "boolean", default: false, description: "save_path 已存在时是否覆盖。" },
    }, []),
    directTool("revit_create_level", "create_level", "创建标高。先用 preview=true 检查。", {
      elevation_mm: lengthSchema("标高高程；例如 3600 或 '3.6m'。"),
      name: { type: "string", description: "可选标高名称。" },
    }, ["elevation_mm"]),
    directTool("revit_create_grid", "create_grid", "创建直线轴网。先用 preview=true 检查。", {
      x1_mm: lengthSchema("起点 X 坐标。"),
      y1_mm: lengthSchema("起点 Y 坐标。"),
      x2_mm: lengthSchema("终点 X 坐标。"),
      y2_mm: lengthSchema("终点 Y 坐标。"),
      name: { type: "string", description: "可选轴网名称。" },
    }, ["x1_mm", "y1_mm", "x2_mm", "y2_mm"]),
    directTool("revit_create_wall", "create_wall", "创建一面直墙；默认高 3000mm、厚 200mm。先用 preview=true 检查。", wallProperties(), ["x1_mm", "y1_mm", "x2_mm", "y2_mm"]),
    directTool("revit_create_rectangle_walls", "create_rectangle_walls", "创建四面闭合矩形墙；默认高 3000mm、厚 200mm。先用 preview=true 检查。", rectangleWallProperties(), ["width_mm", "depth_mm"]),
  ];
}

function directTool(name, operation, description, properties, required) {
  return {
    name,
    description,
    inputSchema: {
      type: "object",
      properties: {
        ...properties,
        ...controlProperties(),
      },
      required,
      additionalProperties: false,
    },
  };
}

function controlProperties() {
  return {
    preview: { type: "boolean", default: false, description: "true 时仅返回建模计划，不修改 Revit 模型。" },
    document_title: { type: "string", description: "可选；仅在当前 Revit 项目标题匹配时执行。" },
    wait_seconds: { type: "number", minimum: 0, maximum: 120, default: 60, description: "等待 Revit 返回结果的秒数。" },
  };
}

function wallProperties() {
  return {
    x1_mm: lengthSchema("起点 X 坐标。"),
    y1_mm: lengthSchema("起点 Y 坐标。"),
    x2_mm: lengthSchema("终点 X 坐标。"),
    y2_mm: lengthSchema("终点 Y 坐标。"),
    height_mm: lengthSchema("可选墙高，默认 3000mm。"),
    thickness_mm: lengthSchema("可选墙厚，默认 200mm；会创建独立 RCB 墙类型，避免修改既有墙类型。"),
    level: { type: "string", description: "可选标高名称；未指定时使用最低标高。" },
    wall_type: { type: "string", description: "可选源基本墙类型名称。" },
    new_wall_type: { type: "string", description: "可选新墙类型名称。" },
  };
}

function rectangleWallProperties() {
  return {
    width_mm: lengthSchema("矩形宽度。"),
    depth_mm: lengthSchema("矩形进深。"),
    height_mm: lengthSchema("可选墙高，默认 3000mm。"),
    thickness_mm: lengthSchema("可选墙厚，默认 200mm。"),
    x_mm: lengthSchema("可选左下角 X 坐标，默认 0。"),
    y_mm: lengthSchema("可选左下角 Y 坐标，默认 0。"),
    level: { type: "string", description: "可选标高名称；未指定时使用最低标高。" },
    wall_type: { type: "string", description: "可选源基本墙类型名称。" },
    new_wall_type: { type: "string", description: "可选新墙类型名称。" },
  };
}

function familyProperties() {
  return {
    family_name: { type: "string", description: "族名称；默认保存为同名 .rfa。" },
    template_path: { type: "string", description: "可选 .rft 样板绝对路径；省略时自动选择公制常规模型/Metric Generic Model。" },
    save_path: { type: "string", description: "可选输出 .rfa 绝对路径；省略时保存到“文档\\RevitCommandBridge\\Families”。" },
    category: { type: "string", description: "可选族类别，例如 OST_GenericModel、OST_MechanicalEquipment。" },
    parameters: {
      type: "array",
      description: "族参数数组；项支持 name、type(length/text/number/integer/yesno/area/volume/angle)、instance、group、default、formula。",
      items: { type: "object", additionalProperties: true },
    },
    types: {
      type: "array",
      description: "族类型数组；项支持 name 和 values/parameter_values。省略时创建“默认”类型。",
      items: { type: "object", additionalProperties: true },
    },
    geometry: {
      type: "array",
      description: "可选族实体；支持 box、cylinder、extrusion，与 create_direct_shape 相同。",
      items: { type: "object", additionalProperties: true },
    },
    load_into_project: { type: "boolean", default: true, description: "保存后是否自动载入当前项目。" },
    overwrite_file: { type: "boolean", default: false, description: "输出 .rfa 已存在时是否覆盖。" },
    overwrite_parameter_values: { type: "boolean", default: true, description: "载入同名族时是否覆盖已有类型参数值。" },
    place: { type: "object", additionalProperties: true, description: "可选：载入后立即放置一实例；传 point、level、type 等放置参数。" },
  };
}

function lengthSchema(description) {
  return {
    anyOf: [{ type: "number" }, { type: "string" }],
    description,
  };
}

function directToolOperation(name) {
  const prefix = "revit_";
  if (!name.startsWith(prefix)) {
    return null;
  }
  const operation = name.slice(prefix.length);
  return supportedOperations().includes(operation) ? operation : null;
}

function toolSuccess(value) {
  return {
    content: [
      {
        type: "text",
        text: JSON.stringify(value, null, 2),
      },
    ],
  };
}

function toolFailure(message, detail = null) {
  return {
    content: [
      {
        type: "text",
        text: detail == null ? message : `${message}\n${JSON.stringify(detail, null, 2)}`,
      },
    ],
    isError: true,
  };
}

function parseWaitSeconds(value) {
  const seconds = Number(value);
  if (!Number.isFinite(seconds) || seconds < 0 || seconds > 120) {
    throw new McpRpcError(-32602, "wait_seconds 必须在 0 到 120 之间。");
  }
  return seconds;
}

function writeMessage(message) {
  process.stdout.write(`${JSON.stringify(message)}\n`);
}

function jsonRpcError(id, code, message, data) {
  const error = { code, message };
  if (data !== undefined) {
    error.data = data;
  }
  return { jsonrpc: "2.0", id, error };
}

function isRecord(value) {
  return value !== null && typeof value === "object" && !Array.isArray(value);
}

class McpRpcError extends Error {
  constructor(code, message, data) {
    super(message);
    this.name = "McpRpcError";
    this.code = code;
    this.data = data;
  }
}
