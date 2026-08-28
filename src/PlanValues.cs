using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using Autodesk.Revit.DB;

namespace RevitCommandBridge
{
    /// <summary>
    /// 表示建模计划中的一个步骤。 / Represents a single step within a modeling plan.
    /// </summary>
    internal sealed class PlanStep
    {
        /// <summary>步骤唯一标识。 / Unique step identifier.</summary>
        public string Id { get; set; }
        /// <summary>原子操作名称。 / Atomic operation name.</summary>
        public string Operation { get; set; }
        /// <summary>该步骤的参数。 / Arguments for this step.</summary>
        public Dictionary<string, object> Arguments { get; set; }
    }

    /// <summary>
    /// 提供从字典中安全读取各类参数值的工具方法。 / Provides utility methods for safely reading typed parameter values from dictionaries.
    /// </summary>
    internal static class PlanValues
    {
        /// <summary>毫米转英尺的换算常数。 / Conversion constant: millimeters to feet.</summary>
        private const double FeetPerMillimeter = 1.0 / 304.8;

        /// <summary>
        /// 从字典中按多个候选键名查找并返回原始值。 / Looks up a raw value from a dictionary by multiple candidate key names.
        /// </summary>
        public static object Get(IDictionary<string, object> values, params string[] names)
        {
            object value;
            if (TryGet(values, out value, names))
            {
                return value;
            }
            return null;
        }

        /// <summary>
        /// 尝试按多个候选键名从字典中查找值。 / Attempts to find a value in a dictionary by multiple candidate key names.
        /// </summary>
        public static bool TryGet(IDictionary<string, object> values, out object value, params string[] names)
        {
            if (values != null)
            {
                foreach (KeyValuePair<string, object> pair in values)
                {
                    foreach (string name in names)
                    {
                        if (string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase))
                        {
                            value = pair.Value;
                            return true;
                        }
                    }
                }
            }

            value = null;
            return false;
        }

        /// <summary>
        /// 从字典中读取字符串参数，取不到时返回默认值。 / Reads a string parameter from the dictionary, returning the default if not found.
        /// </summary>
        public static string String(IDictionary<string, object> values, string defaultValue, params string[] names)
        {
            object value = Get(values, names);
            if (value == null)
            {
                return defaultValue;
            }
            string text = Convert.ToString(value, CultureInfo.InvariantCulture);
            return string.IsNullOrWhiteSpace(text) ? defaultValue : text.Trim();
        }

        /// <summary>
        /// 从字典中读取布尔参数，取不到时返回默认值。 / Reads a boolean parameter from the dictionary, returning the default if not found.
        /// </summary>
        public static bool Boolean(IDictionary<string, object> values, bool defaultValue, params string[] names)
        {
            object value = Get(values, names);
            if (value == null)
            {
                return defaultValue;
            }
            if (value is bool)
            {
                return (bool)value;
            }

            bool parsed;
            if (bool.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), out parsed))
            {
                return parsed;
            }
            throw new BridgeCommandException("参数 " + string.Join("/", names) + " 必须是 true 或 false。");
        }

        /// <summary>
        /// 从字典中读取整数参数，取不到时返回默认值。 / Reads an integer parameter from the dictionary, returning the default if not found.
        /// </summary>
        public static int Integer(IDictionary<string, object> values, int defaultValue, params string[] names)
        {
            object value = Get(values, names);
            if (value == null)
            {
                return defaultValue;
            }

            int parsed;
            if (int.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed))
            {
                return parsed;
            }
            throw new BridgeCommandException("参数 " + string.Join("/", names) + " 必须是整数。");
        }

        /// <summary>
        /// 从字典中读取浮点数参数，取不到时返回默认值。 / Reads a double parameter from the dictionary, returning the default if not found.
        /// </summary>
        public static double Number(IDictionary<string, object> values, double defaultValue, params string[] names)
        {
            object value = Get(values, names);
            if (value == null)
            {
                return defaultValue;
            }
            return ParseNumber(value, string.Join("/", names));
        }

        /// <summary>
        /// 从字典中读取毫米长度参数（可选），取不到时返回默认值。 / Reads an optional millimeter length parameter, returning the default if not found.
        /// </summary>
        public static double Millimeters(IDictionary<string, object> values, double defaultValue, params string[] names)
        {
            object value = Get(values, names);
            if (value == null)
            {
                return defaultValue;
            }
            return ParseMillimeters(value, string.Join("/", names));
        }

        /// <summary>
        /// 从字典中读取必填的毫米长度参数，缺失则抛异常。 / Reads a required millimeter length parameter, throwing if missing.
        /// </summary>
        public static double RequireMillimeters(IDictionary<string, object> values, params string[] names)
        {
            object value = Get(values, names);
            if (value == null)
            {
                throw new BridgeCommandException("缺少参数：" + string.Join("/", names));
            }
            return ParseMillimeters(value, string.Join("/", names));
        }

        /// <summary>
        /// 将对象值强制转换为字典（不区分大小写）。 / Casts a raw value into a case-insensitive dictionary.
        /// </summary>
        public static Dictionary<string, object> Dictionary(object value, string fieldName)
        {
            IDictionary<string, object> source = value as IDictionary<string, object>;
            if (source == null)
            {
                throw new BridgeCommandException("参数 " + fieldName + " 必须是对象。");
            }

            var result = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, object> pair in source)
            {
                result[pair.Key] = pair.Value;
            }
            return result;
        }

        /// <summary>
        /// 从字典中读取指定字段并转为字典，可控制是否必填。 / Reads a dictionary-typed field, with optional requirement enforcement.
        /// </summary>
        public static Dictionary<string, object> Dictionary(IDictionary<string, object> values, string fieldName, bool required)
        {
            object value = Get(values, fieldName);
            if (value == null)
            {
                if (required)
                {
                    throw new BridgeCommandException("缺少参数：" + fieldName);
                }
                return new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            }
            return Dictionary(value, fieldName);
        }

        /// <summary>
        /// 将对象值强制转换为 List&lt;object&gt;。 / Casts a raw value into a List&lt;object&gt;.
        /// </summary>
        public static List<object> List(object value, string fieldName)
        {
            if (value == null)
            {
                throw new BridgeCommandException("缺少参数：" + fieldName);
            }
            if (value is string || value is IDictionary<string, object>)
            {
                throw new BridgeCommandException("参数 " + fieldName + " 必须是数组。");
            }

            IEnumerable source = value as IEnumerable;
            if (source == null)
            {
                throw new BridgeCommandException("参数 " + fieldName + " 必须是数组。");
            }

            var result = new List<object>();
            foreach (object item in source)
            {
                result.Add(item);
            }
            return result;
        }

        /// <summary>
        /// 将对象值强制转换为字典列表。 / Casts a raw value into a list of dictionaries.
        /// </summary>
        public static List<Dictionary<string, object>> DictionaryList(object value, string fieldName)
        {
            var result = new List<Dictionary<string, object>>();
            foreach (object item in List(value, fieldName))
            {
                result.Add(Dictionary(item, fieldName + "[]"));
            }
            return result;
        }

        /// <summary>
        /// 从字典中读取 Revit XYZ 点（x/y/z 单位 mm）。 / Reads a Revit XYZ point from the dictionary (x/y/z in mm).
        /// </summary>
        public static XYZ Point(IDictionary<string, object> values, string fieldName)
        {
            Dictionary<string, object> point = Dictionary(Get(values, fieldName), fieldName);
            double x = RequireMillimeters(point, "x", "x_mm");
            double y = RequireMillimeters(point, "y", "y_mm");
            double z = Millimeters(point, 0.0, "z", "z_mm");
            return new XYZ(ToFeet(x), ToFeet(y), ToFeet(z));
        }

        /// <summary>
        /// 将 Revit XYZ 点转回毫米单位的字典。 / Converts a Revit XYZ point back to a millimeter-unit dictionary.
        /// </summary>
        public static Dictionary<string, object> PointData(XYZ point)
        {
            return new Dictionary<string, object>
            {
                { "x", ToMillimeters(point.X) },
                { "y", ToMillimeters(point.Y) },
                { "z", ToMillimeters(point.Z) }
            };
        }

        /// <summary>
        /// 毫米转英尺（Revit 内部单位）。 / Converts millimeters to feet (Revit internal unit).
        /// </summary>
        public static double ToFeet(double millimeters)
        {
            return millimeters * FeetPerMillimeter;
        }

        /// <summary>
        /// 角度转弧度。 / Converts degrees to radians.
        /// </summary>
        public static double ToRadians(double degrees)
        {
            return degrees * Math.PI / 180.0;
        }

        /// <summary>
        /// 英尺转毫米，四舍五入到 3 位小数。 / Converts feet to millimeters, rounded to 3 decimal places.
        /// </summary>
        public static double ToMillimeters(double feet)
        {
            return Math.Round(feet / FeetPerMillimeter, 3, MidpointRounding.AwayFromZero);
        }

        /// <summary>
        /// 将值解析为 double，支持数字类型和字符串。 / Parses a value as double, supporting both numeric types and strings.
        /// </summary>
        public static double ParseNumber(object value, string name)
        {
            double parsed;
            if (value is byte || value is short || value is int || value is long || value is float || value is double || value is decimal)
            {
                return Convert.ToDouble(value, CultureInfo.InvariantCulture);
            }
            if (double.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out parsed))
            {
                return parsed;
            }
            throw new BridgeCommandException("参数 " + name + " 不是有效数字：" + value);
        }

        /// <summary>
        /// 将值解析为毫米长度，支持 "mm" 和 "m" 后缀以及中文字段。 / Parses a value as millimeter length, supporting "mm" and "m" suffixes and Chinese units.
        /// </summary>
        public static double ParseMillimeters(object value, string name)
        {
            if (value is byte || value is short || value is int || value is long || value is float || value is double || value is decimal)
            {
                return Convert.ToDouble(value, CultureInfo.InvariantCulture);
            }

            string text = Convert.ToString(value, CultureInfo.InvariantCulture);
            if (string.IsNullOrWhiteSpace(text))
            {
                throw new BridgeCommandException("参数 " + name + " 不能为空。");
            }

            // 替换中文单位为英文字符，便于统一解析
            // Replace Chinese unit characters with English equivalents for unified parsing
            text = text.Trim().Replace("毫米", "mm").Replace("米", "m");
            // 判断单位是否为"m"（非"mm"结尾）
            // Detect whether the unit is "m" (not ending with "mm")
            bool meters = !text.EndsWith("mm", StringComparison.OrdinalIgnoreCase) &&
                          text.EndsWith("m", StringComparison.OrdinalIgnoreCase);
            if (text.EndsWith("mm", StringComparison.OrdinalIgnoreCase))
            {
                text = text.Substring(0, text.Length - 2);
            }
            else if (meters)
            {
                text = text.Substring(0, text.Length - 1);
            }

            double parsed;
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed))
            {
                throw new BridgeCommandException("参数 " + name + " 不是有效长度：" + value);
            }
            // 如果单位是米则转换为毫米，否则直接返回毫米值
            // Convert meters to millimeters; otherwise return the millimeter value directly
            return meters ? parsed * 1000.0 : parsed;
        }
    }
}
