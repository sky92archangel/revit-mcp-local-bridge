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

const host = process.env.REVIT_BRIDGE_HOST || "127.0.0.1";
const rootDirectory = resolveBridgeRoot(process.env.REVIT_COMMAND_BRIDGE_ROOT);
const revitVersion = process.env.REVIT_COMMAND_BRIDGE_VERSION || path.basename(rootDirectory);
const port = readPort(process.env.REVIT_BRIDGE_PORT || String(defaultPortForVersion(revitVersion)));

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

server.on("clientError", (_error, socket) => {
  socket.end("HTTP/1.1 400 Bad Request\r\n\r\n");
});

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

export function stopGateway() {
  if (!server.listening) {
    return Promise.resolve();
  }
  return new Promise((resolve, reject) => {
    server.close((error) => (error ? reject(error) : resolve()));
  });
}

const isMainModule = process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url);
if (isMainModule) {
  await startGateway();
  for (const signal of ["SIGINT", "SIGTERM"]) {
    process.on(signal, () => stopGateway().then(() => process.exit(0)));
  }
}

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

async function submitCommand(request, response, url) {
  let envelope;
  try {
    envelope = await readJsonBody(request);
  } catch (error) {
    sendClientError(response, error);
    return;
  }

  const command = isRecord(envelope.command) ? envelope.command : envelope;
  const status = await readBridgeStatus({ rootDirectory });
  const requireRunning = envelope.require_running !== false;
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

  sendJson(response, 202, {
    ok: true,
    state: "queued",
    id: queued.id,
    operation: queued.request.operation,
    result_url: `/commands/${encodeURIComponent(queued.id)}`,
  });
}

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
