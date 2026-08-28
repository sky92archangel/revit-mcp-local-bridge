namespace RevitCommandBridge
{
    /// <summary>
    /// 桥接运行时，管理 Revit 内部的轮询、事件触发、心跳和请求处理生命周期�?
    /// Bridge runtime managing the polling, event triggering, heartbeat, and request processing lifecycle inside Revit.
    /// </summary>
    internal sealed class BridgeRuntime : IDisposable
    {
        // 轮询间隔 300ms，心跳最小间�?2000ms
        // Polling interval 300ms, minimum heartbeat interval 2000ms
        private const int PollIntervalMilliseconds = 300;
        private const int HeartbeatIntervalMilliseconds = 2000;
        private static readonly object StaticGate = new object();
        private static BridgeRuntime _current;

        private readonly object RaiseGate = new object();
        private readonly object StatusGate = new object();
        private readonly BridgeEventHandler _handler;
        private readonly ExternalEvent _externalEvent;
        private readonly Timer _pollTimer;
        private DateTime _lastHeartbeatUtc;
        private Dictionary<string, object> _lastDocumentStatus;
        private volatile bool _stopped;
        private int _disposeStarted;

        /// <summary>
        /// 私有构造：初始化文件队列、恢复未完成的请求、启动轮询定时器�?
        /// Private constructor: initializes the file queue, recovers unfinished requests, starts the poll timer.
        /// </summary>
        private BridgeRuntime()
        {
            BridgeFileQueue.Initialize();
            int recovered = BridgeFileQueue.RecoverProcessingRequests();
            _handler = new BridgeEventHandler(this);
            _externalEvent = ExternalEvent.Create(_handler);
            _lastHeartbeatUtc = DateTime.MinValue;
            // 发布初始就绪心跳
            // Publish initial ready heartbeat
            PublishHeartbeat(true, "running", "Revit 命令桥已就绪�?, new Dictionary<string, object>
            {
                { "revit_api", BridgeBuildInfo.RevitVersion },
                { "protocol", BridgeProtocol.Version },
                { "recovered_requests", recovered }
            });
            // 延迟 250ms 后开始定期轮�?
            // Start periodic polling after a 250ms initial delay
            _pollTimer = new Timer(PollQueue, null, 250, PollIntervalMilliseconds);
            BridgeFileQueue.AppendLog("runtime started recovered=" + recovered);
        }

        /// <summary>
        /// 启动运行时（单例模式）�?
        /// Starts the runtime (singleton pattern).
        /// </summary>
        public static BridgeRuntime Start()
        {
            lock (StaticGate)
            {
                if (_current == null)
                {
                    _current = new BridgeRuntime();
                }

                return _current;
            }
        }

        /// <summary>
        /// 获取当前运行时实例�?
        /// Gets the current runtime instance.
        /// </summary>
        public static BridgeRuntime Current
        {
            get
            {
                lock (StaticGate)
                {
                    return _current;
                }
            }
        }

        /// <summary>
        /// 运行时是否正在运行�?
        /// Whether the runtime is running.
        /// </summary>
        public static bool IsRunning
        {
            get
            {
                BridgeRuntime runtime = Current;
                return runtime != null && !runtime._stopped;
            }
        }

        /// <summary>
        /// 通知队列有新的请求需要处理。若已有待处理请求且无挂起事件，则触�?ExternalEvent�?
        /// Signals the queue that new requests are pending. Raises ExternalEvent if there are pending requests and no event is already pending.
        /// </summary>
        public void SignalQueue()
        {
            if (_stopped)
            {
                return;
            }

            PublishHeartbeat(false, "running", "Revit 命令桥正在等待命令�?, null);
            if (!BridgeFileQueue.HasPendingRequests())
            {
                return;
            }

            lock (RaiseGate)
            {
                try
                {
                    if (_stopped || _externalEvent.IsPending)
                    {
                        return;
                    }

                    ExternalEventRequest result = _externalEvent.Raise();
                    // 仅记录非正常接受的结�?
                    // Only log results that are not accepted or pending
                    if (result != ExternalEventRequest.Accepted && result != ExternalEventRequest.Pending)
                    {
                        BridgeFileQueue.AppendLog("raise returned " + result);
                    }
                }
                catch (Exception ex)
                {
                    BridgeFileQueue.AppendLog("raise failed: " + ex.Message);
                }
            }
        }

        /// <summary>
        /// �?Revit API 上下文中处理一个请求：领取、执行、完成�?
        /// Processes one request in the Revit API context: claim, execute, complete.
        /// </summary>
        public void ProcessOne(UIApplication uiApplication)
        {
            BridgeRequest request;
            if (!BridgeFileQueue.TryClaimNext(out request))
            {
                return;
            }

            BridgeResponse response;
            // 更新心跳为忙碌状�?
            // Update heartbeat to busy state
            PublishHeartbeat(true, "busy", "Revit 正在执行命令�?, new Dictionary<string, object>
            {
                { "request_id", request.Id },
                { "operation", request.Operation },
                { "source", request.Source }
            }, uiApplication);
            try
            {
                response = RevitCommandExecutor.Execute(uiApplication, request);
            }
            catch (BridgeCommandException ex)
            {
                // 业务逻辑异常直接包装为失败响�?
                // Wrap business logic exceptions directly into a failure response
                response = BridgeResponse.Failure(ex.Message, null);
            }
            catch (Exception ex)
            {
                BridgeFileQueue.AppendLog("execution failed id=" + request.Id + " error=" + ex);
                response = BridgeResponse.Failure("Revit 执行失败�? + ex.Message, null);
            }

            try
            {
                BridgeFileQueue.Complete(request, response);
            }
            catch (Exception ex)
            {
                BridgeFileQueue.AppendLog("completion failed id=" + request.Id + " error=" + ex);
            }

            // 执行完成后恢复就绪心�?
            // Restore ready heartbeat after execution
            PublishHeartbeat(true, "running", "Revit 命令桥正在等待命令�?, new Dictionary<string, object>
            {
                { "last_request_id", request.Id },
                { "last_operation", request.Operation },
                { "last_ok", response.Ok },
                { "last_state", response.State }
            }, uiApplication);
        }

        /// <summary>
        /// 释放运行时资源：停止轮询、销毁外部事件、发布停止状态�?
        /// Disposes runtime resources: stops polling, destroys the external event, publishes stopped status.
        /// </summary>
        public void Dispose()
        {
            // 确保只执行一次释�?
            // Ensure disposal runs only once
            if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
            {
                return;
            }

            lock (RaiseGate)
            {
                _stopped = true;
                _pollTimer.Dispose();
                _externalEvent.Dispose();
            }

            BridgeFileQueue.PublishStatus("stopped", "Revit 命令桥已停止�?, new Dictionary<string, object>
            {
                { "revit_api", BridgeBuildInfo.RevitVersion },
                { "protocol", BridgeProtocol.Version }
            });
            BridgeFileQueue.AppendLog("runtime stopped");
            lock (StaticGate)
            {
                // 仅当 _current 仍指向本实例时才清空
                // Only clear _current if it still points to this instance
                if (ReferenceEquals(_current, this))
                {
                    _current = null;
                }
            }
        }

        /// <summary>
        /// 定时器回调，触发队列信号检查�?
        /// Timer callback that triggers the queue signal check.
        /// </summary>
        private void PollQueue(object state)
        {
            try
            {
                SignalQueue();
            }
            catch (Exception ex)
            {
                BridgeFileQueue.AppendLog("queue poll failed: " + ex);
            }
        }

        /// <summary>
        /// 发布心跳状态（含频率控制，force=true 时跳过频率限制）�?
        /// Publishes heartbeat status with rate limiting; force=true bypasses the rate limit.
        /// </summary>
        private void PublishHeartbeat(bool force, string state, string message, Dictionary<string, object> data, UIApplication uiApplication = null)
        {
            DateTime now = DateTime.UtcNow;
            // 非强制模式下检查心跳频率限�?
            // Check heartbeat rate limit in non-force mode
            if (!force && (now - _lastHeartbeatUtc).TotalMilliseconds < HeartbeatIntervalMilliseconds)
            {
                return;
            }

            _lastHeartbeatUtc = now;
            var payload = data == null
                ? new Dictionary<string, object>()
                : new Dictionary<string, object>(data);
            AddDocumentStatus(payload, uiApplication);
            BridgeFileQueue.PublishStatus(state, message, payload);
        }

        /// <summary>
        /// 将当前文档状态（标题、路径、只读等）附加到心跳数据中�?
        /// Attaches the current document status (title, path, read-only, etc.) to the heartbeat data.
        /// </summary>
        private void AddDocumentStatus(Dictionary<string, object> data, UIApplication uiApplication)
        {
            if (data == null)
            {
                return;
            }

            lock (StatusGate)
            {
                // 无活动文档时记录 document_open=false
                // Record document_open=false when there is no active document
                if (uiApplication != null && uiApplication.ActiveUIDocument == null)
                {
                    _lastDocumentStatus = new Dictionary<string, object>
                    {
                        { "document_open", false }
                    };
                }
                else if (uiApplication != null && uiApplication.ActiveUIDocument != null)
                {
                    Autodesk.Revit.DB.Document document = uiApplication.ActiveUIDocument.Document;
                    if (document != null && document.IsValidObject)
                    {
                        _lastDocumentStatus = new Dictionary<string, object>
                        {
                            { "document_open", true },
                            { "document_title", document.Title },
                            { "document_path", document.PathName ?? string.Empty },
                            { "document_read_only", document.IsReadOnly }
                        };
                    }
                }

                if (_lastDocumentStatus == null)
                {
                    return;
                }

                // 将缓存的状态合并到心跳数据�?
                // Merge cached document status into the heartbeat data
                foreach (KeyValuePair<string, object> pair in _lastDocumentStatus)
                {
                    data[pair.Key] = pair.Value;
                }
            }
        }
    }

    /// <summary>
    /// Revit 外部事件处理器，�?Revit API 上下文中调用 BridgeRuntime.ProcessOne�?
    /// Revit external event handler that calls BridgeRuntime.ProcessOne within the Revit API context.
    /// </summary>
    internal sealed class BridgeEventHandler : IExternalEventHandler
    {
        private readonly BridgeRuntime _runtime;

        /// <summary>
        /// 用运行时引用构造事件处理器�?
        /// Constructs the event handler with a reference to the runtime.
        /// </summary>
        public BridgeEventHandler(BridgeRuntime runtime)
        {
            _runtime = runtime;
        }

        /// <summary>
        /// �?Revit 主线程中执行一次请求处理�?
        /// Executes one request processing cycle on the Revit main thread.
        /// </summary>
        public void Execute(UIApplication app)
        {
            _runtime.ProcessOne(app);
        }

        /// <summary>
        /// 返回事件处理器名称，用于 Revit 内部标识�?
        /// Returns the event handler name for Revit internal identification.
        /// </summary>
        public string GetName()
        {
            return "Revit Command Bridge Queue Handler";
        }
    }
}
