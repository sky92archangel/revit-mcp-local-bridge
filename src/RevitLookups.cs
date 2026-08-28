using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;

namespace RevitCommandBridge
{
    /// <summary>
    /// 提供 Revit 元素查找、解析和元数据提取的静态工具方法。 / Provides static utility methods for Revit element lookup, resolution, and metadata extraction.
    /// </summary>
    internal static class RevitLookups
    {
        /// <summary>
        /// 按 level_id、level 名称或默认（第一个）解析标高。 / Resolves a level by level_id, level name, or defaults to the first level.
        /// </summary>
        public static Level ResolveLevel(Document document, IDictionary<string, object> arguments)
        {
            object idValue = PlanValues.Get(arguments, "level_id");
            if (idValue != null)
            {
                int id = ParsePositiveId(idValue, "level_id");
                Level byId = document.GetElement(new ElementId(id)) as Level;
                if (byId == null)
                {
                    throw new BridgeCommandException("找不到 level_id=" + id + " 对应的标高。");
                }
                return byId;
            }

            List<Level> levels = new FilteredElementCollector(document)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .OrderBy(level => level.Elevation)
                .ToList();
            if (levels.Count == 0)
            {
                throw new BridgeCommandException("当前项目没有标高，请先创建标高。");
            }

            string requested = PlanValues.String(arguments, null, "level", "level_name");
            if (string.IsNullOrWhiteSpace(requested))
            {
                return levels[0];
            }
            Level match = levels.FirstOrDefault(level =>
                string.Equals(level.Name, requested, StringComparison.OrdinalIgnoreCase));
            if (match == null)
            {
                throw new BridgeCommandException("找不到标高“" + requested + "”。");
            }
            return match;
        }

        /// <summary>
        /// 按 type_id、family/type 名称解析元素类型，支持默认值。 / Resolves an ElementType by type_id or family/type name, with optional default fallback.
        /// </summary>
        public static ElementType ResolveElementType(
            Document document,
            Type expectedType,
            IDictionary<string, object> arguments,
            bool allowDefault)
        {
            object idValue = PlanValues.Get(arguments, "type_id", "family_type_id");
            if (idValue != null)
            {
                int id = ParsePositiveId(idValue, "type_id");
                ElementType byId = document.GetElement(new ElementId(id)) as ElementType;
                if (byId == null || !expectedType.IsAssignableFrom(byId.GetType()))
                {
                    throw new BridgeCommandException("type_id=" + id + " 不是可用的 " + expectedType.Name + "。");
                }
                return byId;
            }

            string typeName = PlanValues.String(arguments, null, "type", "type_name", "family_type");
            string familyName = PlanValues.String(arguments, null, "family", "family_name");
            List<ElementType> candidates = new FilteredElementCollector(document)
                .OfClass(expectedType)
                .WhereElementIsElementType()
                .Cast<ElementType>()
                .OrderBy(ElementName)
                .ToList();
            if (!string.IsNullOrWhiteSpace(typeName))
            {
                candidates = candidates.Where(candidate =>
                    string.Equals(ElementName(candidate), typeName, StringComparison.OrdinalIgnoreCase)).ToList();
            }
            if (!string.IsNullOrWhiteSpace(familyName))
            {
                candidates = candidates.Where(candidate =>
                    string.Equals(FamilyName(candidate), familyName, StringComparison.OrdinalIgnoreCase)).ToList();
            }

            if (candidates.Count == 0)
            {
                throw new BridgeCommandException(
                    "找不到匹配的 " + expectedType.Name + "。先用 query_catalog(kind=types) 查询现有类型。");
            }
            if (!allowDefault && string.IsNullOrWhiteSpace(typeName) && string.IsNullOrWhiteSpace(familyName))
            {
                throw new BridgeCommandException("必须提供 type_id，或 family/type 名称。");
            }
            if (candidates.Count > 1 && !allowDefault)
            {
                throw new BridgeCommandException("族与类型条件匹配到多个结果，请提供 type_id 精确指定。");
            }
            return candidates[0];
        }

        /// <summary>
        /// 按参数解析 FamilySymbol。 / Resolves a FamilySymbol from arguments.
        /// </summary>
        public static FamilySymbol ResolveFamilySymbol(Document document, IDictionary<string, object> arguments)
        {
            return (FamilySymbol)ResolveElementType(document, typeof(FamilySymbol), arguments, false);
        }

        /// <summary>
        /// 按 BuiltInCategory 枚举、数字 ID 或类别名称解析类别。 / Resolves a category by BuiltInCategory enum, numeric ID, or category name.
        /// </summary>
        public static ElementId ResolveCategoryId(
            Document document,
            IDictionary<string, object> arguments,
            BuiltInCategory defaultCategory)
        {
            object raw = PlanValues.Get(arguments, "category", "category_id");
            if (raw == null)
            {
                return new ElementId(defaultCategory);
            }

            int numeric;
            string text = Convert.ToString(raw, CultureInfo.InvariantCulture).Trim();
            if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out numeric))
            {
                return new ElementId(numeric);
            }

            BuiltInCategory builtIn;
            if (Enum.TryParse(text, true, out builtIn))
            {
                return new ElementId(builtIn);
            }

            foreach (Category category in document.Settings.Categories)
            {
                if (string.Equals(category.Name, text, StringComparison.OrdinalIgnoreCase))
                {
                    return category.Id;
                }
            }
            throw new BridgeCommandException("找不到类别“" + text + "”。可传 OST_*、类别 ID 或当前 Revit 的类别名称。");
        }

        /// <summary>
        /// 按名称查找材质，未找到或名称为空时返回 null。 / Finds a material by name; returns null if not found or name is empty.
        /// </summary>
        public static Material FindMaterial(Document document, string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return null;
            }
            return new FilteredElementCollector(document)
                .OfClass(typeof(Material))
                .Cast<Material>()
                .FirstOrDefault(material => string.Equals(material.Name, name, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// 安全获取元素名称，异常时返回空字符串。 / Safely gets an element's name, returning an empty string on failure.
        /// </summary>
        public static string ElementName(Element element)
        {
            if (element == null)
            {
                return string.Empty;
            }
            try
            {
                return element.Name ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        /// <summary>
        /// 安全获取元素族名称。 / Safely gets an element's family name.
        /// </summary>
        public static string FamilyName(Element element)
        {
            FamilySymbol symbol = element as FamilySymbol;
            if (symbol != null && symbol.Family != null)
            {
                return symbol.Family.Name;
            }
            ElementType type = element as ElementType;
            if (type != null)
            {
                Parameter familyParameter = type.get_Parameter(BuiltInParameter.SYMBOL_FAMILY_NAME_PARAM);
                if (familyParameter != null)
                {
                    return familyParameter.AsString() ?? familyParameter.AsValueString() ?? string.Empty;
                }
            }
            return string.Empty;
        }

        /// <summary>
        /// 提取元素的基本数据：ID、名称、类别、位置、边界框和指定参数。 / Extracts basic element data: ID, name, category, location, bounding box, and specified parameters.
        /// </summary>
        public static Dictionary<string, object> ElementData(Document document, Element element, IEnumerable<string> parameterNames)
        {
            var data = new Dictionary<string, object>
            {
                { "id", (int)element.Id.GetValue() },
                { "unique_id", element.UniqueId },
                { "name", ElementName(element) },
                { "class", element.GetType().FullName },
                { "category", element.Category == null ? null : element.Category.Name },
                { "category_id", element.Category == null ? (object)null : (int)element.Category.Id.GetValue() },
                { "is_type", element is ElementType }
            };

            ElementId typeId = element.GetTypeId();
            if (typeId != null && typeId != ElementId.InvalidElementId)
            {
                Element type = document.GetElement(typeId);
                data["type_id"] = (int)typeId.GetValue();
                data["type_name"] = ElementName(type);
                data["family_name"] = FamilyName(type);
            }
            else if (element is ElementType)
            {
                data["family_name"] = FamilyName(element);
            }

            LocationPoint point = element.Location as LocationPoint;
            LocationCurve curve = element.Location as LocationCurve;
            if (point != null)
            {
                data["location"] = PlanValues.PointData(point.Point);
            }
            else if (curve != null)
            {
                data["curve_start"] = PlanValues.PointData(curve.Curve.GetEndPoint(0));
                data["curve_end"] = PlanValues.PointData(curve.Curve.GetEndPoint(1));
            }

            BoundingBoxXYZ box = element.get_BoundingBox(null);
            if (box != null)
            {
                data["bounding_box"] = new Dictionary<string, object>
                {
                    { "min", PlanValues.PointData(box.Min) },
                    { "max", PlanValues.PointData(box.Max) }
                };
            }

            if (parameterNames != null)
            {
                var parameters = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                foreach (string name in parameterNames)
                {
                    Parameter parameter = element.LookupParameter(name);
                    parameters[name] = parameter == null ? null : ParameterData(parameter);
                }
                data["parameters"] = parameters;
            }
            return data;
        }

        /// <summary>
        /// 提取参数的数据：存储类型、只读标志、显示值和内部值。 / Extracts parameter data: storage type, read-only flag, display value, and internal value.
        /// </summary>
        public static Dictionary<string, object> ParameterData(Parameter parameter)
        {
            var data = new Dictionary<string, object>
            {
                { "storage_type", parameter.StorageType.ToString() },
                { "read_only", parameter.IsReadOnly },
                { "display", parameter.AsValueString() }
            };
            switch (parameter.StorageType)
            {
                case StorageType.Double:
                    data["internal_value"] = parameter.AsDouble();
data["display_unit_type"] = parameter.Definition.GetDisplayUnitType();
                    break;
                case StorageType.Integer:
                    data["value"] = parameter.AsInteger();
                    break;
                case StorageType.String:
                    data["value"] = parameter.AsString();
                    break;
                case StorageType.ElementId:
                    data["element_id"] = (int)parameter.AsElementId().GetValue();
                    break;
            }
            return data;
        }

        /// <summary>
        /// 将值解析为正整数元素 ID。 / Parses a value as a positive integer element ID.
        /// </summary>
        public static int ParsePositiveId(object value, string fieldName)
        {
            int parsed;
            if (!int.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed) || parsed <= 0)
            {
                throw new BridgeCommandException("参数 " + fieldName + " 必须是正整数元素 ID。");
            }
            return parsed;
        }
    }
}
