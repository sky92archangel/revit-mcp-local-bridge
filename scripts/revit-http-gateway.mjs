// Revit HTTP REST 网关 —— 以 HTTP REST API 封装文件队列，提供 health、capabilities、commands 端点
// Revit HTTP REST gateway — wraps the file queue with a REST API, providing health, capabilities, and commands endpoints
import http from "node:http";
import path from "node:path";
import { fileURLToPath } from "node:url";
import {
  BridgeClientError,
  COMMAND_PROTOCOL,
  enqueueCommand,
  isBridgeRunning,
  readBridgeStatus,
  readCommandResult,
  resolveBridgeRoot,
  supportedOperations,
  waitForCommandResult,
} from "./bridge-client.mjs";

// 从环境变量读取主机、根目录、端口配置
// Read host, root directory, and port configuration from environment variables
const host = process.env.REVIT_BRIDGE_HOST || "127.0.0.1";
const rootDirectory = resolveBridgeRoot(process.env.REVIT_COMMAND_BRIDGE_ROOT);
const revitVersion = process.env.REVIT_COMMAND_BRIDGE_VERSION || path.basename(rootDirectory);
const port = readPort(process.env.REVIT_BRIDGE_PORT || String(defaultPortForVersion(revitVersion)));

// 创建 HTTP 服务器，所有请求路由到 handleRequest
// Create HTTP server; all requests are routed to handleRequest
export const server = http.createServer((request, response) => {
  handleRequest(request, response).catch((error) => {
    console.error(error);
    sendJson(response, 500, {
      ok: false,
      error: "internal_error",
      message: "REST 网关发生未处理错误。",
    });
  });
});

// 处理客户端连接错误，返回 400
// Handle client connection errors, return 400
server.on("clientError", (_error, socket) => {
  socket.end("HTTP/1.1 400 Bad Request\r\n\r\n");
});

// 启动 HTTP 服务器监听，返回 Promise
// Start the HTTP server listener, returns a Promise
export function startGateway() {
  if (server.listening) {
    return Promise.resolve();
  }
  return new Promise((resolve, reject) => {
    const rejectStart = (error) => {
      server.off("error", rejectStart);
      reject(error);
    };
    server.once("error", rejectStart);
    server.listen(port, host, () => {
      server.off("error", rejectStart);
      console.log(`Revit Command Bridge REST gateway: http://${host}:${port}`);
      console.log(`Queue root: ${rootDirectory}`);
      resolve();
    });
  });
}

// 停止 HTTP 服务器，返回 Promise
// Stop the HTTP server, returns a Promise
export function stopGateway() {
  if (!server.listening) {
    return Promise.resolve();
  }
  return new Promise((resolve, reject) => {
    server.close((error) => (error ? reject(error) : resolve()));
  });
}

// 直接运行时启动网关并注册信号处理
// When run directly, start the gateway and register signal handlers
const isMainModule = process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url);
if (isMainModule) {
  await startGateway();
  for (const signal of ["SIGINT", "SIGTERM"]) {
    process.on(signal, () => stopGateway().then(() => process.exit(0)));
  }
}

// 路由 HTTP 请求：OPTIONS、GET /health、GET /capabilities、GET/POST /commands
// Route HTTP requests: OPTIONS, GET /health, GET /capabilities, GET/POST /commands
async function handleRequest(request, response) {
  const url = new URL(request.url || "/", `http://${host}:${port}`);
  if (request.method === "OPTIONS") {
    response.writeHead(204, { Allow: "GET, POST, OPTIONS" });
    response.end();
    return;
  }

  if (request.method === "GET" && url.pathname === "/health") {
    const status = await readBridgeStatus({ rootDirectory });
    sendJson(response, 200, {
      ok: true,
      protocol: COMMAND_PROTOCOL,
      bridge_running: isBridgeRunning(status),
      status,
    });
    return;
  }

  if (request.method === "GET" && url.pathname === "/capabilities") {
    sendJson(response, 200, {
      ok: true,
      protocol: COMMAND_PROTOCOL,
      operations: supportedOperations(),
      transport: "local file queue via localhost REST gateway",
    });
    return;
  }

  // 匹配 GET /commands/{id} 路由，读取命令执行结果
  // Match GET /commands/{id} route, read command execution result
  const resultMatch = /^\/commands\/([^/]+)$/.exec(url.pathname);
  if (request.method === "GET" && resultMatch) {
    let result;
    try {
      result = await readCommandResult(decodeURIComponent(resultMatch[1]), { rootDirectory });
    } catch (error) {
      sendClientError(response, error);
      return;
    }

    if (result == null) {
      sendJson(response, 202, {
        ok: true,
        state: "pending",
        id: decodeURIComponent(resultMatch[1]),
      });
      return;
    }

    sendJson(response, 200, result);
    return;
  }

  // 匹配 POST /commands 路由，提交新命令
  // Match POST /commands route, submit a new command
  if (request.method === "POST" && url.pathname === "/commands") {
    await submitCommand(request, response, url);
    return;
  }

  sendJson(response, 404, {
    ok: false,
    error: "not_found",
    message: "可用端点：GET /health、GET /capabilities、POST /commands、GET /commands/{id}。",
  });
}

// 处理 POST /commands：解析请求体、检查桥状态、入队列、可选等待结果
// Handle POST /commands: parse body, check bridge state, enqueue, optionally wait for result
async function submitCommand(request, response, url) {
  let envelope;
  try {
    envelope = await readJsonBody(request);
  } catch (error) {
    sendClientError(response, error);
    return;
  }

  // 支持 envelope.command 包裹或直接作为命令对象
  // Support either envelope.command wrapper or the envelope itself as the command
  const command = isRecord(envelope.command) ? envelope.command : envelope;
  const status = await readBridgeStatus({ rootDirectory });
  const requireRunning = envelope.require_running !== false;
  // 默认要求桥接运行中，否则返回 503
  // By default require the bridge to be running, otherwise return 503
  if (requireRunning && !isBridgeRunning(status)) {
    sendJson(response, 503, {
      ok: false,
      error: "bridge_not_running",
      message: "Revit 命令桥未运行。请在 Revit 功能区点击“启动桥接”。",
      status,
    });
    return;
  }

  let queued;
  try {
    queued = await enqueueCommand(command, { rootDirectory, source: "rest" });
  } catch (error) {
    sendClientError(response, error);
    return;
  }

  // 解析 wait_seconds 参数，大于 0 时同步等待结果
  // Parse wait_seconds; if > 0, synchronously wait for the result
  let waitSeconds;
  try {
    waitSeconds = readWaitSeconds(url.searchParams.get("wait_seconds") ?? envelope.wait_seconds ?? 0);
  } catch (error) {
    sendClientError(response, error);
    return;
  }
  if (waitSeconds > 0) {
    const result = await waitForCommandResult(queued.id, {
      rootDirectory,
      waitMilliseconds: waitSeconds * 1000,
    });
    if (result != null) {
      sendJson(response, 200, result);
      return;
    }
  }

  // 不等待或超时：返回 202 Accepted 及结果轮询地址
  // No wait or timeout: return 202 Accepted with result polling URL
  sendJson(response, 202, {
    ok: true,
    state: "queued",
    id: queued.id,
    operation: queued.request.operation,
    result_url: `/commands/${encodeURIComponent(queued.id)}`,
  });
}

// 将客户端错误转换为对应的 HTTP 状态码和 JSON 响应
// Convert client errors to appropriate HTTP status codes and JSON responses
function sendClientError(response, error) {
  if (error instanceof BridgeClientError) {
    const status = error.code === "ID_CONFLICT" ? 409 : 400;
    sendJson(response, status, {
      ok: false,
      error: error.code.toLowerCase(),
      message: error.message,
    });
    return;
  }

  sendJson(response, 400, {
    ok: false,
    error: "invalid_request",
    message: error instanceof Error ? error.message : String(error),
  });
}

// 发送 JSON 响应，设置 Content-Type、Content-Length 和 Cache-Control
// Send a JSON response with Content-Type, Content-Length, and Cache-Control headers
function sendJson(response, statusCode, value) {
  if (response.writableEnded) {
    return;
  }
  const body = JSON.stringify(value);
  response.writeHead(statusCode, {
    "Content-Type": "application/json; charset=utf-8",
    "Content-Length": Buffer.byteLength(body),
    "Cache-Control": "no-store",
  });
  response.end(body);
}

// 从 HTTP 请求体中读取并解析 JSON，限制 1MB
// Read and parse JSON from the HTTP request body, limited to 1MB
function readJsonBody(request) {
  return new Promise((resolve, reject) => {
    let size = 0;
    const chunks = [];
    request.on("data", (chunk) => {
      size += chunk.length;
      if (size > 1024 * 1024) {
        reject(new BridgeClientError("请求体超过 1MB 限制。", "INVALID_REQUEST"));
        request.destroy();
        return;
      }
      chunks.push(chunk);
    });
    request.on("error", reject);
    request.on("end", () => {
      try {
        const raw = Buffer.concat(chunks).toString("utf8");
        const parsed = JSON.parse(raw);
        if (!isRecord(parsed)) {
          throw new BridgeClientError("请求体必须是 JSON 对象。", "INVALID_REQUEST");
        }
        resolve(parsed);
      } catch (error) {
        reject(error instanceof BridgeClientError ? error : new BridgeClientError("JSON 无法解析：" + error.message, "INVALID_REQUEST"));
      }
    });
  });
}

function readPort(value) {
  const portValue = Number(value);
  if (!Number.isInteger(portValue) || portValue < 1024 || portValue > 65535) {
    throw new Error("REVIT_BRIDGE_PORT 必须为 1024 到 65535 的整数。");
  }
  return portValue;
}

function defaultPortForVersion(revitVersion) {
  const year = Number(revitVersion);
  return Number.isInteger(year) && year >= 2000 && year <= 2999
    ? 8000 + (year % 1000)
    : 8765;
}

function readWaitSeconds(value) {
  const seconds = Number(value);
  if (!Number.isFinite(seconds) || seconds < 0 || seconds > 120) {
    throw new BridgeClientError("wait_seconds 必须在 0 到 120 之间。", "INVALID_REQUEST");
  }
  return seconds;
}

function isRecord(value) {
  return value !== null && typeof value === "object" && !Array.isArray(value);
}
