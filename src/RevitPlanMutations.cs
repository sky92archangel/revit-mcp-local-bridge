using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;

namespace RevitCommandBridge
{
    internal static class RevitPlanMutations
    {
        public static Dictionary<string, object> SetParameters(PlanStep step, PlanExecutionContext context)
        {
            IList<ElementId> ids = context.ResolveElementIds(step.Arguments, "element_ids", "elements", "targets");
            if (ids.Count == 0 && context.Preview)
            {
                return new Dictionary<string, object> { { "deferred", true }, { "reason", "preview 中前置元素引用尚无真实 ID。" } };
            }
            Dictionary<string, object> requested = PlanValues.Dictionary(step.Arguments, "parameters", true);
            if (requested.Count == 0)
            {
                throw new BridgeCommandException("set_parameters.parameters 不能为空。");
            }
            bool ignoreMissing = PlanValues.Boolean(step.Arguments, false, "ignore_missing");
            bool ignoreReadOnly = PlanValues.Boolean(step.Arguments, false, "ignore_read_only");
            var changes = new List<Dictionary<string, object>>();

            foreach (ElementId id in ids)
            {
                Element element = context.Document.GetElement(id);
                if (element == null)
                {
                    throw new BridgeCommandException("找不到 element_id=" + id.IntegerValue + " 对应元素。");
                }
                var changed = new List<string>();
                foreach (KeyValuePair<string, object> pair in requested)
                {
                    Parameter parameter = FindParameter(element, pair.Key);
                    if (parameter == null)
                    {
                        if (ignoreMissing)
                        {
                            continue;
                        }
                        throw new BridgeCommandException("元素 " + id.IntegerValue + " 找不到参数“" + pair.Key + "”。");
                    }
                    if (parameter.IsReadOnly)
                    {
                        if (ignoreReadOnly)
                        {
                            continue;
                        }
                        throw new BridgeCommandException("元素 " + id.IntegerValue + " 的参数“" + pair.Key + "”是只读。");
                    }

                    ValidateParameterValue(parameter, pair.Value, pair.Key);
                    if (!context.Preview)
                    {
                        SetParameterValue(parameter, pair.Value, pair.Key);
                    }
                    changed.Add(pair.Key);
                }
                changes.Add(new Dictionary<string, object>
                {
                    { "element_id", id.IntegerValue },
                    { "parameters", changed.ToArray() }
                });
            }

            return new Dictionary<string, object>
            {
                { "element_ids", ids.Select(id => id.IntegerValue).ToArray() },
                { "changes", changes },
                { "preview", context.Preview }
            };
        }

        public static Dictionary<string, object> DeleteElements(PlanStep step, PlanExecutionContext context)
        {
            IList<ElementId> ids = context.ResolveElementIds(step.Arguments, "element_ids", "elements", "targets");
            if (ids.Count == 0 && context.Preview)
            {
                return new Dictionary<string, object> { { "deferred", true }, { "reason", "preview 中前置元素引用尚无真实 ID。" } };
            }
            foreach (ElementId id in ids)
            {
                if (context.Document.GetElement(id) == null)
                {
                    throw new BridgeCommandException("找不到待删除 element_id=" + id.IntegerValue + "。");
                }
            }
            var data = new Dictionary<string, object>
            {
                { "requested_element_ids", ids.Select(id => id.IntegerValue).ToArray() }
            };
            if (context.Preview)
            {
                return data;
            }
            ICollection<ElementId> deleted = context.Document.Delete(ids);
            data["deleted_element_ids"] = deleted.Select(id => id.IntegerValue).ToArray();
            return data;
        }

        public static Dictionary<string, object> SelectElements(PlanStep step, PlanExecutionContext context)
        {
            IList<ElementId> ids = context.ResolveElementIds(step.Arguments, "element_ids", "elements", "targets");
            if (ids.Count == 0 && context.Preview)
            {
                return new Dictionary<string, object> { { "deferred", true }, { "reason", "preview 中前置元素引用尚无真实 ID。" } };
            }
            foreach (ElementId id in ids)
            {
                if (context.Document.GetElement(id) == null)
                {
                    throw new BridgeCommandException("找不到待选择 element_id=" + id.IntegerValue + "。");
                }
            }
            bool show = PlanValues.Boolean(step.Arguments, true, "show", "zoom");
            if (!context.Preview)
            {
                context.RequestSelection(ids, show);
            }
            return new Dictionary<string, object>
            {
                { "element_ids", ids.Select(id => id.IntegerValue).ToArray() },
                { "show", show },
                { "preview", context.Preview }
            };
        }

        private static Parameter FindParameter(Element element, string requestedName)
        {
            const string prefix = "BIP:";
            if (requestedName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                BuiltInParameter builtIn;
                if (!Enum.TryParse(requestedName.Substring(prefix.Length).Trim(), true, out builtIn))
                {
                    throw new BridgeCommandException("无效 BuiltInParameter：" + requestedName);
                }
                return element.get_Parameter(builtIn);
            }
            return element.LookupParameter(requestedName);
        }

        private static void ValidateParameterValue(Parameter parameter, object value, string name)
        {
            switch (parameter.StorageType)
            {
                case StorageType.Double:
                    ReadDoubleValue(value, name);
                    break;
                case StorageType.Integer:
                    ReadIntegerValue(value, name);
                    break;
                case StorageType.String:
                    if (value is IDictionary<string, object>)
                    {
                        throw new BridgeCommandException("字符串参数“" + name + "”不能使用单位对象。");
                    }
                    break;
                case StorageType.ElementId:
                    ReadElementIdValue(value, name);
                    break;
                default:
                    throw new BridgeCommandException("参数“" + name + "”的 StorageType 不受支持：" + parameter.StorageType);
            }
        }

        private static void SetParameterValue(Parameter parameter, object value, string name)
        {
            switch (parameter.StorageType)
            {
                case StorageType.Double:
                    parameter.Set(ReadDoubleValue(value, name));
                    return;
                case StorageType.Integer:
                    parameter.Set(ReadIntegerValue(value, name));
                    return;
                case StorageType.String:
                    parameter.Set(Convert.ToString(value, CultureInfo.InvariantCulture));
                    return;
                case StorageType.ElementId:
                    parameter.Set(ReadElementIdValue(value, name));
                    return;
                default:
                    throw new BridgeCommandException("参数“" + name + "”的 StorageType 不受支持：" + parameter.StorageType);
            }
        }

        private static double ReadDoubleValue(object raw, string name)
        {
            IDictionary<string, object> objectValue = raw as IDictionary<string, object>;
            if (objectValue == null)
            {
                return PlanValues.ParseNumber(raw, name);
            }
            object rawValue = PlanValues.Get(objectValue, "value");
            if (rawValue == null)
            {
                throw new BridgeCommandException("参数“" + name + "”的数值对象缺少 value。");
            }
            double value = PlanValues.ParseNumber(rawValue, name + ".value");
            string unit = PlanValues.String(objectValue, "internal", "unit").ToLowerInvariant();
            switch (unit)
            {
                case "internal":
                    return value;
                case "mm":
                case "millimeter":
                case "millimeters":
                    return PlanValues.ToFeet(value);
                case "m":
                case "meter":
                case "meters":
                    return PlanValues.ToFeet(value * 1000.0);
                case "ft":
                case "feet":
                    return value;
                case "deg":
                case "degree":
                case "degrees":
                    return value * Math.PI / 180.0;
                case "rad":
                case "radian":
                case "radians":
                    return value;
                default:
                    throw new BridgeCommandException("参数“" + name + "”不支持 unit=“" + unit + "”。支持 internal、mm、m、ft、deg、rad。");
            }
        }

        private static int ReadIntegerValue(object raw, string name)
        {
            if (raw is bool)
            {
                return (bool)raw ? 1 : 0;
            }
            int value;
            if (!int.TryParse(Convert.ToString(raw, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
            {
                throw new BridgeCommandException("参数“" + name + "”必须是整数或布尔值。");
            }
            return value;
        }

        private static ElementId ReadElementIdValue(object raw, string name)
        {
            IDictionary<string, object> objectValue = raw as IDictionary<string, object>;
            if (objectValue != null)
            {
                raw = PlanValues.Get(objectValue, "element_id", "id", "value");
            }
            int id;
            if (!int.TryParse(Convert.ToString(raw, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out id))
            {
                throw new BridgeCommandException("参数“" + name + "”必须是元素 ID。");
            }
            return new ElementId(id);
        }
    }
}
