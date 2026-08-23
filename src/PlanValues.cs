using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using Autodesk.Revit.DB;

namespace RevitCommandBridge
{
    internal sealed class PlanStep
    {
        public string Id { get; set; }
        public string Operation { get; set; }
        public Dictionary<string, object> Arguments { get; set; }
    }

    internal static class PlanValues
    {
        private const double FeetPerMillimeter = 1.0 / 304.8;

        public static object Get(IDictionary<string, object> values, params string[] names)
        {
            object value;
            if (TryGet(values, out value, names))
            {
                return value;
            }
            return null;
        }

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

        public static double Number(IDictionary<string, object> values, double defaultValue, params string[] names)
        {
            object value = Get(values, names);
            if (value == null)
            {
                return defaultValue;
            }
            return ParseNumber(value, string.Join("/", names));
        }

        public static double Millimeters(IDictionary<string, object> values, double defaultValue, params string[] names)
        {
            object value = Get(values, names);
            if (value == null)
            {
                return defaultValue;
            }
            return ParseMillimeters(value, string.Join("/", names));
        }

        public static double RequireMillimeters(IDictionary<string, object> values, params string[] names)
        {
            object value = Get(values, names);
            if (value == null)
            {
                throw new BridgeCommandException("缺少参数：" + string.Join("/", names));
            }
            return ParseMillimeters(value, string.Join("/", names));
        }

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

        public static List<Dictionary<string, object>> DictionaryList(object value, string fieldName)
        {
            var result = new List<Dictionary<string, object>>();
            foreach (object item in List(value, fieldName))
            {
                result.Add(Dictionary(item, fieldName + "[]"));
            }
            return result;
        }

        public static XYZ Point(IDictionary<string, object> values, string fieldName)
        {
            Dictionary<string, object> point = Dictionary(Get(values, fieldName), fieldName);
            double x = RequireMillimeters(point, "x", "x_mm");
            double y = RequireMillimeters(point, "y", "y_mm");
            double z = Millimeters(point, 0.0, "z", "z_mm");
            return new XYZ(ToFeet(x), ToFeet(y), ToFeet(z));
        }

        public static Dictionary<string, object> PointData(XYZ point)
        {
            return new Dictionary<string, object>
            {
                { "x", ToMillimeters(point.X) },
                { "y", ToMillimeters(point.Y) },
                { "z", ToMillimeters(point.Z) }
            };
        }

        public static double ToFeet(double millimeters)
        {
            return millimeters * FeetPerMillimeter;
        }

        public static double ToRadians(double degrees)
        {
            return degrees * Math.PI / 180.0;
        }

        public static double ToMillimeters(double feet)
        {
            return Math.Round(feet / FeetPerMillimeter, 3, MidpointRounding.AwayFromZero);
        }

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

            text = text.Trim().Replace("毫米", "mm").Replace("米", "m");
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
            return meters ? parsed * 1000.0 : parsed;
        }
    }
}
