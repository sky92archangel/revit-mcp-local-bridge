using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace RevitCommandBridge
{
    /// <summary>
    /// 族操作：列出族样板、载入族、创建族。
    /// Family operations: list family templates, load family, create family.
    /// </summary>
    internal static class RevitFamilyOperations
    {
        /// <summary>默认样板列表最大返回数 / Default template list limit.</summary>
        private const int DefaultTemplateLimit = 200;
        /// <summary>样板列表最大返回数上限 / Maximum template list limit.</summary>
        private const int MaximumTemplateLimit = 1000;

        /// <summary>
        /// 列出族样板目录下的 .rft 文件。
        /// List .rft files in the family template directory.
        /// </summary>
        public static BridgeResponse ListFamilyTemplates(UIApplication uiApplication, BridgeRequest request)
        {
            string requestedRoot = BridgeArguments.GetString(request, null, "template_root", "root", "path");
            string root = ResolveTemplateRoot(uiApplication, requestedRoot);
            int limit = PlanValues.Integer(request.Arguments, DefaultTemplateLimit, "limit");
            if (limit < 1 || limit > MaximumTemplateLimit)
            {
                throw new BridgeCommandException("list_family_templates.limit 必须在 1 到 " + MaximumTemplateLimit + " 之间。");
            }

            List<string> templates = Directory.GetFiles(root, "*.rft", SearchOption.AllDirectories)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .Take(limit)
                .ToList();
            string defaultTemplate = TryResolveDefaultTemplate(root);
            var data = new Dictionary<string, object>
            {
                { "template_root", root },
                { "template_count", templates.Count },
                { "templates", templates.ToArray() },
                { "default_generic_model_template", defaultTemplate },
                { "truncated", templates.Count == limit }
            };
            return BridgeResponse.Success("completed", "读取到 " + templates.Count + " 个族样板。", data);
        }

        /// <summary>
        /// 将 .rfa 族文件载入当前项目文档。
        /// Load a .rfa family file into the current project document.
        /// </summary>
        public static BridgeResponse LoadFamily(UIApplication uiApplication, Document projectDocument, BridgeRequest request)
        {
            EnsureProjectDocument(projectDocument);
            string path = BridgeArguments.GetString(request, null, "family_path", "path", "file");
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new BridgeCommandException("load_family 缺少 family_path。");
            }
            path = NormalizeFamilyPath(path);
            if (!File.Exists(path))
            {
                throw new BridgeCommandException("族文件不存在：" + path);
            }

            bool overwrite = BridgeArguments.GetBoolean(request, false, "overwrite_parameter_values", "overwrite");
            var data = new Dictionary<string, object>
            {
                { "family_path", path },
                { "overwrite_parameter_values", overwrite }
            };
            if (request.Preview)
            {
                return BridgeResponse.Success("preview", "预览：将载入族文件。", data);
            }

            Family family;
            using (Transaction transaction = new Transaction(projectDocument, "RCB 载入族"))
            {
                if (transaction.Start() != TransactionStatus.Started)
                {
                    throw new BridgeCommandException("Revit 未能启动族载入事务。");
                }
                try
                {
                    bool loaded = projectDocument.LoadFamily(path, new RcbFamilyLoadOptions(overwrite), out family);
                    if (!loaded || family == null)
                    {
                        throw new BridgeCommandException("Revit 未能载入族文件：" + path);
                    }
                    if (transaction.Commit() != TransactionStatus.Committed)
                    {
                        throw new BridgeCommandException("Revit 未能提交族载入事务。");
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
            data["family_id"] = (int)family.Id.Value;
            data["family_name"] = family.Name;
            data["placement_type"] = family.FamilyPlacementType.ToString();
            data["symbol_ids"] = family.GetFamilySymbolIds().Select(id => (int)id.Value).ToArray();
            return BridgeResponse.Success("completed", "已载入族“" + family.Name + "”。", data);
        }

        /// <summary>
        /// 从样板创建新族：添加参数、类型、几何，保存并可选载入项目。
        /// Create a new family from template: add parameters, types, geometry, save, and optionally load into project.
        /// </summary>
        public static BridgeResponse CreateFamily(UIApplication uiApplication, Document projectDocument, BridgeRequest request)
        {
            EnsureProjectDocument(projectDocument);
            string projectPath = projectDocument.PathName;
            if (string.IsNullOrWhiteSpace(projectPath))
            {
                throw new BridgeCommandException(
                    "新建族会临时切换 Revit 文档。请先保存当前项目，再创建或载入新族。");
            }
            projectPath = Path.GetFullPath(projectPath);
            string familyName = BridgeArguments.GetString(request, null, "family_name", "name");
            if (string.IsNullOrWhiteSpace(familyName))
            {
                throw new BridgeCommandException("create_family 缺少 family_name。");
            }
            familyName = ValidateFamilyName(familyName.Trim());

            string templatePath = ResolveTemplatePath(
                uiApplication,
                BridgeArguments.GetString(request, null, "template_path", "template"));
            string savePath = ResolveSavePath(
                BridgeArguments.GetString(request, null, "save_path", "family_path", "path"),
                familyName);
            bool overwriteFile = BridgeArguments.GetBoolean(request, false, "overwrite_file", "overwrite");
            bool loadIntoProject = BridgeArguments.GetBoolean(request, true, "load_into_project", "load");
            bool overwriteLoadedParameters = BridgeArguments.GetBoolean(
                request,
                true,
                "overwrite_parameter_values",
                "overwrite_loaded_parameters");
            string category = BridgeArguments.GetString(request, null, "category", "category_id");
            List<FamilyParameterSpec> parameters = ParseParameterSpecs(request.Arguments);
            List<FamilyTypeSpec> types = ParseTypeSpecs(request.Arguments);
            List<Dictionary<string, object>> geometry = ParseOptionalDictionaryList(request.Arguments, "geometry", "solids", "primitives");
            Dictionary<string, object> place = ParseOptionalDictionary(request.Arguments, "place", "placement");

            if (geometry.Count > 500)
            {
                throw new BridgeCommandException("create_family.geometry 最多允许 500 个实体原语。");
            }
            if (place.Count > 0 && !loadIntoProject)
            {
                throw new BridgeCommandException("create_family.place 需要 load_into_project=true。");
            }
            if (File.Exists(savePath) && !overwriteFile)
            {
                throw new BridgeCommandException("目标族文件已存在。设置 overwrite_file=true 才会覆盖：" + savePath);
            }

            var plan = new Dictionary<string, object>
            {
                { "family_name", familyName },
                { "template_path", templatePath },
                { "save_path", savePath },
                { "category", category },
                { "parameter_count", parameters.Count },
                { "type_count", types.Count == 0 ? 1 : types.Count },
                { "geometry", DescribeFamilyGeometry(geometry) },
                { "load_into_project", loadIntoProject },
                { "place_requested", place.Count > 0 }
            };
            if (request.Preview)
            {
                return BridgeResponse.Success("preview", "预览：将创建、保存并按设置载入族文件。", plan);
            }

            string saveDirectory = Path.GetDirectoryName(savePath);
            if (string.IsNullOrWhiteSpace(saveDirectory))
            {
                throw new BridgeCommandException("save_path 必须包含有效目录。");
            }
            Directory.CreateDirectory(saveDirectory);

            Document familyDocument = null;
            try
            {
                familyDocument = uiApplication.Application.NewFamilyDocument(templatePath);
                if (familyDocument == null || !familyDocument.IsValidObject || !familyDocument.IsFamilyDocument)
                {
                    throw new BridgeCommandException("Revit 未能从样板创建族文档。");
                }

                List<FamilyParameterSpec> appliedParameters;
                List<FamilyTypeSpec> appliedTypes;
                int freeFormCount;
                using (Transaction familyTransaction = new Transaction(familyDocument, "RCB 创建族"))
                {
                    if (familyTransaction.Start() != TransactionStatus.Started)
                    {
                        throw new BridgeCommandException("Revit 未能启动族编辑事务。");
                    }
                    try
                    {
                        ApplyFamilyCategory(familyDocument, category);
                        appliedParameters = AddFamilyParameters(familyDocument, parameters);
                        appliedTypes = AddFamilyTypes(familyDocument, types);
                        ApplyFamilyValues(familyDocument, appliedParameters, appliedTypes);
                        freeFormCount = AddFamilyGeometry(familyDocument, geometry);
                        if (familyTransaction.Commit() != TransactionStatus.Committed)
                        {
                            throw new BridgeCommandException("Revit 未能提交族编辑事务。");
                        }
                    }
                    catch
                    {
                        if (familyTransaction.GetStatus() == TransactionStatus.Started)
                        {
                            familyTransaction.RollBack();
                        }
                        throw;
                    }
                }

                var saveOptions = new SaveAsOptions
                {
                    OverwriteExistingFile = overwriteFile
                };
                familyDocument.SaveAs(savePath, saveOptions);
                if (!familyDocument.Close(false))
                {
                    throw new BridgeCommandException("Revit 未能关闭已保存的族文档。");
                }
                familyDocument = null;

                plan["saved"] = true;
                plan["parameter_names"] = appliedParameters.Select(item => item.Name).ToArray();
                plan["type_names"] = appliedTypes.Select(item => item.Name).ToArray();
                plan["free_form_count"] = freeFormCount;

                if (loadIntoProject)
                {
                    Family loadedFamily;
                    using (Transaction projectTransaction = new Transaction(projectDocument, "RCB 载入新建族"))
                    {
                        if (projectTransaction.Start() != TransactionStatus.Started)
                        {
                            throw new BridgeCommandException("Revit 未能启动新建族载入事务。");
                        }
                        try
                        {
                            bool loaded = projectDocument.LoadFamily(
                                savePath,
                                new RcbFamilyLoadOptions(overwriteLoadedParameters),
                                out loadedFamily);
                            if (!loaded || loadedFamily == null)
                            {
                                throw new BridgeCommandException("族文件已保存，但 Revit 未能将其载入当前项目。");
                            }

                            plan["loaded"] = true;
                            plan["family_id"] = (int)loadedFamily.Id.Value;
                            plan["family_name"] = loadedFamily.Name;
                            plan["placement_type"] = loadedFamily.FamilyPlacementType.ToString();
                            plan["symbol_ids"] = loadedFamily.GetFamilySymbolIds().Select(id => (int)id.Value).ToArray();

                            if (place.Count > 0)
                            {
                                Dictionary<string, object> placementData = PlaceCreatedFamily(
                                    uiApplication,
                                    projectDocument,
                                    loadedFamily,
                                    place);
                                plan["placement"] = placementData;
                            }
                            if (projectTransaction.Commit() != TransactionStatus.Committed)
                            {
                                throw new BridgeCommandException("Revit 未能提交新建族载入事务。");
                            }
                        }
                        catch
                        {
                            if (projectTransaction.GetStatus() == TransactionStatus.Started)
                            {
                                projectTransaction.RollBack();
                            }
                            throw;
                        }
                    }
                }
                else
                {
                    plan["loaded"] = false;
                }

                return BridgeResponse.Success("completed", "已创建族“" + familyName + "”。", plan);
            }
            finally
            {
                if (familyDocument != null && familyDocument.IsValidObject)
                {
                    try
                    {
                        familyDocument.Close(false);
                    }
                    catch
                    {
                    }
                }
                RestoreProjectDocument(uiApplication, projectPath);
            }
        }

        /// <summary>
        /// 确认当前文档是有效项目文档（非族文档）。
        /// Ensure the current document is a valid project document (not a family document).
        /// </summary>
        private static void EnsureProjectDocument(Document document)
        {
            if (document == null || !document.IsValidObject)
            {
                throw new BridgeCommandException("当前 Revit 文档不可用。");
            }
            if (document.IsFamilyDocument)
            {
                throw new BridgeCommandException("当前打开的是族文档。请打开项目文档后再载入或创建项目族。");
            }
        }

        /// <summary>
        /// 恢复并激活指定路径的项目文档（创建族后切回项目）。
        /// Restore and activate the project document at the given path (switch back after family creation).
        /// </summary>
        private static void RestoreProjectDocument(UIApplication uiApplication, string projectPath)
        {
            if (uiApplication == null || string.IsNullOrWhiteSpace(projectPath) || !File.Exists(projectPath))
            {
                return;
            }
            try
            {
                UIDocument current = uiApplication.ActiveUIDocument;
                if (current != null && current.Document != null &&
                    string.Equals(current.Document.PathName, projectPath, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
                uiApplication.OpenAndActivateDocument(projectPath);
            }
            catch (Exception ex)
            {
                BridgeFileQueue.AppendLog("restore project document failed: " + ex.Message);
            }
        }

        /// <summary>
        /// 解析族样板根目录：使用请求路径或 Revit 默认路径。
        /// Resolve the family template root: use the requested path or Revit's default template path.
        /// </summary>
        private static string ResolveTemplateRoot(UIApplication uiApplication, string requestedRoot)
        {
            string root = string.IsNullOrWhiteSpace(requestedRoot)
                ? uiApplication.Application.FamilyTemplatePath
                : requestedRoot.Trim();
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                throw new BridgeCommandException("找不到族样板目录：" + root);
            }
            return Path.GetFullPath(root);
        }

        /// <summary>
        /// 解析族样板路径：显式路径或默认公制常规模型样板。
        /// Resolve the family template path: explicit path or default Metric Generic Model template.
        /// </summary>
        private static string ResolveTemplatePath(UIApplication uiApplication, string requestedTemplate)
        {
            if (!string.IsNullOrWhiteSpace(requestedTemplate))
            {
                string normalized = Path.GetFullPath(requestedTemplate.Trim());
                if (!File.Exists(normalized))
                {
                    throw new BridgeCommandException("族样板不存在：" + normalized);
                }
                if (!string.Equals(Path.GetExtension(normalized), ".rft", StringComparison.OrdinalIgnoreCase))
                {
                    throw new BridgeCommandException("template_path 必须是 .rft 族样板文件。");
                }
                return normalized;
            }

            string root = ResolveTemplateRoot(uiApplication, null);
            string resolved = TryResolveDefaultTemplate(root);
            if (string.IsNullOrWhiteSpace(resolved))
            {
                throw new BridgeCommandException(
                    "没有找到默认“公制常规模型 / Metric Generic Model”族样板。先调用 list_family_templates 查询后显式传 template_path。");
            }
            return resolved;
        }

        /// <summary>
        /// 尝试在根目录下查找默认公制常规模型族样板。
        /// Try to find the default "Metric Generic Model" family template in the root directory.
        /// </summary>
        private static string TryResolveDefaultTemplate(string root)
        {
            string[] exactPatterns =
            {
                "*公制常规模型*.rft",
                "*Metric Generic Model*.rft",
                "*Generic Model*.rft"
            };
            foreach (string pattern in exactPatterns)
            {
                string match = Directory.GetFiles(root, pattern, SearchOption.AllDirectories)
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(match))
                {
                    return match;
                }
            }
            return null;
        }

        /// <summary>
        /// 解析族保存路径：默认为 Documents/RevitCommandBridge/Families/ 目录。
        /// Resolve the family save path: defaults to Documents/RevitCommandBridge/Families/.
        /// </summary>
        private static string ResolveSavePath(string requestedPath, string familyName)
        {
            string path = requestedPath;
            if (string.IsNullOrWhiteSpace(path))
            {
                path = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                    "RevitCommandBridge",
                    "Families",
                    familyName + ".rfa");
            }
            else
            {
                path = path.Trim();
                if (string.IsNullOrWhiteSpace(Path.GetExtension(path)))
                {
                    path += ".rfa";
                }
            }
            path = Path.GetFullPath(path);
            if (!string.Equals(Path.GetExtension(path), ".rfa", StringComparison.OrdinalIgnoreCase))
            {
                throw new BridgeCommandException("save_path 必须以 .rfa 结尾。");
            }
            return path;
        }

        /// <summary>
        /// 规范化族文件路径，确保为 .rfa 后缀。
        /// Normalize the family file path and ensure it has a .rfa extension.
        /// </summary>
        private static string NormalizeFamilyPath(string path)
        {
            string normalized = Path.GetFullPath(path.Trim());
            if (!string.Equals(Path.GetExtension(normalized), ".rfa", StringComparison.OrdinalIgnoreCase))
            {
                throw new BridgeCommandException("family_path 必须是 .rfa 文件。");
            }
            return normalized;
        }

        /// <summary>
        /// 校验族名称是否合法（无非法字符、长度不超过 180）。
        /// Validate the family name (no invalid path characters, max 180 characters).
        /// </summary>
        private static string ValidateFamilyName(string name)
        {
            if (name.Length > 180 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                throw new BridgeCommandException("family_name 包含无效文件名字符，或长度超过 180。");
            }
            return name;
        }

        /// <summary>
        /// 从参数字典中解析可选的字典列表。
        /// Parse an optional list of dictionaries from the arguments.
        /// </summary>
        private static List<Dictionary<string, object>> ParseOptionalDictionaryList(
            IDictionary<string, object> values,
            params string[] names)
        {
            object raw = PlanValues.Get(values, names);
            return raw == null
                ? new List<Dictionary<string, object>>()
                : PlanValues.DictionaryList(raw, names[0]);
        }

        /// <summary>
        /// 从参数字典中解析可选的嵌套字典。
        /// Parse an optional nested dictionary from the arguments.
        /// </summary>
        private static Dictionary<string, object> ParseOptionalDictionary(
            IDictionary<string, object> values,
            params string[] names)
        {
            object raw = PlanValues.Get(values, names);
            return raw == null
                ? new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
                : PlanValues.Dictionary(raw, names[0]);
        }

        /// <summary>
        /// 描述族几何的摘要信息（原语类型列表）。
        /// Describe the family geometry summary (list of primitive kinds).
        /// </summary>
        private static Dictionary<string, object> DescribeFamilyGeometry(
            IList<Dictionary<string, object>> geometry)
        {
            var kinds = new List<string>();
            foreach (Dictionary<string, object> item in geometry)
            {
                string kind = PlanValues.String(item, null, "kind", "type");
                if (string.IsNullOrWhiteSpace(kind))
                {
                    throw new BridgeCommandException("create_family.geometry[] 缺少 kind。");
                }
                kinds.Add(kind.Trim().ToLowerInvariant());
            }
            return new Dictionary<string, object>
            {
                { "primitive_count", geometry.Count },
                { "primitive_kinds", kinds.ToArray() }
            };
        }

        /// <summary>
        /// 在族文档中设置族类别。
        /// Set the family category in the family document.
        /// </summary>
        private static void ApplyFamilyCategory(Document familyDocument, string requestedCategory)
        {
            if (string.IsNullOrWhiteSpace(requestedCategory))
            {
                return;
            }
            ElementId categoryId = RevitLookups.ResolveCategoryId(
                familyDocument,
                new Dictionary<string, object> { { "category", requestedCategory } },
                BuiltInCategory.OST_GenericModel);
            Category category = familyDocument.Settings.Categories.Cast<Category>()
                .FirstOrDefault(candidate => candidate.Id.Value == categoryId.Value);
            if (category == null)
            {
                throw new BridgeCommandException("族样板不支持类别：" + requestedCategory + "。");
            }
            Family owner = familyDocument.OwnerFamily;
            if (owner == null || !owner.IsAppropriateCategoryId(categoryId))
            {
                throw new BridgeCommandException("当前族样板不支持设置为类别：" + requestedCategory + "。");
            }
            owner.FamilyCategory = category;
        }

        /// <summary>
        /// 解析族参数规格说明列表。
        /// Parse the list of family parameter specifications.
        /// </summary>
        private static List<FamilyParameterSpec> ParseParameterSpecs(IDictionary<string, object> values)
        {
            List<Dictionary<string, object>> raw = ParseOptionalDictionaryList(values, "parameters", "family_parameters");
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var result = new List<FamilyParameterSpec>();
            foreach (Dictionary<string, object> item in raw)
            {
                string name = PlanValues.String(item, null, "name");
                if (string.IsNullOrWhiteSpace(name))
                {
                    throw new BridgeCommandException("parameters[].name 不能为空。");
                }
                if (!seen.Add(name))
                {
                    throw new BridgeCommandException("族参数名称重复：" + name);
                }
                string type = NormalizeParameterType(PlanValues.String(item, "length", "type", "parameter_type"));
                string group = NormalizeParameterGroup(PlanValues.String(item, "data", "group", "parameter_group"));
                object defaultValue = PlanValues.Get(item, "default", "value");
                string formula = PlanValues.String(item, null, "formula");
                result.Add(new FamilyParameterSpec
                {
                    Name = name,
                    Type = type,
                    Group = group,
                    IsInstance = PlanValues.Boolean(item, false, "instance", "is_instance"),
                    HasDefault = defaultValue != null,
                    DefaultValue = defaultValue,
                    Formula = formula
                });
            }
            return result;
        }

        /// <summary>
        /// 解析族类型规格列表，若未指定则创建默认类型。
        /// Parse the list of family type specifications; creates a default type if none specified.
        /// </summary>
        private static List<FamilyTypeSpec> ParseTypeSpecs(IDictionary<string, object> values)
        {
            List<Dictionary<string, object>> raw = ParseOptionalDictionaryList(values, "types", "family_types");
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var result = new List<FamilyTypeSpec>();
            foreach (Dictionary<string, object> item in raw)
            {
                string name = PlanValues.String(item, null, "name", "type_name");
                if (string.IsNullOrWhiteSpace(name))
                {
                    throw new BridgeCommandException("types[].name 不能为空。");
                }
                if (!seen.Add(name))
                {
                    throw new BridgeCommandException("族类型名称重复：" + name);
                }
                result.Add(new FamilyTypeSpec
                {
                    Name = name,
                    Values = ParseOptionalDictionary(item, "values", "parameter_values", "parameters")
                });
            }
            if (result.Count == 0)
            {
                result.Add(new FamilyTypeSpec
                {
                    Name = "默认",
                    Values = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
                });
            }
            return result;
        }

        /// <summary>
        /// 向族文档添加参数（跳过已有同名参数）。
        /// Add parameters to the family document (skip existing parameters with the same name).
        /// </summary>
        private static List<FamilyParameterSpec> AddFamilyParameters(
            Document familyDocument,
            IList<FamilyParameterSpec> requested)
        {
            FamilyManager manager = familyDocument.FamilyManager;
            var result = new List<FamilyParameterSpec>();
            foreach (FamilyParameterSpec spec in requested)
            {
                FamilyParameter existing = manager.get_Parameter(spec.Name);
                if (existing != null)
                {
                    spec.Parameter = existing;
                    result.Add(spec);
                    continue;
                }
                spec.Parameter = AddFamilyParameter(manager, spec);
                result.Add(spec);
            }
            return result;
        }

        /// <summary>
        /// 通过 Revit API 反射调用 ForgeTypeId 重载添加族参数。
        /// Use Revit API reflection to call the ForgeTypeId overload for adding a family parameter.
        /// </summary>
        private static FamilyParameter AddFamilyParameter(FamilyManager manager, FamilyParameterSpec spec)
        {
            Assembly assembly = manager.GetType().Assembly;
            Type forgeType = assembly.GetType("Autodesk.Revit.DB.ForgeTypeId", false);
            MethodInfo overload = manager.GetType().GetMethods()
                .FirstOrDefault(method =>
                {
                    if (!string.Equals(method.Name, "AddParameter", StringComparison.Ordinal))
                    {
                        return false;
                    }
                    ParameterInfo[] parameters = method.GetParameters();
                    return parameters.Length == 4 &&
                           parameters[0].ParameterType == typeof(string) &&
                           parameters[1].ParameterType == forgeType &&
                           parameters[2].ParameterType == forgeType &&
                           parameters[3].ParameterType == typeof(bool);
                });
            if (overload == null)
            {
                throw new BridgeCommandException("当前 Revit API 未找到 ForgeTypeId 族参数接口。");
            }
            object group = ResolveForgeTypeId(assembly, "GroupTypeId", ForgeGroupMember(spec.Group));
            object parameterType = ResolveForgeSpecTypeId(assembly, spec.Type);
            try
            {
                return (FamilyParameter)overload.Invoke(manager, new[] { (object)spec.Name, group, parameterType, spec.IsInstance });
            }
            catch (TargetInvocationException ex)
            {
                throw new BridgeCommandException("创建族参数“" + spec.Name + "”失败：" +
                    (ex.InnerException == null ? ex.Message : ex.InnerException.Message));
            }
        }

        /// <summary>
        /// 将简写类型名映射到 Revit SpecTypeId ForgeTypeId 实例。
        /// Map shorthand type names to Revit SpecTypeId ForgeTypeId instances.
        /// </summary>
        private static object ResolveForgeSpecTypeId(Assembly assembly, string type)
        {
            switch (type)
            {
                case "length": return ResolveForgeTypeId(assembly, "SpecTypeId", "Length");
                case "area": return ResolveForgeTypeId(assembly, "SpecTypeId", "Area");
                case "volume": return ResolveForgeTypeId(assembly, "SpecTypeId", "Volume");
                case "angle": return ResolveForgeTypeId(assembly, "SpecTypeId", "Angle");
                case "number": return ResolveForgeTypeId(assembly, "SpecTypeId", "Number");
                case "text": return ResolveForgeTypeId(assembly, "SpecTypeId+String", "Text");
                case "multiline_text": return ResolveForgeTypeId(assembly, "SpecTypeId+String", "MultilineText");
                case "yesno": return ResolveForgeTypeId(assembly, "SpecTypeId+Boolean", "YesNo");
                case "integer": return ResolveForgeTypeId(assembly, "SpecTypeId+Int", "Integer");
                default:
                    throw new BridgeCommandException("当前 Revit ForgeTypeId 路线不支持参数类型：" + type);
            }
        }

        /// <summary>
        /// 通过反射从 Revit DB 程序集解析 ForgeTypeId 静态属性/字段。
        /// Resolve a ForgeTypeId static property/field from the Revit DB assembly via reflection.
        /// </summary>
        private static object ResolveForgeTypeId(Assembly assembly, string typeName, string memberName)
        {
            Type type = assembly.GetType("Autodesk.Revit.DB." + typeName, false);
            if (type == null)
            {
                throw new BridgeCommandException("当前 Revit API 缺少类型：" + typeName);
            }
            PropertyInfo property = type.GetProperty(memberName, BindingFlags.Public | BindingFlags.Static);
            if (property != null)
            {
                return property.GetValue(null, null);
            }
            FieldInfo field = type.GetField(memberName, BindingFlags.Public | BindingFlags.Static);
            if (field != null)
            {
                return field.GetValue(null);
            }
            throw new BridgeCommandException("当前 Revit API 缺少 ForgeTypeId 成员：" + typeName + "." + memberName);
        }

        /// <summary>
        /// 将简写分组名映射到 Forge GroupTypeId 成员名。
        /// Map shorthand group names to Forge GroupTypeId member names.
        /// </summary>
        private static string ForgeGroupMember(string group)
        {
            switch (group)
            {
                case "constraints": return "Constraints";
                case "geometry": return "Geometry";
                case "identity": return "IdentityData";
                case "materials": return "Materials";
                case "text": return "Text";
                case "data":
                default: return "Data";
            }
        }

        /// <summary>
        /// 向族文档添加类型（跳过已有同名类型）。
        /// Add types to the family document (skip existing types with the same name).
        /// </summary>
        private static List<FamilyTypeSpec> AddFamilyTypes(
            Document familyDocument,
            IList<FamilyTypeSpec> requested)
        {
            FamilyManager manager = familyDocument.FamilyManager;
            var result = new List<FamilyTypeSpec>();
            foreach (FamilyTypeSpec spec in requested)
            {
                FamilyType existing = manager.Types.Cast<FamilyType>()
                    .FirstOrDefault(type => string.Equals(type.Name, spec.Name, StringComparison.OrdinalIgnoreCase));
                spec.Type = existing ?? manager.NewType(spec.Name);
                result.Add(spec);
            }
            return result;
        }

        /// <summary>
        /// 将默认值和类型特定值写入族参数。
        /// Write default values and type-specific values to family parameters.
        /// </summary>
        private static void ApplyFamilyValues(
            Document familyDocument,
            IList<FamilyParameterSpec> parameters,
            IList<FamilyTypeSpec> types)
        {
            FamilyManager manager = familyDocument.FamilyManager;
            var byName = parameters.ToDictionary(item => item.Name, StringComparer.OrdinalIgnoreCase);
            foreach (FamilyTypeSpec type in types)
            {
                manager.CurrentType = type.Type;
                foreach (FamilyParameterSpec parameter in parameters.Where(item => item.HasDefault))
                {
                    SetFamilyParameterValue(manager, parameter, parameter.DefaultValue);
                }
                foreach (KeyValuePair<string, object> pair in type.Values)
                {
                    FamilyParameterSpec parameter;
                    if (!byName.TryGetValue(pair.Key, out parameter))
                    {
                        throw new BridgeCommandException(
                            "族类型“" + type.Name + "”引用了未创建的参数：“" + pair.Key + "”。");
                    }
                    SetFamilyParameterValue(manager, parameter, pair.Value);
                }
            }
            foreach (FamilyParameterSpec parameter in parameters)
            {
                if (!string.IsNullOrWhiteSpace(parameter.Formula))
                {
                    if (!parameter.Parameter.CanAssignFormula)
                    {
                        throw new BridgeCommandException("族参数“" + parameter.Name + "”不支持公式。");
                    }
                    manager.SetFormula(parameter.Parameter, parameter.Formula);
                }
            }
        }

        /// <summary>
        /// 按参数类型设置族参数值（支持单位转换）。
        /// Set a family parameter value by type (with unit conversion support).
        /// </summary>
        private static void SetFamilyParameterValue(
            FamilyManager manager,
            FamilyParameterSpec spec,
            object value)
        {
            if (value == null)
            {
                return;
            }
            switch (spec.Type)
            {
                case "text":
                case "multiline_text":
                case "url":
                    manager.Set(spec.Parameter, Convert.ToString(value, CultureInfo.InvariantCulture));
                    return;
                case "integer":
                    manager.Set(spec.Parameter, PlanValues.Integer(
                        new Dictionary<string, object> { { "value", value } }, 0, "value"));
                    return;
                case "yesno":
                    manager.Set(spec.Parameter, ReadBooleanValue(value) ? 1 : 0);
                    return;
                case "length":
                    manager.Set(spec.Parameter, PlanValues.ToFeet(PlanValues.ParseMillimeters(value, spec.Name)));
                    return;
                case "area":
                    double areaMm = PlanValues.ParseNumber(value, spec.Name);
                    manager.Set(spec.Parameter, PlanValues.ToFeet(1.0) * PlanValues.ToFeet(1.0) * areaMm);
                    return;
                case "volume":
                    double volumeMm = PlanValues.ParseNumber(value, spec.Name);
                    manager.Set(spec.Parameter, PlanValues.ToFeet(1.0) * PlanValues.ToFeet(1.0) * PlanValues.ToFeet(1.0) * volumeMm);
                    return;
                case "angle":
                    manager.Set(spec.Parameter, PlanValues.ParseNumber(value, spec.Name) * Math.PI / 180.0);
                    return;
                case "material":
                    manager.Set(spec.Parameter, new ElementId(RevitLookups.ParsePositiveId(value, spec.Name)));
                    return;
                default:
                    manager.Set(spec.Parameter, PlanValues.ParseNumber(value, spec.Name));
                    return;
            }
        }

        /// <summary>
        /// 将对象解析为布尔值（支持 true/false 字符串和 0/1 整数）。
        /// Parse an object as a boolean (supports true/false strings and 0/1 integers).
        /// </summary>
        private static bool ReadBooleanValue(object value)
        {
            if (value is bool)
            {
                return (bool)value;
            }
            string text = Convert.ToString(value, CultureInfo.InvariantCulture);
            bool parsed;
            if (bool.TryParse(text, out parsed))
            {
                return parsed;
            }
            int integer;
            if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out integer))
            {
                return integer != 0;
            }
            throw new BridgeCommandException("布尔参数必须是 true/false 或 0/1：" + value);
        }

        /// <summary>
        /// 将几何原语列表转换为 FreeFormElement 添加到族文档。
        /// Convert geometry primitives into FreeFormElements in the family document.
        /// </summary>
        private static int AddFamilyGeometry(
            Document familyDocument,
            IList<Dictionary<string, object>> geometry)
        {
            if (geometry.Count == 0)
            {
                return 0;
            }
            var arguments = new Dictionary<string, object> { { "geometry", geometry } };
            IList<GeometryObject> objects = RevitGeometryFactory.CreateGeometry(
                arguments,
                new SolidOptions(ElementId.InvalidElementId, ElementId.InvalidElementId));
            int count = 0;
            foreach (GeometryObject item in objects)
            {
                Solid solid = item as Solid;
                if (solid == null)
                {
                    throw new BridgeCommandException("族几何仅支持实体原语。");
                }
                FreeFormElement.Create(familyDocument, solid);
                count++;
            }
            return count;
        }

        /// <summary>
        /// 将新创建的族实例放置到项目文档中。
        /// Place the newly created family instance into the project document.
        /// </summary>
        private static Dictionary<string, object> PlaceCreatedFamily(
            UIApplication uiApplication,
            Document projectDocument,
            Family family,
            IDictionary<string, object> place)
        {
            FamilySymbol symbol = ResolveLoadedFamilySymbol(projectDocument, family, place);
            var placementArguments = new Dictionary<string, object>(place, StringComparer.OrdinalIgnoreCase)
            {
                { "type_id", (int)symbol.Id.Value }
            };
            var context = new PlanExecutionContext(uiApplication, projectDocument, false);
            var step = new PlanStep
            {
                Id = "place_created_family",
                Operation = "place_family_instance",
                Arguments = placementArguments
            };
            return RevitPlanCreations.PlaceFamilyInstance(step, context);
        }

        /// <summary>
        /// 在已载入的族中解析要放置的 FamilySymbol（通过 type_id 或 type_name）。
        /// Resolve the FamilySymbol to place within the loaded family (by type_id or type_name).
        /// </summary>
        private static FamilySymbol ResolveLoadedFamilySymbol(
            Document document,
            Family family,
            IDictionary<string, object> values)
        {
            object rawTypeId = PlanValues.Get(values, "type_id", "family_type_id");
            if (rawTypeId != null)
            {
                FamilySymbol byId = document.GetElement(
                    new ElementId(RevitLookups.ParsePositiveId(rawTypeId, "place.type_id"))) as FamilySymbol;
                if (byId == null || byId.Family == null || (int)byId.Family.Id.Value != (int)family.Id.Value)
                {
                    throw new BridgeCommandException("place.type_id 不属于新创建的族“" + family.Name + "”。");
                }
                return byId;
            }

            string typeName = PlanValues.String(values, null, "type", "type_name", "family_type");
            List<FamilySymbol> symbols = family.GetFamilySymbolIds()
                .Select(id => document.GetElement(id) as FamilySymbol)
                .Where(symbol => symbol != null)
                .OrderBy(symbol => symbol.Name)
                .ToList();
            if (!string.IsNullOrWhiteSpace(typeName))
            {
                symbols = symbols.Where(symbol =>
                    string.Equals(symbol.Name, typeName, StringComparison.OrdinalIgnoreCase)).ToList();
            }
            if (symbols.Count == 0)
            {
                throw new BridgeCommandException("新创建的族没有可放置的匹配类型。");
            }
            return symbols[0];
        }

        /// <summary>
        /// 规范化参数类型名称（支持中英文别名）。
        /// Normalize parameter type names (supports Chinese and English aliases).
        /// </summary>
        private static string NormalizeParameterType(string value)
        {
            string type = (value ?? string.Empty).Trim().ToLowerInvariant()
                .Replace("-", "_")
                .Replace(" ", "_");
            switch (type)
            {
                case "length":
                case "长度": return "length";
                case "area":
                case "面积": return "area";
                case "volume":
                case "体积": return "volume";
                case "angle":
                case "角度": return "angle";
                case "number":
                case "数值": return "number";
                case "text":
                case "文本": return "text";
                case "multiline_text":
                case "多行文本": return "multiline_text";
                case "integer":
                case "整数": return "integer";
                case "yesno":
                case "yes_no":
                case "布尔":
                case "是否": return "yesno";
                case "material":
                case "材质": return "material";
                case "url": return "url";
                default:
                    throw new BridgeCommandException("不支持的族参数类型：" + value);
            }
        }

        /// <summary>
        /// 规范化参数分组名称（支持中英文别名）。
        /// Normalize parameter group names (supports Chinese and English aliases).
        /// </summary>
        private static string NormalizeParameterGroup(string value)
        {
            string group = (value ?? string.Empty).Trim().ToLowerInvariant()
                .Replace("-", "_")
                .Replace(" ", "_");
            switch (group)
            {
                case "constraints":
                case "约束": return "constraints";
                case "geometry":
                case "几何": return "geometry";
                case "identity":
                case "identity_data":
                case "标识数据": return "identity";
                case "materials":
                case "material":
                case "材质": return "materials";
                case "text":
                case "文本": return "text";
                case "data":
                case "数据":
                default: return "data";
            }
        }

        /// <summary>
        /// 族参数规格：名称、类型、分组、实例/类型、默认值、公式。
        /// Family parameter specification: name, type, group, instance/type, default value, formula.
        /// </summary>
        private sealed class FamilyParameterSpec
        {
            public string Name { get; set; }
            public string Type { get; set; }
            public string Group { get; set; }
            public bool IsInstance { get; set; }
            public bool HasDefault { get; set; }
            public object DefaultValue { get; set; }
            public string Formula { get; set; }
            public FamilyParameter Parameter { get; set; }
        }

        /// <summary>
        /// 族类型规格：名称、参数值、对应的 FamilyType 对象。
        /// Family type specification: name, parameter values, and the corresponding FamilyType object.
        /// </summary>
        private sealed class FamilyTypeSpec
        {
            public string Name { get; set; }
            public Dictionary<string, object> Values { get; set; }
            public FamilyType Type { get; set; }
        }

        /// <summary>
        /// 族载入回调：控制是否覆盖已有参数值。
        /// Family load callback: controls whether to overwrite existing parameter values.
        /// </summary>
        private sealed class RcbFamilyLoadOptions : IFamilyLoadOptions
        {
            private readonly bool _overwriteParameterValues;

            public RcbFamilyLoadOptions(bool overwriteParameterValues)
            {
                _overwriteParameterValues = overwriteParameterValues;
            }

            public bool OnFamilyFound(bool familyInUse, out bool overwriteParameterValues)
            {
                overwriteParameterValues = _overwriteParameterValues;
                return true;
            }

            public bool OnSharedFamilyFound(
                Family sharedFamily,
                bool familyInUse,
                out FamilySource source,
                out bool overwriteParameterValues)
            {
                source = FamilySource.Family;
                overwriteParameterValues = _overwriteParameterValues;
                return true;
            }
        }
    }
}
