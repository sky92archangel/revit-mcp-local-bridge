using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.DB;

namespace RevitCommandBridge
{
    /// <summary>
    /// 参数定义层（项目参数 / 共享参数 / 族参数）的类型与分组解析，
    /// 以及 manage_project_parameters 的实现。
    /// 使用 ForgeTypeId（SpecTypeId / GroupTypeId）API。
    /// </summary>
    internal static class RevitParameterAdmin
    {
        public static string NormalizeSpecToken(string token)
        {
            string value = (token ?? string.Empty).Trim().ToLowerInvariant();
            switch (value)
            {
                case "length":
                case "长度": return "length";
                case "number":
                case "integer":
                case "数值": return "number";
                case "text":
                case "string":
                case "文本": return "text";
                case "yesno":
                case "boolean":
                case "布尔": return "yesno";
                case "angle":
                case "角度": return "angle";
                case "area":
                case "面积": return "area";
                case "volume":
                case "体积": return "volume";
                default:
                    throw new BridgeCommandException(
                        "不支持的参数类型“" + token + "”。支持 length、number、text、yesno、angle、area、volume。");
            }
        }

        private static string NormalizeGroupToken(string token)
        {
            string value = (token ?? "data").Trim().ToLowerInvariant();
            switch (value)
            {
                case "geometry":
                case "几何图形": return "geometry";
                case "data":
                case "数据": return "data";
                case "general":
                case "常规": return "general";
                case "mechanical":
                case "hvac":
                case "机械": return "mechanical";
                case "electrical":
                case "电气": return "electrical";
                case "plumbing":
                case "管道": return "plumbing";
                case "text":
                case "文字": return "text";
                case "identity":
                case "identity_data":
                case "标识数据": return "identity";
                case "materials":
                case "材质": return "materials";
                case "structural":
                case "结构": return "structural";
                case "constraints":
                case "约束": return "constraints";
                case "visibility":
                case "可见性": return "visibility";
                case "phasing":
                case "阶段化": return "phasing";
                default:
                    throw new BridgeCommandException(
                        "不支持的参数分组“" + token + "”。支持 geometry、data、general、mechanical、electrical、plumbing、text、identity、materials、structural、constraints、visibility、phasing。");
            }
        }

        public static ForgeTypeId ResolveSpec(string token)
        {
            switch (NormalizeSpecToken(token))
            {
                case "length": return SpecTypeId.Length;
                case "number": return SpecTypeId.Number;
                case "text": return SpecTypeId.String.Text;
                case "yesno": return SpecTypeId.Boolean.YesNo;
                case "angle": return SpecTypeId.Angle;
                case "area": return SpecTypeId.Area;
                case "volume": return SpecTypeId.Volume;
                default: return SpecTypeId.Length;
            }
        }

        private static ForgeTypeId ResolveGroup(string token)
        {
            switch (NormalizeGroupToken(token))
            {
                case "geometry": return GroupTypeId.Geometry;
                case "data": return GroupTypeId.Data;
                case "general": return GroupTypeId.General;
                case "mechanical": return GroupTypeId.Mechanical;
                case "electrical": return GroupTypeId.Electrical;
                case "plumbing": return GroupTypeId.Plumbing;
                case "text": return GroupTypeId.Text;
                case "identity": return GroupTypeId.IdentityData;
                case "materials": return GroupTypeId.Materials;
                case "structural": return GroupTypeId.Structural;
                case "constraints": return GroupTypeId.Constraints;
                case "visibility": return GroupTypeId.Visibility;
                case "phasing": return GroupTypeId.Phasing;
                default: return GroupTypeId.Data;
            }
        }

        public static FamilyParameter AddFamilyParameter(
            FamilyManager manager,
            string name,
            string typeToken,
            string groupToken,
            bool isInstance)
        {
ForgeTypeId spec = ResolveSpec(typeToken);
ForgeTypeId group = ResolveGroup(groupToken);
            return manager.AddParameter(name, group, spec, isInstance);
        }

        public static Dictionary<string, object> ManageProjectParameters(PlanStep step, PlanExecutionContext context)
        {
            string action = PlanValues.String(step.Arguments, null, "action").Trim().ToLowerInvariant();
            switch (action)
            {
                case "list":
                    return ListProjectParameters(context);
                case "add_shared":
                case "add":
                    return AddSharedParameter(step, context);
                case "delete":
                case "remove":
                    return DeleteProjectParameter(step, context);
                default:
                    throw new BridgeCommandException("manage_project_parameters.action 仅支持 add_shared、delete、list。");
            }
        }

        private static Dictionary<string, object> ListProjectParameters(PlanExecutionContext context)
        {
            var items = new List<Dictionary<string, object>>();
            BindingMap map = context.Document.ParameterBindings;
            foreach (DictionaryEntry entry in map)
            {
                Definition definition = entry.Key as Definition;
                ElementBinding binding = entry.Value as ElementBinding;
                if (definition == null || binding == null)
                {
                    continue;
                }
                var categories = new List<string>();
                if (binding.Categories != null)
                {
                    foreach (Category category in binding.Categories)
                    {
                        categories.Add(category.Name);
                    }
                }
                items.Add(new Dictionary<string, object>
                {
                    { "name", definition.Name },
                    { "binding_type", binding is InstanceBinding ? "instance" : "type" },
                    { "categories", categories.ToArray() }
                });
            }
            return new Dictionary<string, object>
            {
                { "action", "list" },
                { "count", items.Count },
                { "parameters", items }
            };
        }

        private static Dictionary<string, object> AddSharedParameter(PlanStep step, PlanExecutionContext context)
        {
            Application application = context.UiApplication.Application;
            Document document = context.Document;
            string name = PlanValues.String(step.Arguments, null, "name", "parameter_name");
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new BridgeCommandException("manage_project_parameters 缺少 name。");
            }
            string typeToken = PlanValues.String(step.Arguments, "length", "type");
            string groupToken = PlanValues.String(step.Arguments, "data", "group", "parameter_group");
            string sharedGroupName = PlanValues.String(step.Arguments, "RCB", "shared_group", "file_group");
            bool isInstance = PlanValues.Boolean(step.Arguments, false, "instance", "is_instance");
            List<string> categoryTokens = ReadCategoryTokens(step.Arguments);

            var data = new Dictionary<string, object>
            {
                { "action", "add_shared" },
                { "name", name },
                { "type", NormalizeSpecToken(typeToken) },
                { "group", groupToken },
                { "shared_group", sharedGroupName },
                { "instance", isInstance },
                { "categories", categoryTokens.ToArray() }
            };
            NormalizeSpecToken(typeToken);
            NormalizeGroupToken(groupToken);
            if (context.Preview)
            {
                return data;
            }

            if (string.IsNullOrWhiteSpace(application.SharedParametersFilename))
            {
                throw new BridgeCommandException(
                    "项目尚未配置共享参数文件（Revit → 管理 → 共享参数）。桥接不代管该文件路径。");
            }
            DefinitionFile definitionFile = application.OpenSharedParameterFile();
            if (definitionFile == null)
            {
                throw new BridgeCommandException("无法打开共享参数文件。");
            }
            DefinitionGroup group = null;
            foreach (DefinitionGroup candidate in definitionFile.Groups)
            {
                if (string.Equals(candidate.Name, sharedGroupName, StringComparison.OrdinalIgnoreCase))
                {
                    group = candidate;
                    break;
                }
            }
            if (group == null)
            {
                group = definitionFile.Groups.Create(sharedGroupName);
            }
            ExternalDefinition definition = group.Definitions.get_Item(name) as ExternalDefinition;
            if (definition == null)
            {
ExternalDefinitionCreationOptions options =
    new ExternalDefinitionCreationOptions(name, ResolveSpec(typeToken));
definition = group.Definitions.Create(options) as ExternalDefinition;
            }
            if (definition == null)
            {
                throw new BridgeCommandException("在共享参数文件中创建定义失败：" + name);
            }

            CategorySet categorySet = application.Create.NewCategorySet();
            foreach (string token in categoryTokens)
            {
                var lookup = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                lookup["category"] = token;
                ElementId categoryId = RevitLookups.ResolveCategoryId(
                    document, lookup, BuiltInCategory.OST_GenericModel);
Category category = Category.GetCategory(document, categoryId);
                if (category == null)
                {
                    throw new BridgeCommandException("找不到类别：“" + token + "”。");
                }
                categorySet.Insert(category);
            }
            ElementBinding binding = isInstance
                ? (ElementBinding)application.Create.NewInstanceBinding(categorySet)
                : application.Create.NewTypeBinding(categorySet);
bool bound = document.ParameterBindings.Insert(definition, binding, ResolveGroup(groupToken));
            data["bound"] = bound;
            return data;
        }

        private static Dictionary<string, object> DeleteProjectParameter(PlanStep step, PlanExecutionContext context)
        {
            string name = PlanValues.String(step.Arguments, null, "name", "parameter_name");
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new BridgeCommandException("manage_project_parameters.delete 缺少 name。");
            }
            var data = new Dictionary<string, object>
            {
                { "action", "delete" },
                { "name", name }
            };
            if (context.Preview)
            {
                return data;
            }

            Definition matched = null;
            BindingMap map = context.Document.ParameterBindings;
            foreach (DictionaryEntry entry in map)
            {
                Definition definition = entry.Key as Definition;
                if (definition != null &&
                    string.Equals(definition.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    matched = definition;
                    break;
                }
            }
            if (matched == null)
            {
                throw new BridgeCommandException("当前项目没有绑定名为“" + name + "”的项目参数。");
            }
            data["removed"] = map.Remove(matched);
            return data;
        }

        private static List<string> ReadCategoryTokens(IDictionary<string, object> arguments)
        {
            object raw = PlanValues.Get(arguments, "categories", "category");
            List<object> list = PlanValues.List(raw == null ? new List<object>() : raw, "categories");
            var result = new List<string>();
            foreach (object item in list)
            {
                string text = Convert.ToString(item, CultureInfo.InvariantCulture);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    result.Add(text.Trim());
                }
            }
            if (result.Count == 0)
            {
                throw new BridgeCommandException("manage_project_parameters.add_shared 需要 categories 数组。");
            }
            return result;
        }
    }
}
