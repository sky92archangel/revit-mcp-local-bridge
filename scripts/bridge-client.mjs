import crypto from "node:crypto";
import os from "node:os";
import path from "node:path";
import { fileURLToPath } from "node:url";
import {
  mkdir,
  readFile,
  rename,
  stat,
  unlink,
  writeFile,
} from "node:fs/promises";

export const COMMAND_PROTOCOL = "revit-command-bridge/2.0";

export class BridgeClientError extends Error {
  constructor(message, code = "BRIDGE_ERROR") {
    super(message);
    this.name = "BridgeClientError";
    this.code = code;
  }
}

export function defaultBridgeRoot() {
  const localApplicationData = process.env.LOCALAPPDATA || path.join(os.homedir(), "AppData", "Local");
  const requestedVersion = process.env.REVIT_COMMAND_BRIDGE_VERSION || inferBundledRevitVersion();
  if (!requestedVersion) {
    throw new Error("Revit version cannot be inferred; set REVIT_COMMAND_BRIDGE_VERSION.");
  }
  return path.join(localApplicationData, "RevitCommandBridge", requestedVersion);
}

export function resolveBridgeRoot(value) {
  return path.resolve(value || process.env.REVIT_COMMAND_BRIDGE_ROOT || defaultBridgeRoot());
}

export function bridgePaths(rootDirectory) {
  const root = resolveBridgeRoot(rootDirectory);
  return {
    root,
    inbox: path.join(root, "inbox"),
    processing: path.join(root, "processing"),
    outbox: path.join(root, "outbox"),
    archive: path.join(root, "archive"),
    logs: path.join(root, "logs"),
    status: path.join(root, "status.json"),
  };
}

export function createRequestId() {
  return crypto.randomUUID().replace(/-/g, "");
}

export function validateRequestId(value) {
  const id = String(value || "").trim();
  if (id.length < 1 || id.length > 128) {
    throw new BridgeClientError("命令 id 必须为 1 到 128 个字符。", "INVALID_ID");
  }
  if (!/^[A-Za-z0-9._-]+$/.test(id)) {
    throw new BridgeClientError("命令 id 只能包含字母、数字、.、_、-。", "INVALID_ID");
  }
  return id;
}

export function normalizeRequest(input, defaults = {}) {
  if (!isRecord(input)) {
    throw new BridgeClientError("命令请求必须是 JSON 对象。", "INVALID_REQUEST");
  }

  const operation = String(input.operation ?? input.command ?? "").trim();
  if (!operation) {
    throw new BridgeClientError("缺少 operation。", "INVALID_REQUEST");
  }

  const args = input.args ?? input.arguments ?? {};
  if (!isRecord(args)) {
    throw new BridgeClientError("args 必须是 JSON 对象。", "INVALID_REQUEST");
  }

  const source = String(input.source ?? defaults.source ?? "external").trim() || "external";
  const documentTitle = input.document_title ?? input.documentTitle ?? defaults.document_title ?? null;
  const preview = input.preview ?? input.dry_run ?? defaults.preview ?? false;
  return {
    id: validateRequestId(input.id ?? defaults.id ?? createRequestId()),
    operation,
    args,
    preview: parseBoolean(preview, "preview"),
    document_title: documentTitle == null || String(documentTitle).trim() === "" ? null : String(documentTitle).trim(),
    source,
    created_utc: new Date().toISOString(),
  };
}

export async function ensureBridgeDirectories(rootDirectory) {
  const paths = bridgePaths(rootDirectory);
  await Promise.all([
    mkdir(paths.inbox, { recursive: true }),
    mkdir(paths.processing, { recursive: true }),
    mkdir(paths.outbox, { recursive: true }),
    mkdir(paths.archive, { recursive: true }),
    mkdir(paths.logs, { recursive: true }),
  ]);
  return paths;
}

export async function enqueueCommand(input, options = {}) {
  const paths = await ensureBridgeDirectories(options.rootDirectory);
  const request = normalizeRequest(input, options);
  const requestPath = path.join(paths.inbox, `${request.id}.request.json`);
  const processingPath = path.join(paths.processing, `${request.id}.processing.json`);
  const resultPath = path.join(paths.outbox, `${request.id}.result.json`);
  if (await anyExists([requestPath, processingPath, resultPath])) {
    throw new BridgeClientError(`命令 ID 已存在：${request.id}。请读取已有结果，或生成新 ID。`, "ID_CONFLICT");
  }

  const temporaryPath = `${requestPath}.${crypto.randomBytes(8).toString("hex")}.tmp`;
  try {
    await writeFile(temporaryPath, `${JSON.stringify(request)}\n`, { encoding: "utf8", flag: "wx" });
    await rename(temporaryPath, requestPath);
  } catch (error) {
    await deleteIfExists(temporaryPath);
    if (error && error.code === "EEXIST") {
      throw new BridgeClientError(`命令 ID 已存在：${request.id}。`, "ID_CONFLICT");
    }
    throw error;
  }

  return {
    id: request.id,
    request,
    root_directory: paths.root,
    result_path: resultPath,
  };
}

export async function readCommandResult(id, options = {}) {
  const paths = bridgePaths(options.rootDirectory);
  const resultPath = path.join(paths.outbox, `${validateRequestId(id)}.result.json`);
  try {
    const raw = await readFile(resultPath, "utf8");
    return JSON.parse(raw);
  } catch (error) {
    if (error && error.code === "ENOENT") {
      return null;
    }
    throw error;
  }
}

export async function waitForCommandResult(id, options = {}) {
  const waitMilliseconds = Math.max(0, Number(options.waitMilliseconds ?? 60000));
  const pollMilliseconds = Math.max(50, Number(options.pollMilliseconds ?? 200));
  const deadline = Date.now() + waitMilliseconds;
  do {
    try {
      const result = await readCommandResult(id, options);
      if (result != null) {
        return result;
      }
    } catch (error) {
      if (!(error && (error.code === "EBUSY" || error.code === "EPERM"))) {
        throw error;
      }
    }
    if (Date.now() >= deadline) {
      break;
    }
    await sleep(Math.min(pollMilliseconds, Math.max(0, deadline - Date.now())));
  } while (Date.now() <= deadline);

  return null;
}

export async function readBridgeStatus(options = {}) {
  const paths = bridgePaths(options.rootDirectory);
  try {
    const raw = await readFile(paths.status, "utf8");
    return JSON.parse(raw);
  } catch (error) {
    if (error && error.code === "ENOENT") {
      return null;
    }
    throw error;
  }
}

export function isBridgeRunning(status, maxAgeMilliseconds = 5000) {
  if (!isRecord(status) || !["running", "busy"].includes(status.state)) {
    return false;
  }
  const updated = Date.parse(status.updated_utc || "");
  return Number.isFinite(updated) && Date.now() - updated <= maxAgeMilliseconds;
}

export function supportedOperations() {
  return [
    "health",
    "execute_plan",
    "list_levels",
    "list_wall_types",
    "new_project",
    "create_level",
    "create_grid",
    "create_wall",
    "create_rectangle_walls",
  ];
}

function parseBoolean(value, fieldName) {
  if (typeof value === "boolean") {
    return value;
  }
  if (typeof value === "string") {
    if (value.trim().toLowerCase() === "true") {
      return true;
    }
    if (value.trim().toLowerCase() === "false") {
      return false;
    }
  }
  throw new BridgeClientError(`${fieldName} 必须是 true 或 false。`, "INVALID_REQUEST");
}

function isRecord(value) {
  return value !== null && typeof value === "object" && !Array.isArray(value);
}

async function anyExists(paths) {
  for (const candidate of paths) {
    try {
      await stat(candidate);
      return true;
    } catch (error) {
      if (!(error && error.code === "ENOENT")) {
        throw error;
      }
    }
  }
  return false;
}

async function deleteIfExists(filePath) {
  try {
    await unlink(filePath);
  } catch (error) {
    if (!(error && error.code === "ENOENT")) {
      throw error;
    }
  }
}

function sleep(milliseconds) {
  return new Promise((resolve) => setTimeout(resolve, milliseconds));
}

function inferBundledRevitVersion() {
  const scriptDirectory = path.dirname(fileURLToPath(import.meta.url));
  const candidate = path.basename(path.dirname(scriptDirectory));
  const match = /20\d{2}/.exec(candidate);
  return match ? match[0] : null;
}
