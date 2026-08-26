using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace RevitCommandBridge
{
    /// <summary>
    /// 基于文件的请求队列，通过文件系统和原子写入在进程间传递请求与响应。
    /// File-based request queue that passes requests and responses between processes using the file system and atomic writes.
    /// </summary>
    internal static class BridgeFileQueue
    {
        private static readonly object FileGate = new object();
        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);
        private static bool _initialized;

        /// <summary>
        /// 队列根目录。
        /// Root directory of the queue.
        /// </summary>
        public static string RootDirectory
        {
            get
            {
                return BridgeBuildInfo.QueueRootDirectory;
            }
        }

        /// <summary>
        /// 收件箱目录：外部写入的请求文件存放于此。
        /// Inbox directory: request files written by external processes are placed here.
        /// </summary>
        public static string InboxDirectory { get { return Path.Combine(RootDirectory, "inbox"); } }

        /// <summary>
        /// 处理中目录：Revit 端认领后的请求移到此处。
        /// Processing directory: requests moved here after being claimed by Revit.
        /// </summary>
        public static string ProcessingDirectory { get { return Path.Combine(RootDirectory, "processing"); } }

        /// <summary>
        /// 出箱目录：处理完成后结果文件写入此处。
        /// Outbox directory: result files written here after processing completes.
        /// </summary>
        public static string OutboxDirectory { get { return Path.Combine(RootDirectory, "outbox"); } }

        /// <summary>
        /// 归档目录：处理完成或被判定为无效的请求移入此处。
        /// Archive directory: completed or invalid requests are moved here.
        /// </summary>
        public static string ArchiveDirectory { get { return Path.Combine(RootDirectory, "archive"); } }

        /// <summary>
        /// 日志目录。
        /// Log directory.
        /// </summary>
        public static string LogDirectory { get { return Path.Combine(RootDirectory, "logs"); } }

        /// <summary>
        /// 状态文件路径（status.json）。
        /// Status file path (status.json).
        /// </summary>
        public static string StatusFilePath { get { return Path.Combine(RootDirectory, "status.json"); } }

        /// <summary>
        /// 初始化队列目录结构，线程安全。
        /// Initializes the queue directory structure in a thread-safe manner.
        /// </summary>
        public static void Initialize()
        {
            lock (FileGate)
            {
                if (_initialized)
                {
                    return;
                }

                Directory.CreateDirectory(InboxDirectory);
                Directory.CreateDirectory(ProcessingDirectory);
                Directory.CreateDirectory(OutboxDirectory);
                Directory.CreateDirectory(ArchiveDirectory);
                Directory.CreateDirectory(LogDirectory);
                _initialized = true;
            }
        }

        /// <summary>
        /// 将请求加入收件箱队列。校验 ID 唯一性后原子写入。
        /// Enqueues a request into the inbox. Validates ID uniqueness, then atomically writes the file.
        /// </summary>
        public static string Enqueue(BridgeRequest request)
        {
            if (request == null)
            {
                throw new BridgeCommandException("命令请求不能为空。");
            }

            Initialize();
            if (string.IsNullOrWhiteSpace(request.Id))
            {
                request.Id = Guid.NewGuid().ToString("N");
            }

            request.Id = NormalizeRequestId(request.Id);

            request.CreatedUtc = DateTime.UtcNow;
            string finalPath = Path.Combine(InboxDirectory, request.Id + ".request.json");
            lock (FileGate)
            {
                // 检查 ID 是否已存在于 inbox / processing / outbox 中
                // Check if the ID already exists in inbox / processing / outbox
                string processingPath = Path.Combine(ProcessingDirectory, request.Id + ".processing.json");
                string resultPath = Path.Combine(OutboxDirectory, request.Id + ".result.json");
                if (File.Exists(finalPath) || File.Exists(processingPath) || File.Exists(resultPath))
                {
                    throw new BridgeCommandException("命令 ID 已存在：" + request.Id + "。请生成新 ID，或读取已有结果。");
                }

                WriteAtomically(finalPath, BridgeJson.SerializeRequest(request));
            }

            AppendLog("queued id=" + request.Id + " operation=" + request.Operation + " source=" + request.Source);
            return request.Id;
        }

        /// <summary>
        /// 尝试领取下一个待处理请求：按创建时间排序，通过文件 Move 实现原子认领。
        /// Attempts to claim the next pending request: ordered by creation time, claimed atomically via file Move.
        /// </summary>
        public static bool TryClaimNext(out BridgeRequest request)
        {
            Initialize();
            request = null;

            lock (FileGate)
            {
                // 按创建时间升序排序，同时间按文件名排序
                // Sort by creation time ascending, tie-break by file name
                string[] files = Directory.GetFiles(InboxDirectory, "*.request.json")
                    .OrderBy(path => File.GetCreationTimeUtc(path))
                    .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                foreach (string incomingPath in files)
                {
                    string processingPath = Path.Combine(
                        ProcessingDirectory,
                        Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(incomingPath)) + ".processing.json");

                    // 通过文件移动实现原子认领：其他进程无法拿到同一个文件
                    // Atomic claim via file move: other processes cannot claim the same file
                    try
                    {
                        File.Move(incomingPath, processingPath);
                    }
                    catch (IOException)
                    {
                        // 文件被其他进程先移走了，跳过
                        // Another process moved this file first, skip it
                        continue;
                    }

                    try
                    {
                        // 检查文件大小上限（1MB）
                        // Check file size limit (1MB)
                        FileInfo fileInfo = new FileInfo(processingPath);
                        if (fileInfo.Length > 1024 * 1024)
                        {
                            throw new BridgeCommandException("命令请求超过 1MB 限制。");
                        }

                        BridgeRequest parsed = BridgeJson.ParseRequest(File.ReadAllText(processingPath, Encoding.UTF8));
                        parsed.Id = string.IsNullOrWhiteSpace(parsed.Id)
                            ? Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(incomingPath))
                            : parsed.Id;
                        parsed.Id = NormalizeRequestId(parsed.Id);
                        parsed.Source = string.IsNullOrWhiteSpace(parsed.Source) ? "external" : parsed.Source;
                        // 缓存处理中文件路径以便后续 Complete 时归档
                        // Cache the processing file path for later archiving during Complete
                        RequestProcessingPath.Store(parsed, processingPath);
                        request = parsed;
                        return true;
                    }
                    catch (Exception ex)
                    {
                        // 解析失败时写入错误结果并归档
                        // Write error result and archive on parse failure
                        string id = Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(incomingPath));
                        var error = BridgeResponse.Failure("命令请求无效：" + ex.Message, null);
                        WriteAtomically(Path.Combine(OutboxDirectory, id + ".result.json"), BridgeJson.SerializeResponse(id, error));
                        Archive(processingPath, id, "invalid");
                        AppendLog("invalid id=" + id + " error=" + ex.Message);
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// 完成请求处理：将结果写入 outbox，将处理中文件移入归档。
        /// Completes request processing: writes the result to outbox, archives the processing file.
        /// </summary>
        public static void Complete(BridgeRequest request, BridgeResponse response)
        {
            Initialize();
            lock (FileGate)
            {
                string resultPath = Path.Combine(OutboxDirectory, request.Id + ".result.json");
                WriteAtomically(resultPath, BridgeJson.SerializeResponse(request.Id, response));

                string processingPath = RequestProcessingPath.Take(request);
                if (!string.IsNullOrWhiteSpace(processingPath) && File.Exists(processingPath))
                {
                    Archive(processingPath, request.Id, response.Ok ? "completed" : "failed");
                }
            }

            AppendLog("completed id=" + request.Id + " state=" + response.State + " ok=" + response.Ok);
        }

        /// <summary>
        /// 检查 inbox 中是否有待处理的请求。
        /// Checks whether there are pending requests in the inbox.
        /// </summary>
        public static bool HasPendingRequests()
        {
            Initialize();
            try
            {
                return Directory.EnumerateFiles(InboxDirectory, "*.request.json").Any();
            }
            catch (IOException)
            {
                return false;
            }
        }

        /// <summary>
        /// 恢复上次中断时遗留在 processing 目录中的请求（移回 inbox）。
        /// Recovers requests left in the processing directory from a previous interruption (moves them back to inbox).
        /// </summary>
        public static int RecoverProcessingRequests()
        {
            Initialize();
            int recovered = 0;
            lock (FileGate)
            {
                foreach (string processingPath in Directory.GetFiles(ProcessingDirectory, "*.processing.json"))
                {
                    string id = Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(processingPath));
                    string inboxPath = Path.Combine(InboxDirectory, id + ".request.json");
                    string resultPath = Path.Combine(OutboxDirectory, id + ".result.json");
                    try
                    {
                        // 已有结果则直接归档
                        // Archive directly if result already exists
                        if (File.Exists(resultPath))
                        {
                            Archive(processingPath, id, "already-completed");
                        }
                        else if (!File.Exists(inboxPath))
                        {
                            // 移回 inbox 以便重新处理
                            // Move back to inbox for reprocessing
                            File.Move(processingPath, inboxPath);
                            recovered++;
                        }
                    }
                    catch (Exception ex)
                    {
                        AppendLog("recovery failed id=" + id + " error=" + ex.Message);
                    }
                }
            }

            if (recovered > 0)
            {
                AppendLog("recovered processing requests=" + recovered);
            }

            return recovered;
        }

        /// <summary>
        /// 向 status.json 发布运行时状态（供外部进程查询）。
        /// Publishes runtime status to status.json (for external processes to query).
        /// </summary>
        public static void PublishStatus(string state, string message, Dictionary<string, object> data)
        {
            Initialize();
            var payload = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            {
                { "state", state ?? "unknown" },
                { "message", message ?? string.Empty },
                { "updated_utc", DateTime.UtcNow.ToString("o") },
                { "data", data ?? new Dictionary<string, object>() }
            };

            lock (FileGate)
            {
                WriteAtomically(StatusFilePath, BridgeJson.Serialize(payload));
            }
        }

        /// <summary>
        /// 追加日志到 bridge.log。异常静默处理以免影响 Revit 命令执行。
        /// Appends a log line to bridge.log. Exceptions are silently handled to avoid disrupting Revit command execution.
        /// </summary>
        public static void AppendLog(string line)
        {
            try
            {
                Initialize();
                string record = DateTime.UtcNow.ToString("o") + " " + line + Environment.NewLine;
                File.AppendAllText(Path.Combine(LogDirectory, "bridge.log"), record, Utf8NoBom);
            }
            catch
            {
                // 日志写入绝不能中断 Revit 命令处理
                // Logging must never interrupt Revit command handling.
            }
        }

        /// <summary>
        /// 将文件移入归档目录，文件名附带状态标记和时间戳。
        /// Moves a file into the archive directory, appending state and timestamp to the filename.
        /// </summary>
        private static void Archive(string sourcePath, string requestId, string state)
        {
            string archivePath = Path.Combine(
                ArchiveDirectory,
                requestId + "." + state + "." + DateTime.UtcNow.Ticks + ".json");
            File.Move(sourcePath, archivePath);
        }

        /// <summary>
        /// 原子写入：先写入临时文件，再通过 Move/Replace 确保内容完整。
        /// Atomic write: writes to a temp file first, then uses Move/Replace to ensure content integrity.
        /// </summary>
        private static void WriteAtomically(string finalPath, string contents)
        {
            string temporaryPath = finalPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporaryPath, contents, Utf8NoBom);
                if (File.Exists(finalPath))
                {
                    File.Replace(temporaryPath, finalPath, null);
                }
                else
                {
                    File.Move(temporaryPath, finalPath);
                }
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
        }

        /// <summary>
        /// 规范化请求 ID：去空格、限长度、限制字符集。
        /// Normalizes a request ID: trims whitespace, enforces length limits, restricts character set.
        /// </summary>
        private static string NormalizeRequestId(string id)
        {
            string normalized = (id ?? string.Empty).Trim();
            if (normalized.Length == 0 || normalized.Length > 128)
            {
                throw new BridgeCommandException("命令 ID 必须为 1 到 128 个字符。");
            }

            foreach (char character in normalized)
            {
                if (!(char.IsLetterOrDigit(character) || character == '-' || character == '_' || character == '.'))
                {
                    throw new BridgeCommandException("命令 ID 只能包含字母、数字、-、_、.。");
                }
            }

            return normalized;
        }

        /// <summary>
        /// 存储请求对象到处理中文件路径的映射，用于 Complete 时归档。
        /// Stores the mapping from request object to its processing file path, used during Complete for archiving.
        /// </summary>
        private static class RequestProcessingPath
        {
            private static readonly Dictionary<BridgeRequest, string> Paths =
                new Dictionary<BridgeRequest, string>();

            public static void Store(BridgeRequest request, string path)
            {
                Paths[request] = path;
            }

            public static string Take(BridgeRequest request)
            {
                string path;
                if (!Paths.TryGetValue(request, out path))
                {
                    return null;
                }

                Paths.Remove(request);
                return path;
            }
        }
    }
}
