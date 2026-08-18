using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace RevitCommandBridge
{
    internal static class BridgeFileQueue
    {
        private static readonly object FileGate = new object();
        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);
        private static bool _initialized;

        public static string RootDirectory
        {
            get
            {
                return BridgeBuildInfo.QueueRootDirectory;
            }
        }

        public static string InboxDirectory { get { return Path.Combine(RootDirectory, "inbox"); } }
        public static string ProcessingDirectory { get { return Path.Combine(RootDirectory, "processing"); } }
        public static string OutboxDirectory { get { return Path.Combine(RootDirectory, "outbox"); } }
        public static string ArchiveDirectory { get { return Path.Combine(RootDirectory, "archive"); } }
        public static string LogDirectory { get { return Path.Combine(RootDirectory, "logs"); } }
        public static string StatusFilePath { get { return Path.Combine(RootDirectory, "status.json"); } }

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

        public static bool TryClaimNext(out BridgeRequest request)
        {
            Initialize();
            request = null;

            lock (FileGate)
            {
                string[] files = Directory.GetFiles(InboxDirectory, "*.request.json")
                    .OrderBy(path => File.GetCreationTimeUtc(path))
                    .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                foreach (string incomingPath in files)
                {
                    string processingPath = Path.Combine(
                        ProcessingDirectory,
                        Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(incomingPath)) + ".processing.json");

                    try
                    {
                        File.Move(incomingPath, processingPath);
                    }
                    catch (IOException)
                    {
                        continue;
                    }

                    try
                    {
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
                        RequestProcessingPath.Store(parsed, processingPath);
                        request = parsed;
                        return true;
                    }
                    catch (Exception ex)
                    {
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
                        if (File.Exists(resultPath))
                        {
                            Archive(processingPath, id, "already-completed");
                        }
                        else if (!File.Exists(inboxPath))
                        {
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
                // Logging must never interrupt Revit command handling.
            }
        }

        private static void Archive(string sourcePath, string requestId, string state)
        {
            string archivePath = Path.Combine(
                ArchiveDirectory,
                requestId + "." + state + "." + DateTime.UtcNow.Ticks + ".json");
            File.Move(sourcePath, archivePath);
        }

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
