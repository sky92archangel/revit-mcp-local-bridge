using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace RevitCommandBridge
{
    internal static class BridgeProtocol
    {
        public const string Version = "revit-command-bridge/2.0";
    }

    internal static class PlanCommandExecutor
    {
        private const int MaximumSteps = 500;

        private static readonly string[] AtomicOperations =
        {
            "query_document",
            "query_catalog",
            "query_elements",
            "create_level",
            "create_grid",
            "create_wall",
            "create_direct_shape",
            "create_mep_curve",
            "connect_mep",
            "place_family_instance",
            "create_structural_member",
            "set_parameters",
            "delete_elements",
            "select_elements"
        };

        private static readonly HashSet<string> WriteOperations = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "create_level",
            "create_grid",
            "create_wall",
            "create_direct_shape",
            "create_mep_curve",
            "connect_mep",
            "place_family_instance",
            "create_structural_member",
            "set_parameters",
            "delete_elements"
        };

        public static string[] SupportedAtomicOperations
        {
            get { return AtomicOperations.ToArray(); }
        }

        public static bool IsWritePlan(BridgeRequest request)
        {
            return ParseSteps(request).Any(step => WriteOperations.Contains(step.Operation));
        }

        public static BridgeResponse Execute(UIApplication uiApplication, Document document, BridgeRequest request)
        {
            List<PlanStep> steps = ParseSteps(request);
            bool hasWrites = steps.Any(step => WriteOperations.Contains(step.Operation));
            if (document.IsReadOnly && hasWrites)
            {
                throw new BridgeCommandException("当前 Revit 文档为只读，不能执行包含写操作的计划。");
            }

            var context = new PlanExecutionContext(uiApplication, document, request.Preview);
            var stepResults = new List<Dictionary<string, object>>();
            if (request.Preview || !hasWrites)
            {
                ExecuteSteps(steps, context, stepResults);
            }
            else
            {
                using (Transaction transaction = new Transaction(document, "RCB 通用建模计划"))
                {
                    TransactionStatus started = transaction.Start();
                    if (started != TransactionStatus.Started)
                    {
                        throw new BridgeCommandException("Revit 未能启动计划事务：" + started);
                    }

                    try
                    {
                        ExecuteSteps(steps, context, stepResults);
                        TransactionStatus committed = transaction.Commit();
                        if (committed != TransactionStatus.Committed)
                        {
                            throw new BridgeCommandException("Revit 未能提交计划事务：" + committed);
                        }
                    }
                    catch
                    {
                        if (transaction.GetStatus() == TransactionStatus.Started)
                        {
                            transaction.RollBack();
                        }
                        throw;
                    }
                }
            }

            context.ApplyDeferredUiActions();
            var data = new Dictionary<string, object>
            {
                { "operation", "execute_plan" },
                { "protocol", BridgeProtocol.Version },
                { "transaction_mode", hasWrites ? "all_or_nothing" : "read_only" },
                { "step_count", steps.Count },
                { "write_step_count", steps.Count(step => WriteOperations.Contains(step.Operation)) },
                { "steps", stepResults }
            };
            if (context.DeferredPreviewReferences.Count > 0)
            {
                data["deferred_preview_references"] = context.DeferredPreviewReferences.ToArray();
            }

            string state = request.Preview ? "preview" : "completed";
            string message = request.Preview
                ? "计划校验完成；未修改 Revit 模型。"
                : "计划已执行；全部写操作已作为一个 Revit 事务提交。";
            return BridgeResponse.Success(state, message, data);
        }

        private static void ExecuteSteps(
            IList<PlanStep> steps,
            PlanExecutionContext context,
            IList<Dictionary<string, object>> stepResults)
        {
            foreach (PlanStep step in steps)
            {
                try
                {
                    Dictionary<string, object> data = RevitPlanOperations.Execute(step, context);
                    context.RegisterResult(step.Id, data);
                    stepResults.Add(new Dictionary<string, object>
                    {
                        { "id", step.Id },
                        { "operation", step.Operation },
                        { "state", context.Preview && WriteOperations.Contains(step.Operation) ? "preview" : "completed" },
                        { "data", data }
                    });
                }
                catch (BridgeCommandException ex)
                {
                    throw new BridgeCommandException(
                        "计划步骤“" + step.Id + "”（" + step.Operation + "）失败：" + ex.Message);
                }
                catch (Exception ex)
                {
                    throw new BridgeCommandException(
                        "计划步骤“" + step.Id + "”（" + step.Operation + "）执行异常：" + ex.Message);
                }
            }
        }

        private static List<PlanStep> ParseSteps(BridgeRequest request)
        {
            object rawSteps = PlanValues.Get(request.Arguments, "steps", "operations");
            List<Dictionary<string, object>> values = PlanValues.DictionaryList(rawSteps, "steps");
            if (values.Count == 0)
            {
                throw new BridgeCommandException("execute_plan 至少需要一个 steps 项。");
            }
            if (values.Count > MaximumSteps)
            {
                throw new BridgeCommandException("单个计划最多允许 " + MaximumSteps + " 个步骤。");
            }

            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var result = new List<PlanStep>();
            for (int index = 0; index < values.Count; index++)
            {
                Dictionary<string, object> value = values[index];
                string id = PlanValues.String(value, "step_" + (index + 1).ToString(CultureInfo.InvariantCulture), "id");
                if (!IsValidStepId(id))
                {
                    throw new BridgeCommandException("步骤 id 只能包含字母、数字、点、下划线或连字符：" + id);
                }
                if (!ids.Add(id))
                {
                    throw new BridgeCommandException("步骤 id 重复：" + id);
                }

                string operation = NormalizeAtomicOperation(PlanValues.String(value, null, "operation", "op"));
                if (string.IsNullOrWhiteSpace(operation))
                {
                    throw new BridgeCommandException("步骤“" + id + "”缺少 operation。");
                }
                if (!AtomicOperations.Contains(operation, StringComparer.OrdinalIgnoreCase))
                {
                    throw new BridgeCommandException(
                        "步骤“" + id + "”不支持原子操作“" + operation + "”。支持：" + string.Join("、", AtomicOperations));
                }

                object rawArguments = PlanValues.Get(value, "args", "arguments");
                Dictionary<string, object> arguments = rawArguments == null
                    ? new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
                    : PlanValues.Dictionary(rawArguments, "steps[].args");
                result.Add(new PlanStep { Id = id, Operation = operation, Arguments = arguments });
            }
            return result;
        }

        private static string NormalizeAtomicOperation(string operation)
        {
            string value = (operation ?? string.Empty).Trim().ToLowerInvariant();
            switch (value)
            {
                case "查询文档": return "query_document";
                case "查询目录": return "query_catalog";
                case "查询元素": return "query_elements";
                case "创建标高": return "create_level";
                case "创建轴网": return "create_grid";
                case "创建墙":
                case "创建墙体": return "create_wall";
                case "创建通用几何": return "create_direct_shape";
                case "创建机电管线": return "create_mep_curve";
                case "连接机电": return "connect_mep";
                case "放置族实例": return "place_family_instance";
                case "创建结构构件": return "create_structural_member";
                case "设置参数": return "set_parameters";
                case "删除元素": return "delete_elements";
                case "选择元素": return "select_elements";
                default: return value;
            }
        }

        private static bool IsValidStepId(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > 128)
            {
                return false;
            }
            foreach (char character in value)
            {
                if (!(char.IsLetterOrDigit(character) || character == '-' || character == '_' || character == '.'))
                {
                    return false;
                }
            }
            return true;
        }
    }

    internal sealed class PlanExecutionContext
    {
        private readonly Dictionary<string, Dictionary<string, object>> _results =
            new Dictionary<string, Dictionary<string, object>>(StringComparer.OrdinalIgnoreCase);
        private readonly List<ElementId> _selection = new List<ElementId>();
        private bool _showSelection;

        public PlanExecutionContext(UIApplication uiApplication, Document document, bool preview)
        {
            UiApplication = uiApplication;
            Document = document;
            Preview = preview;
            DeferredPreviewReferences = new List<string>();
        }

        public UIApplication UiApplication { get; private set; }
        public Document Document { get; private set; }
        public bool Preview { get; private set; }
        public List<string> DeferredPreviewReferences { get; private set; }

        public void RegisterResult(string stepId, Dictionary<string, object> result)
        {
            _results[stepId] = result ?? new Dictionary<string, object>();
        }

        public IList<ElementId> ResolveElementIds(IDictionary<string, object> arguments, params string[] fieldNames)
        {
            object value = PlanValues.Get(arguments, fieldNames);
            if (value == null)
            {
                throw new BridgeCommandException("缺少元素目标参数：" + string.Join("/", fieldNames));
            }

            var tokens = new List<object>();
            if (value is string || !(value is System.Collections.IEnumerable))
            {
                tokens.Add(value);
            }
            else
            {
                foreach (object token in (System.Collections.IEnumerable)value)
                {
                    tokens.Add(token);
                }
            }

            var ids = new List<ElementId>();
            foreach (object token in tokens)
            {
                AddResolvedToken(ids, token);
            }
            return ids.GroupBy(id => id.IntegerValue).Select(group => group.First()).ToList();
        }

        public ElementId ResolveSingleElementId(IDictionary<string, object> arguments, params string[] fieldNames)
        {
            IList<ElementId> values = ResolveElementIds(arguments, fieldNames);
            if (values.Count == 0 && Preview)
            {
                return ElementId.InvalidElementId;
            }
            if (values.Count != 1)
            {
                throw new BridgeCommandException("参数 " + string.Join("/", fieldNames) + " 必须解析为一个元素。");
            }
            return values[0];
        }

        public void RequestSelection(IEnumerable<ElementId> ids, bool show)
        {
            _selection.Clear();
            _selection.AddRange(ids);
            _showSelection = show;
        }

        public void ApplyDeferredUiActions()
        {
            if (Preview || _selection.Count == 0 || UiApplication.ActiveUIDocument == null)
            {
                return;
            }
            UiApplication.ActiveUIDocument.Selection.SetElementIds(_selection);
            if (_showSelection)
            {
                UiApplication.ActiveUIDocument.ShowElements(_selection);
            }
        }

        private void AddResolvedToken(ICollection<ElementId> target, object token)
        {
            if (token == null)
            {
                throw new BridgeCommandException("元素目标不能为 null。");
            }
            string text = token as string;
            if (text != null && (text.StartsWith("$", StringComparison.Ordinal) || text.StartsWith("@", StringComparison.Ordinal)))
            {
                string stepId = text.Substring(1);
                Dictionary<string, object> result;
                if (!_results.TryGetValue(stepId, out result))
                {
                    throw new BridgeCommandException("找不到前置步骤引用：" + text);
                }

                object resolved = PlanValues.Get(result, "element_ids", "element_id", "id");
                if (resolved == null)
                {
                    if (Preview)
                    {
                        if (!DeferredPreviewReferences.Contains(text))
                        {
                            DeferredPreviewReferences.Add(text);
                        }
                        return;
                    }
                    throw new BridgeCommandException("步骤引用“" + text + "”没有返回元素 ID。");
                }

                if (resolved is string || !(resolved is System.Collections.IEnumerable))
                {
                    AddResolvedToken(target, resolved);
                }
                else
                {
                    foreach (object item in (System.Collections.IEnumerable)resolved)
                    {
                        AddResolvedToken(target, item);
                    }
                }
                return;
            }

            int id;
            if (!int.TryParse(Convert.ToString(token, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out id) || id <= 0)
            {
                throw new BridgeCommandException("无效元素 ID：" + token);
            }
            target.Add(new ElementId(id));
        }
    }
}
