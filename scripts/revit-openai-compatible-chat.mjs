// OpenAI 兼容模式聊天助手 —— 通过 OpenAI Chat Completions API 调用模型，自动将 Revit 工具调用接入文件队列
// OpenAI-compatible chat assistant — calls models via the OpenAI Chat Completions API and bridges Revit tool calls to the file queue
import readline from "node:readline/promises";
import { stdin as input, stdout as output } from "node:process";
import {
  BridgeClientError,
  enqueueCommand,
  isBridgeRunning,
  readBridgeStatus,
  resolveBridgeRoot,
  supportedOperations,
  waitForCommandResult,
} from "./bridge-client.mjs";

// 读取配置、命令行参数、工具定义，构造系统提示词
// Read configuration, command-line args, tool definitions, and build the system prompt
const configuration = readConfiguration();
const commandLine = readCommandLine(process.argv.slice(2));
const rootDirectory = resolveBridgeRoot(process.env.REVIT_COMMAND_BRIDGE_ROOT);
const toolDefinitions = buildToolDefinitions();
// 系统提示词：约束模型行为，要求先预览后执行
// System prompt: constrain model behavior, require preview before execution
const systemMessage = {
  role: "system",
  content: [
    "你是 Revit 建模助手，只能通过已提供的工具操作当前打开的 Revit 项目。",
    "所有长度单位均为 mm。先查询当前文档的标高、类型、族或系统；写入模型前先调用 preview=true，向用户展示结果后，收到明确确认再用 preview=false 执行。",
    "不要生成、执行或建议任意 C#、Python、Dynamo 脚本；使用 revit_execute_plan 的受控步骤。",
    "只有工具返回 ok=true 且 state/result 明确成功时，才可称模型已修改。",
  ].join("\n"),
};

// 命令行模式：单次提问并输出回答；交互模式：进入 REPL 循环
// CLI mode: single question and answer; interactive mode: enter REPL loop
if (commandLine.message != null) {
  const messages = [systemMessage, { role: "user", content: commandLine.message }];
  const answer = await continueConversation(messages);
  output.write(`${answer}\n`);
} else {
  await runInteractiveChat();
}

// 从环境变量读取 AI 模型配置（API Key、Base URL、Model）
// Read AI model configuration from environment variables (API Key, Base URL, Model)
function readConfiguration() {
  const apiKey = String(process.env.REVIT_AI_API_KEY || "").trim();
  const baseUrl = String(process.env.REVIT_AI_BASE_URL || "").trim().replace(/\/+$/, "");
  const model = String(process.env.REVIT_AI_MODEL || "").trim();
  if (!apiKey || !baseUrl || !model) {
    throw new Error("未读取到本机模型配置。请用 Revit AI Hub 安装器或 configure-ai-provider.ps1 配置 API。 ");
  }
  if (!/^https?:\/\//i.test(baseUrl)) {
    throw new Error("模型 API 地址必须以 http:// 或 https:// 开头。");
  }
  return { apiKey, baseUrl, model };
}

// 解析命令行参数：--message 单条指令，--help 显示帮助
// Parse command-line arguments: --message for a single instruction, --help for help text
function readCommandLine(argumentsValue) {
  const result = { message: null };
  for (let index = 0; index < argumentsValue.length; index += 1) {
    const item = argumentsValue[index];
    if (item === "--message") {
      const message = argumentsValue[index + 1];
      if (!message) {
        throw new Error("--message 后需要提供一条建模指令。");
      }
      result.message = message;
      index += 1;
      continue;
    }
    if (item === "--help" || item === "-h") {
      output.write("用法：node revit-openai-compatible-chat.mjs [--message \"建模指令\"]\n");
      process.exit(0);
    }
    throw new Error(`未知参数：${item}`);
  }
  return result;
}

// 交互式 REPL 循环：用户输入 -> 模型响应 -> 工具调用循环
// Interactive REPL loop: user input -> model response -> tool call loop
async function runInteractiveChat() {
  const terminal = readline.createInterface({ input, output });
  const messages = [systemMessage];
  output.write(`已连接通用模型 API：${configuration.model}\n`);
  output.write("输入建模需求；:clear 清空会话；:quit 退出。\n\n");
  try {
    while (true) {
      const userInput = (await terminal.question("你：")).trim();
      if (!userInput) {
        continue;
      }
      if (userInput === ":quit" || userInput === ":exit") {
        break;
      }
      if (userInput === ":clear") {
        messages.splice(1);
        output.write("已清空会话上下文。\n\n");
        continue;
      }
      if (userInput === ":help") {
        output.write("先确保 Revit 已打开项目并启动 Revit Command Bridge。模型会先预览写入，再等你的确认。\n\n");
        continue;
      }

      messages.push({ role: "user", content: userInput });
      try {
        const answer = await continueConversation(messages);
        output.write(`\n助手：${answer}\n\n`);
      } catch (error) {
        output.write(`\n调用失败：${error instanceof Error ? error.message : String(error)}\n\n`);
      }
    }
  } finally {
    terminal.close();
  }
}

// 多轮工具调用循环：模型最多连续请求 10 轮工具调用
// Multi-round tool call loop: model may request up to 10 consecutive tool call rounds
async function continueConversation(messages) {
  for (let round = 0; round < 10; round += 1) {
    const assistantMessage = await requestChatCompletion(messages);
    messages.push(assistantMessage);
    const toolCalls = Array.isArray(assistantMessage.tool_calls) ? assistantMessage.tool_calls : [];
    if (toolCalls.length === 0) {
      return normalizeContent(assistantMessage.content) || "模型没有返回可显示的文字。";
    }

    for (const toolCall of toolCalls) {
      const result = await executeToolCall(toolCall);
      messages.push({
        role: "tool",
        tool_call_id: String(toolCall.id || ""),
        content: JSON.stringify(result),
      });
    }
  }
  throw new Error("模型连续请求工具超过 10 轮，已停止本次会话。");
}

// 向 OpenAI 兼容 API 发送 Chat Completions 请求，返回第一个 choice 的 message
// Send a Chat Completions request to the OpenAI-compatible API, return the first choice's message
async function requestChatCompletion(messages) {
  const response = await fetch(chatCompletionsUrl(configuration.baseUrl), {
    method: "POST",
    headers: {
      Authorization: `Bearer ${configuration.apiKey}`,
      "Content-Type": "application/json",
    },
    body: JSON.stringify({
      model: configuration.model,
      messages,
      tools: toolDefinitions,
      tool_choice: "auto",
      temperature: 0.1,
      stream: false,
    }),
  });

  const raw = await response.text();
  let payload;
  try {
    payload = JSON.parse(raw);
  } catch {
    throw new Error(`模型 API 返回了非 JSON 响应（HTTP ${response.status}）。`);
  }
  if (!response.ok) {
    const message = payload?.error?.message || payload?.message || raw.slice(0, 500);
    throw new Error(`模型 API 请求失败（HTTP ${response.status}）：${message}`);
  }
  const message = payload?.choices?.[0]?.message;
  if (!isRecord(message)) {
    throw new Error("模型 API 响应缺少 choices[0].message。");
  }
  return message;
}

// 执行模型返回的单个工具调用：解析参数并分发到 revit_command 或 revit_execute_plan
// Execute a single tool call from the model: parse arguments and dispatch to revit_command or revit_execute_plan
async function executeToolCall(toolCall) {
  if (!isRecord(toolCall) || !isRecord(toolCall.function)) {
    return { ok: false, error: "invalid_tool_call", message: "模型返回了无效的工具调用。" };
  }
  let argumentsValue;
  try {
    argumentsValue = JSON.parse(String(toolCall.function.arguments || "{}"));
  } catch (error) {
    return { ok: false, error: "invalid_tool_arguments", message: `工具参数不是有效 JSON：${error.message}` };
  }
  if (!isRecord(argumentsValue)) {
    return { ok: false, error: "invalid_tool_arguments", message: "工具参数必须是 JSON 对象。" };
  }

  switch (toolCall.function.name) {
    case "revit_command":
      return submitRevitCommand(argumentsValue);
    case "revit_execute_plan":
      return submitRevitPlan(argumentsValue);
    default:
      return { ok: false, error: "unknown_tool", message: `未注册的工具：${toolCall.function.name}` };
  }
}

async function submitRevitPlan(argumentsValue) {
  if (!Array.isArray(argumentsValue.steps) || argumentsValue.steps.length === 0) {
    return { ok: false, error: "invalid_plan", message: "revit_execute_plan 需要至少一个 steps 项。" };
  }
  return submitToRevit({
    operation: "execute_plan",
    args: { steps: argumentsValue.steps },
    preview: argumentsValue.preview ?? true,
    document_title: argumentsValue.document_title ?? null,
    wait_seconds: argumentsValue.wait_seconds,
  });
}

async function submitRevitCommand(argumentsValue) {
  const operation = String(argumentsValue.operation || "").trim();
  if (!supportedOperations().includes(operation)) {
    return { ok: false, error: "unsupported_operation", message: `不支持的顶层操作：${operation}` };
  }
  return submitToRevit({
    operation,
    args: isRecord(argumentsValue.args) ? argumentsValue.args : {},
    preview: argumentsValue.preview ?? !["health", "list_levels", "list_wall_types"].includes(operation),
    document_title: argumentsValue.document_title ?? null,
    wait_seconds: argumentsValue.wait_seconds,
  });
}

async function submitToRevit(command) {
  const status = await readBridgeStatus({ rootDirectory });
  if (!isBridgeRunning(status)) {
    return {
      ok: false,
      error: "bridge_not_running",
      message: "Revit 命令桥未运行。请打开 Revit 项目，并在 Revit 功能区启动命令桥。",
      status,
    };
  }

  const waitSeconds = readWaitSeconds(command.wait_seconds ?? 60);
  try {
    const queued = await enqueueCommand({
      operation: command.operation,
      args: command.args,
      preview: readBoolean(command.preview, "preview"),
      document_title: command.document_title,
      source: "openai-compatible",
    }, { rootDirectory, source: "openai-compatible" });
    const result = await waitForCommandResult(queued.id, {
      rootDirectory,
      waitMilliseconds: waitSeconds * 1000,
    });
    return result ?? {
      ok: true,
      state: "queued",
      id: queued.id,
      operation: command.operation,
      message: "命令已排队，尚未在等待时间内完成。",
    };
  } catch (error) {
    if (error instanceof BridgeClientError) {
      return { ok: false, error: error.code.toLowerCase(), message: error.message };
    }
    return { ok: false, error: "bridge_error", message: error instanceof Error ? error.message : String(error) };
  }
}

function buildToolDefinitions() {
  return [
    {
      type: "function",
      function: {
        name: "revit_execute_plan",
        description: "执行通用 Revit 建模计划。所有写入步骤必须先 preview=true；收到用户确认后才 preview=false。steps 可以组合查询、建筑、结构、机电、参数、选择等受控操作。",
        parameters: {
          type: "object",
          properties: {
            steps: {
              type: "array",
              minItems: 1,
              maxItems: 500,
              items: {
                type: "object",
                properties: {
                  id: { type: "string" },
                  operation: { type: "string" },
                  args: { type: "object", additionalProperties: true },
                },
                required: ["operation"],
                additionalProperties: false,
              },
            },
            preview: { type: "boolean", description: "写入前必须为 true。" },
            document_title: { type: "string" },
            wait_seconds: { type: "number", minimum: 0, maximum: 120 },
          },
          required: ["steps"],
          additionalProperties: false,
        },
      },
    },
    {
      type: "function",
      function: {
        name: "revit_command",
        description: "调用一个受控顶层 Revit 命令。查询优先使用此工具；建模优先使用 revit_execute_plan。",
        parameters: {
          type: "object",
          properties: {
            operation: { type: "string", enum: supportedOperations() },
            args: { type: "object", additionalProperties: true },
            preview: { type: "boolean" },
            document_title: { type: "string" },
            wait_seconds: { type: "number", minimum: 0, maximum: 120 },
          },
          required: ["operation"],
          additionalProperties: false,
        },
      },
    },
  ];
}

function chatCompletionsUrl(baseUrl) {
  return /\/chat\/completions$/i.test(baseUrl) ? baseUrl : `${baseUrl}/chat/completions`;
}

function normalizeContent(value) {
  if (typeof value === "string") {
    return value;
  }
  if (Array.isArray(value)) {
    return value.map((part) => typeof part?.text === "string" ? part.text : "").join("");
  }
  return value == null ? "" : String(value);
}

function readWaitSeconds(value) {
  const seconds = Number(value);
  if (!Number.isFinite(seconds) || seconds < 0 || seconds > 120) {
    return 60;
  }
  return seconds;
}

function readBoolean(value, fieldName) {
  if (typeof value === "boolean") {
    return value;
  }
  if (typeof value === "string" && ["true", "false"].includes(value.trim().toLowerCase())) {
    return value.trim().toLowerCase() === "true";
  }
  throw new BridgeClientError(`${fieldName} 必须是 true 或 false。`, "INVALID_REQUEST");
}

function isRecord(value) {
  return value !== null && typeof value === "object" && !Array.isArray(value);
}
