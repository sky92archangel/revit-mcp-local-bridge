namespace RevitCommandBridge
{
    /// <summary>
    /// 定义桥接协议版本常量�?/ Defines the bridge protocol version constants.
    /// </summary>
    internal static class BridgeProtocol
    {
        /// <summary>当前协议版本标识�?/ Current protocol version identifier.</summary>
        public const string Version = "revit-command-bridge/2.0";
    }

    /// <summary>
    /// 执行建模计划：解析步骤、事务管理、分步执行�?/ Executes a modeling plan: step parsing, transaction management, and step-by-step execution.
    /// </summary>
    internal static class PlanCommandExecutor
    {
        /// <summary>单个计划允许的最大步骤数�?/ Maximum number of steps allowed in a single plan.</summary>
        private const int MaximumSteps = 500;

        /// <summary>
        /// 所有支持的原子操作列表�?/ List of all supported atomic operations.
        /// </summary>
        private static readonly string[] AtomicOperations =
        {
            "query_document",
            "query_catalog",
            "query_elements",
            "query_references",
            "query_parameters",
            "query_geometry",
            "query_room",
            "check_interferences",
            "query_mep_network",
            "query_view_range",
            "query_selection",
            "create_level",
            "create_grid",
            "create_wall",
            "create_floor",
            "create_room",
            "create_space",
            "create_model_curve",
            "create_direct_shape",
            "create_swept_shape",
            "create_mep_curve",
            "connect_mep",
            "create_mep_system",
            "create_insulation",
            "place_family_instance",
            "load_family",
            "create_structural_member",
            "create_view",
            "create_drafting_view",
            "create_section_view",
            "create_elevation_view",
            "create_callout",
            "duplicate_view",
            "create_view_template",
            "create_sheet",
            "transform_elements",
            "rename_element",
            "set_element_curve",
            "place_view_on_sheet",
            "create_detail_curve",
            "create_text_note",
            "create_dimension",
            "create_tag",
            "create_filled_region",
            "create_revision",
            "create_revision_cloud",
            "create_schedule",
            "place_schedule_on_sheet",
            "set_view_properties",
            "set_element_overrides",
            "set_category_overrides",
            "manage_view_filters",
            "set_view_range",
            "manage_schedule_fields",
            "manage_graphics_resources",
            "create_opening",
            "set_parameters",
            "manage_schema_data",
            "manage_family_parameters",
            "manage_project_parameters",
            "duplicate_type",
            "delete_elements",
            "select_elements",
            "export",
            "save_document"
        };

        /// <summary>
        /// 会修�?Revit 模型的写操作集合�?/ Write operations that modify the Revit model.
        /// </summary>
        private static readonly HashSet<string> WriteOperations = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "create_level",
            "create_grid",
            "create_wall",
            "create_floor",
            "create_room",
            "create_space",
            "create_model_curve",
            "create_direct_shape",
            "create_swept_shape",
            "create_mep_curve",
            "connect_mep",
            "create_mep_system",
            "create_insulation",
            "place_family_instance",
            "load_family",
            "create_structural_member",
            "create_view",
            "create_drafting_view",
            "create_section_view",
            "create_elevation_view",
            "create_callout",
            "duplicate_view",
            "create_view_template",
            "create_sheet",
            "transform_elements",
            "rename_element",
            "set_element_curve",
            "place_view_on_sheet",
            "create_detail_curve",
            "create_text_note",
            "create_dimension",
            "create_tag",
            "create_filled_region",
            "create_revision",
            "create_revision_cloud",
            "create_schedule",
            "place_schedule_on_sheet",
            "set_view_properties",
            "create_opening",
            "create_swept_shape",
            "create_insulation",
            "set_element_overrides",
            "set_category_overrides",
            "manage_view_filters",
            "set_view_range",
            "manage_schedule_fields",
            "manage_graphics_resources",
            "manage_schema_data",
            "manage_family_parameters",
            "manage_project_parameters",
            "duplicate_type",
            "set_parameters",
            "delete_elements"
        };

        /// <summary>
        /// 涉及外部文件输出的操作集合（export / save_document）�?/ Operations involving external file output (export / save_document).
        /// </summary>
        private static readonly HashSet<string> ExternalOperations = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "export",
            "save_document"
        };

        /// <summary>
        /// 文档保存操作集合�?/ Document-saving operations.
        /// </summary>
        private static readonly HashSet<string> DocumentWriteOperations = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "save_document"
        };

        /// <summary>获取受支持的原子操作列表副本�?/ Gets a copy of the supported atomic operations list.</summary>
        public static string[] SupportedAtomicOperations
        {
            get { return AtomicOperations.ToArray(); }
        }

        /// <summary>
        /// 判断请求是否包含写操作步骤�?/ Determines whether the request contains any write-operation steps.
        /// </summary>
        public static bool IsWritePlan(BridgeRequest request)
        {
            return ParseSteps(request).Any(step =>
                WriteOperations.Contains(step.Operation) || DocumentWriteOperations.Contains(step.Operation));
        }

        /// <summary>
        /// 执行建模计划：校验、事务包装、步骤执行、返回结果�?/ Executes a modeling plan: validation, transaction wrapping, step execution, and result collection.
        /// </summary>
        public static BridgeResponse Execute(UIApplication uiApplication, Document document, BridgeRequest request)
        {
            List<PlanStep> steps = ParseSteps(request);
            bool hasWrites = steps.Any(step => WriteOperations.Contains(step.Operation));
            bool hasExternalOperations = steps.Any(step => ExternalOperations.Contains(step.Operation));
            if (hasExternalOperations && steps.Count > 1)
            {
                throw new BridgeCommandException(
                    "export �?save_document 必须作为单独 execute_plan 执行，不能与建模或查询步骤混用。这样可避免外部文件操作破坏 Revit 事务边界�?);
            }
            if (document.IsReadOnly && (hasWrites || steps.Any(step => DocumentWriteOperations.Contains(step.Operation))))
            {
                throw new BridgeCommandException("当前 Revit 文档为只读，不能执行包含写操作的计划�?);
            }

            var context = new PlanExecutionContext(uiApplication, document, request.Preview);
            var stepResults = new List<Dictionary<string, object>>();
            BridgeFailurePreprocessor failurePreprocessor = null;
            // 预览模式或纯只读模式不需事务，直接执�?
            // Preview or read-only mode: execute without a transaction
            if (request.Preview || !hasWrites)
            {
                ExecuteSteps(steps, context, stepResults);
            }
            else
            {
                // 写操作在单个 Revit 事务中执行，支持全部提交或全部回�?
                // All write operations execute inside a single Revit transaction (all-or-nothing)
                    if (started != TransactionStatus.Started)
                    {
                        throw new BridgeCommandException("Revit 未能启动计划事务�? + started);
                    }

                    // 安装失败预处理器以收集警告而非弹窗
                    // Install a failure preprocessor to collect warnings instead of showing dialogs
                    failurePreprocessor = new BridgeFailurePreprocessor();
                    FailureHandlingOptions failureOptions = transaction.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(failurePreprocessor);
                    failureOptions.SetForcedModalHandling(false);
                    transaction.SetFailureHandlingOptions(failureOptions);

                    try
                    {
                        ExecuteSteps(steps, context, stepResults);
                        TransactionStatus committed = transaction.Commit();
                        if (committed != TransactionStatus.Committed)
                        {
                            throw new BridgeCommandException("Revit 未能提交计划事务�? + committed);
                        }
                    }
                    catch
                    {
                        // 事务未提交时回滚以保持模型一致�?
                        // Roll back if the transaction has not been committed
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
                { "transaction_mode", hasWrites ? "all_or_nothing" : (hasExternalOperations ? "external_side_effect" : "read_only") },
                { "step_count", steps.Count },
                { "write_step_count", steps.Count(step => WriteOperations.Contains(step.Operation)) },
                { "external_step_count", steps.Count(step => ExternalOperations.Contains(step.Operation)) },
                { "steps", stepResults }
            };
            if (failurePreprocessor != null && failurePreprocessor.Messages.Count > 0)
            {
                data["failure_messages"] = failurePreprocessor.Messages.ToArray();
            }
            if (context.DeferredPreviewReferences.Count > 0)
            {
                data["deferred_preview_references"] = context.DeferredPreviewReferences.ToArray();
            }

            string state = request.Preview ? "preview" : "completed";
            string message = request.Preview
                ? "计划校验完成；未修改 Revit 模型或输出文件�?
                : (hasWrites
                    ? "计划已执行；全部模型写操作已作为一�?Revit 事务提交�?
                    : (hasExternalOperations
                        ? "外部输出/保存计划已执行�?
                        : "只读计划已执行�?));
            return BridgeResponse.Success(state, message, data);
        }

        /// <summary>
        /// 逐步骤执行计划，收集结果或抛出异常�?/ Executes plan steps sequentially, collecting results or throwing on failure.
        /// </summary>
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
                        "计划步骤�? + step.Id + "”（" + step.Operation + "）失败：" + ex.Message);
                }
                catch (Exception ex)
                {
                    throw new BridgeCommandException(
                        "计划步骤�? + step.Id + "”（" + step.Operation + "）执行异常：" + ex.Message);
                }
            }
        }

        /// <summary>
        /// 从请求中解析步骤列表，验�?ID 合法性、操作存在性、重复性�?/ Parses the step list from a request, validating IDs, operations, and uniqueness.
        /// </summary>
        private static List<PlanStep> ParseSteps(BridgeRequest request)
        {
            object rawSteps = PlanValues.Get(request.Arguments, "steps", "operations");
            List<Dictionary<string, object>> values = PlanValues.DictionaryList(rawSteps, "steps");
            if (values.Count == 0)
            {
                throw new BridgeCommandException("execute_plan 至少需要一�?steps 项�?);
            }
            if (values.Count > MaximumSteps)
            {
                throw new BridgeCommandException("单个计划最多允�?" + MaximumSteps + " 个步骤�?);
            }

            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var result = new List<PlanStep>();
            for (int index = 0; index < values.Count; index++)
            {
                Dictionary<string, object> value = values[index];
                string id = PlanValues.String(value, "step_" + (index + 1).ToString(CultureInfo.InvariantCulture), "id");
                if (!IsValidStepId(id))
                {
                    throw new BridgeCommandException("步骤 id 只能包含字母、数字、点、下划线或连字符�? + id);
                }
                if (!ids.Add(id))
                {
                    throw new BridgeCommandException("步骤 id 重复�? + id);
                }

                string operation = NormalizeAtomicOperation(PlanValues.String(value, null, "operation", "op"));
                if (string.IsNullOrWhiteSpace(operation))
                {
                    throw new BridgeCommandException("步骤�? + id + "”缺�?operation�?);
                }
                if (!AtomicOperations.Contains(operation, StringComparer.OrdinalIgnoreCase))
                {
                    throw new BridgeCommandException(
                        "步骤�? + id + "”不支持原子操作�? + operation + "”。支持：" + string.Join("�?, AtomicOperations));
                }

                object rawArguments = PlanValues.Get(value, "args", "arguments");
                Dictionary<string, object> arguments = rawArguments == null
                    ? new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
                    : PlanValues.Dictionary(rawArguments, "steps[].args");
                result.Add(new PlanStep { Id = id, Operation = operation, Arguments = arguments });
            }
            return result;
        }

        /// <summary>
        /// 将中文操作名标准化为英文操作名�?/ Normalizes Chinese operation names to their English equivalents.
        /// </summary>
        private static string NormalizeAtomicOperation(string operation)
        {
            string value = (operation ?? string.Empty).Trim().ToLowerInvariant();
            switch (value)
            {
                case "查询文档": return "query_document";
                case "查询目录": return "query_catalog";
                case "查询元素": return "query_elements";
                case "查询几何引用":
                case "查询标注引用": return "query_references";
                case "查询参数": return "query_parameters";
                case "查询几何": return "query_geometry";
                case "查询房间": return "query_room";
                case "碰撞检�?: return "check_interferences";
                case "查询管网": return "query_mep_network";
                case "查询视图范围": return "query_view_range";
                case "查询选中":
                case "查询选择": return "query_selection";
                case "创建标高": return "create_level";
                case "创建轴网": return "create_grid";
                case "创建�?:
                case "创建墙体": return "create_wall";
                case "创建楼板": return "create_floor";
                case "创建房间": return "create_room";
                case "创建空间": return "create_space";
                case "创建模型�?: return "create_model_curve";
                case "创建通用几何": return "create_direct_shape";
                case "创建放样实体": return "create_swept_shape";
                case "创建机电管线": return "create_mep_curve";
                case "连接机电": return "connect_mep";
                case "创建机电系统": return "create_mep_system";
                case "创建保温�?: return "create_insulation";
                case "放置族实�?: return "place_family_instance";
                case "加载�?: return "load_family";
                case "创建结构构件": return "create_structural_member";
                case "创建视图": return "create_view";
                case "创建绘图视图": return "create_drafting_view";
                case "创建剖面":
                case "创建剖面视图": return "create_section_view";
                case "创建立面":
                case "创建立面视图": return "create_elevation_view";
                case "创建详图索引":
                case "创建详图": return "create_callout";
                case "复制视图": return "duplicate_view";
                case "创建视图样板": return "create_view_template";
                case "创建图纸": return "create_sheet";
                case "变换元素":
                case "移动元素": return "transform_elements";
                case "重命名元�?: return "rename_element";
                case "修改线型": return "set_element_curve";
                case "放置视图到图�?: return "place_view_on_sheet";
                case "创建详图�?: return "create_detail_curve";
                case "创建文字":
                case "创建文字注释": return "create_text_note";
                case "创建尺寸标注": return "create_dimension";
                case "创建标记": return "create_tag";
                case "创建填充区域": return "create_filled_region";
                case "创建修订": return "create_revision";
                case "创建修订云线": return "create_revision_cloud";
                case "创建明细�?: return "create_schedule";
                case "放置明细表到图纸": return "place_schedule_on_sheet";
                case "设置视图属�?: return "set_view_properties";
                case "设置图元替换": return "set_element_overrides";
                case "设置类别替换": return "set_category_overrides";
                case "管理视图过滤�?: return "manage_view_filters";
                case "设置视图范围": return "set_view_range";
                case "管理明细表字�?: return "manage_schedule_fields";
                case "管理图形资源": return "manage_graphics_resources";
                case "创建洞口": return "create_opening";
                case "设置参数": return "set_parameters";
                case "管理扩展数据": return "manage_schema_data";
                case "管理族参�?: return "manage_family_parameters";
                case "管理项目参数": return "manage_project_parameters";
                case "复制类型": return "duplicate_type";
                case "删除元素": return "delete_elements";
                case "选择元素": return "select_elements";
                case "导出": return "export";
                case "保存项目": return "save_document";
                default: return value;
            }
        }

        /// <summary>
        /// 验证步骤 ID 是否合法（字母、数字、点、下划线、连字符，最�?128 字符）�?/ Validates that a step ID contains only safe characters and is within length limit.
        /// </summary>
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

    /// <summary>
    /// 保存建模计划执行过程中的上下文状态：前置步骤结果、选中项、UI 动作等�?/ Holds contextual state during plan execution: predecessor step results, selections, and deferred UI actions.
    /// </summary>
    internal sealed class PlanExecutionContext
    {
        /// <summary>按步�?ID 存储的结果字典�?/ Step results indexed by step ID.</summary>
        private readonly Dictionary<string, Dictionary<string, object>> _results =
            new Dictionary<string, Dictionary<string, object>>(StringComparer.OrdinalIgnoreCase);
        /// <summary>延迟选中的元�?ID 列表�?/ Deferred element selection list.</summary>
        private readonly List<ElementId> _selection = new List<ElementId>();
        /// <summary>是否在选中后缩放显示元素�?/ Whether to show (zoom to) selected elements.</summary>
        private bool _showSelection;

        /// <summary>
        /// 初始化执行上下文�?/ Initializes the execution context.
        /// </summary>
        public PlanExecutionContext(UIApplication uiApplication, Document document, bool preview)
        {
            UiApplication = uiApplication;
            Document = document;
            Preview = preview;
            DeferredPreviewReferences = new List<string>();
        }

        /// <summary>当前 Revit UI 应用对象�?/ The current Revit UI application object.</summary>
        public UIApplication UiApplication { get; private set; }
        /// <summary>当前 Revit 文档�?/ The current Revit document.</summary>
        public Document Document { get; private set; }
        /// <summary>是否为预览模式（不执行写操作）�?/ Whether in preview mode (no write operations).</summary>
        public bool Preview { get; private set; }
        /// <summary>预览模式下无法解析的引用列表�?/ List of references that could not be resolved during preview.</summary>
        public List<string> DeferredPreviewReferences { get; private set; }

        /// <summary>
        /// 注册某个步骤的执行结果，供后续步骤引用�?/ Registers a step result for reference by subsequent steps.
        /// </summary>
        public void RegisterResult(string stepId, Dictionary<string, object> result)
        {
            _results[stepId] = result ?? new Dictionary<string, object>();
        }

        /// <summary>
        /// 将参数值解析为 ElementId 列表，支�?$/@ 引用前置步骤�?/ Resolves parameter values to a list of ElementIds, supporting $/@ references to prior steps.
        /// </summary>
        public IList<ElementId> ResolveElementIds(IDictionary<string, object> arguments, params string[] fieldNames)
        {
            object value = PlanValues.Get(arguments, fieldNames);
            if (value == null)
            {
                throw new BridgeCommandException("缺少元素目标参数�? + string.Join("/", fieldNames));
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
            return ids.GroupBy(id => (int)id.GetValue()).Select(group => group.First()).ToList();
        }

        /// <summary>
        /// 将参数值解析为单个 ElementId，预览模式下可返回无�?ID�?/ Resolves parameter values to a single ElementId; may return invalid ID in preview mode.
        /// </summary>
        public ElementId ResolveSingleElementId(IDictionary<string, object> arguments, params string[] fieldNames)
        {
            IList<ElementId> values = ResolveElementIds(arguments, fieldNames);
            if (values.Count == 0 && Preview)
            {
                return ElementId.InvalidElementId;
            }
            if (values.Count != 1)
            {
                throw new BridgeCommandException("参数 " + string.Join("/", fieldNames) + " 必须解析为一个元素�?);
            }
            return values[0];
        }

        /// <summary>
        /// 请求在执行完成后选中指定的元素�?/ Requests element selection after execution completes.
        /// </summary>
        public void RequestSelection(IEnumerable<ElementId> ids, bool show)
        {
            _selection.Clear();
            _selection.AddRange(ids);
            _showSelection = show;
        }

        /// <summary>
        /// 应用延迟�?UI 动作（选中元素、缩放显示）�?/ Applies deferred UI actions (element selection and zoom).
        /// </summary>
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

        /// <summary>
        /// 递归解析单个引用标记�?ElementId，支�?$/@ 嵌套引用�?/ Recursively resolves a single reference token to ElementIds, supporting nested $/@ references.
        /// </summary>
        private void AddResolvedToken(ICollection<ElementId> target, object token)
        {
            if (token == null)
            {
                throw new BridgeCommandException("元素目标不能�?null�?);
            }
            string text = token as string;
            // $ �?@ 前缀表示引用前置步骤的输�?
            // $ or @ prefix references a prior step's output
            if (text != null && (text.StartsWith("$", StringComparison.Ordinal) || text.StartsWith("@", StringComparison.Ordinal)))
            {
                string stepId = text.Substring(1);
                Dictionary<string, object> result;
                if (!_results.TryGetValue(stepId, out result))
                {
                    throw new BridgeCommandException("找不到前置步骤引用：" + text);
                }

                // 从前置步骤结果中提取元素 ID
                // Extract element IDs from the prior step result
                object resolved = PlanValues.Get(result, "element_ids", "element_id", "id");
                if (resolved == null)
                {
                    // 预览模式下暂存无法解析的引用，等执行完整后再处理
                    // In preview mode, defer unresolved references for post-execution resolution
                    if (Preview)
                    {
                        if (!DeferredPreviewReferences.Contains(text))
                        {
                            DeferredPreviewReferences.Add(text);
                        }
                        return;
                    }
                    throw new BridgeCommandException("步骤引用�? + text + "”没有返回元�?ID�?);
                }

                // 递归解析引用（支持嵌套引用列表）
                // Recursively resolve references (supports nested reference lists)
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

            // 无前缀时视为直接元�?ID（正整数�?
            // Without a prefix, treat as a literal element ID (positive integer)
            int id;
            if (!int.TryParse(Convert.ToString(token, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out id) || id <= 0)
            {
                throw new BridgeCommandException("无效元素 ID�? + token);
            }
            target.Add(new ElementId(id));
        }
    }
}
