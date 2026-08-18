using System;
using System.Collections.Generic;
using System.Threading;
using Autodesk.Revit.UI;

namespace RevitCommandBridge
{
    internal sealed class BridgeRuntime : IDisposable
    {
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

        private BridgeRuntime()
        {
            BridgeFileQueue.Initialize();
            int recovered = BridgeFileQueue.RecoverProcessingRequests();
            _handler = new BridgeEventHandler(this);
            _externalEvent = ExternalEvent.Create(_handler);
            _lastHeartbeatUtc = DateTime.MinValue;
            PublishHeartbeat(true, "running", "Revit 命令桥已就绪。", new Dictionary<string, object>
            {
                { "revit_api", BridgeBuildInfo.RevitVersion },
                { "protocol", BridgeProtocol.Version },
                { "recovered_requests", recovered }
            });
            _pollTimer = new Timer(PollQueue, null, 250, PollIntervalMilliseconds);
            BridgeFileQueue.AppendLog("runtime started recovered=" + recovered);
        }

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

        public static bool IsRunning
        {
            get
            {
                BridgeRuntime runtime = Current;
                return runtime != null && !runtime._stopped;
            }
        }

        public void SignalQueue()
        {
            if (_stopped)
            {
                return;
            }

            PublishHeartbeat(false, "running", "Revit 命令桥正在等待命令。", null);
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

        public void ProcessOne(UIApplication uiApplication)
        {
            BridgeRequest request;
            if (!BridgeFileQueue.TryClaimNext(out request))
            {
                return;
            }

            BridgeResponse response;
            PublishHeartbeat(true, "busy", "Revit 正在执行命令。", new Dictionary<string, object>
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
                response = BridgeResponse.Failure(ex.Message, null);
            }
            catch (Exception ex)
            {
                BridgeFileQueue.AppendLog("execution failed id=" + request.Id + " error=" + ex);
                response = BridgeResponse.Failure("Revit 执行失败：" + ex.Message, null);
            }

            try
            {
                BridgeFileQueue.Complete(request, response);
            }
            catch (Exception ex)
            {
                BridgeFileQueue.AppendLog("completion failed id=" + request.Id + " error=" + ex);
            }

            PublishHeartbeat(true, "running", "Revit 命令桥正在等待命令。", new Dictionary<string, object>
            {
                { "last_request_id", request.Id },
                { "last_operation", request.Operation },
                { "last_ok", response.Ok },
                { "last_state", response.State }
            }, uiApplication);
        }

        public void Dispose()
        {
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

            BridgeFileQueue.PublishStatus("stopped", "Revit 命令桥已停止。", new Dictionary<string, object>
            {
                { "revit_api", BridgeBuildInfo.RevitVersion },
                { "protocol", BridgeProtocol.Version }
            });
            BridgeFileQueue.AppendLog("runtime stopped");
            lock (StaticGate)
            {
                if (ReferenceEquals(_current, this))
                {
                    _current = null;
                }
            }
        }

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

        private void PublishHeartbeat(bool force, string state, string message, Dictionary<string, object> data, UIApplication uiApplication = null)
        {
            DateTime now = DateTime.UtcNow;
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

        private void AddDocumentStatus(Dictionary<string, object> data, UIApplication uiApplication)
        {
            if (data == null)
            {
                return;
            }

            lock (StatusGate)
            {
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

                foreach (KeyValuePair<string, object> pair in _lastDocumentStatus)
                {
                    data[pair.Key] = pair.Value;
                }
            }
        }
    }

    internal sealed class BridgeEventHandler : IExternalEventHandler
    {
        private readonly BridgeRuntime _runtime;

        public BridgeEventHandler(BridgeRuntime runtime)
        {
            _runtime = runtime;
        }

        public void Execute(UIApplication app)
        {
            _runtime.ProcessOne(app);
        }

        public string GetName()
        {
            return "Revit Command Bridge Queue Handler";
        }
    }
}
