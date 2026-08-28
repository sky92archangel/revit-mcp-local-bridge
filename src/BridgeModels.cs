using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RevitCommandBridge
{
    /// <summary>
    /// 桥接请求对象，表示从外部发向 Revit 的一条命令。
    /// Bridge request object representing a command sent from an external source to Revit.
    /// </summary>
    internal sealed class BridgeRequest
    {
        /// <summary>
        /// 初始化请求，自动生成 ID、设置来源和创建时间。
        /// Initializes the request with auto-generated ID, default source, and current UTC time.
        /// </summary>
        public BridgeRequest()
        {
            // 参数字典使用不区分大小写的键比较
            // Argument dictionary uses case-insensitive key comparison
            Arguments = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            Id = Guid.NewGuid().ToString("N");
            Source = "unknown";
            CreatedUtc = DateTime.UtcNow;
        }

        /// <summary>
        /// 请求唯一标识符。
        /// Unique identifier for the request.
        /// </summary>
        public string Id { get; set; }

        /// <summary>
        /// 操作名称，如 "create_wall"、"delete_element"。
        /// Operation name, e.g. "create_wall", "delete_element".
        /// </summary>
        public string Operation { get; set; }

        /// <summary>
        /// 操作的参数字典。
        /// Arguments dictionary for the operation.
        /// </summary>
        public Dictionary<string, object> Arguments { get; set; }

        /// <summary>
        /// 预览模式：为 true 时仅校验不实际执行。
        /// Preview mode: when true, validates without executing.
        /// </summary>
        public bool Preview { get; set; }

        /// <summary>
        /// 目标文档标题，为空时取活动文档。
        /// Target document title; uses the active document when empty.
        /// </summary>
        public string DocumentTitle { get; set; }

        /// <summary>
        /// 请求来源标识。
        /// Source identifier of the request.
        /// </summary>
        public string Source { get; set; }

        /// <summary>
        /// 请求创建时的 UTC 时间。
        /// UTC time when the request was created.
        /// </summary>
        public DateTime CreatedUtc { get; set; }
    }

    /// <summary>
    /// 桥接响应对象，封装命令执行的结果状态和数据。
    /// Bridge response object encapsulating the result status and data of command execution.
    /// </summary>
    internal sealed class BridgeResponse
    {
        /// <summary>
        /// 私有构造，通过静态工厂方法 Success / Failure 创建。
        /// Private constructor; instances are created via the Success / Failure factory methods.
        /// </summary>
        private BridgeResponse(bool ok, string state, string message, Dictionary<string, object> data)
        {
            Ok = ok;
            State = state;
            Message = message;
            Data = data ?? new Dictionary<string, object>();
            CompletedUtc = DateTime.UtcNow;
        }

        /// <summary>
        /// 执行是否成功。
        /// Whether the execution succeeded.
        /// </summary>
        public bool Ok { get; private set; }

        /// <summary>
        /// 状态标识（如 "running"、"busy"、"failed"）。
        /// State identifier (e.g. "running", "busy", "failed").
        /// </summary>
        public string State { get; private set; }

        /// <summary>
        /// 描述消息。
        /// Descriptive message.
        /// </summary>
        public string Message { get; private set; }

        /// <summary>
        /// 响应中附带的数据。
        /// Additional data attached to the response.
        /// </summary>
        public Dictionary<string, object> Data { get; private set; }

        /// <summary>
        /// 响应完成的 UTC 时间。
        /// UTC time when the response was completed.
        /// </summary>
        public DateTime CompletedUtc { get; private set; }

        /// <summary>
        /// 创建成功响应。
        /// Creates a success response.
        /// </summary>
        public static BridgeResponse Success(string state, string message, Dictionary<string, object> data)
        {
            return new BridgeResponse(true, state, message, data);
        }

        /// <summary>
        /// 创建失败响应，State 固定为 "failed"。
        /// Creates a failure response with State fixed to "failed".
        /// </summary>
        public static BridgeResponse Failure(string message, Dictionary<string, object> data)
        {
            return new BridgeResponse(false, "failed", message, data);
        }

        /// <summary>
        /// 将响应序列化为字典（便于 JSON 输出）。
        /// Serializes the response into a dictionary (for JSON output).
        /// </summary>
        public Dictionary<string, object> ToDictionary(string requestId)
        {
            return new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            {
                { "id", requestId },
                { "ok", Ok },
                { "state", State },
                { "message", Message },
                { "data", Data },
                { "completed_utc", CompletedUtc.ToString("o", CultureInfo.InvariantCulture) }
            };
        }

        /// <summary>
        /// 将响应转为 JSON 字符串。
        /// Converts the response to a JSON string.
        /// </summary>
        public string ToDisplayText(string requestId)
        {
            return BridgeJson.Serialize(ToDictionary(requestId));
        }
    }

    /// <summary>
    /// 桥接命令执行过程中抛出的自定义异常，用于业务逻辑错误。
    /// Custom exception thrown during bridge command execution for business logic errors.
    /// </summary>
    internal sealed class BridgeCommandException : Exception
    {
        /// <summary>
        /// 以错误消息构造异常。
        /// Constructs the exception with an error message.
        /// </summary>
        public BridgeCommandException(string message)
            : base(message)
        {
        }
    }

    /// <summary>
    /// 桥接 JSON 序列化工具，提供自定义的 Dictionary 序列化/反序列化与请求/响应转换。
    /// Bridge JSON serialization utility providing custom Dictionary serialization/deserialization and request/response conversion.
    /// </summary>
    internal static class BridgeJson
    {
        // 自定义的 JSON 序列化选项：MaxDepth=10，不区分大小写属性名，使用 DictionaryConverter
        // Custom JSON serializer options: MaxDepth=10, case-insensitive property names, with DictionaryConverter
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            MaxDepth = 10,
            PropertyNameCaseInsensitive = true,
            Converters = { new DictionaryConverter() }
        };

        /// <summary>
        /// 自定义 Dictionary&lt;string, object&gt; 转换器，支持任意嵌套的 JSON 结构。
        /// Custom converter for Dictionary&lt;string, object&gt; supporting arbitrarily nested JSON structures.
        /// </summary>
        private sealed class DictionaryConverter : JsonConverter<Dictionary<string, object>>
        {
            public override Dictionary<string, object> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            {
                return ReadDictionary(ref reader);
            }

            /// <summary>
            /// 递归读取 JSON 对象到不区分大小写的字典。
            /// Recursively reads a JSON object into a case-insensitive dictionary.
            /// </summary>
            private static Dictionary<string, object> ReadDictionary(ref Utf8JsonReader reader)
            {
                var result = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                while (reader.Read())
                {
                    if (reader.TokenType == JsonTokenType.EndObject) return result;
                    if (reader.TokenType != JsonTokenType.PropertyName) continue;
                    string key = reader.GetString();
                    reader.Read();
                    result[key] = ReadValue(ref reader);
                }
                return result;
            }

            /// <summary>
            /// 递归读取 JSON 值（支持对象、数组、布尔、数字、字符串、null）。
            /// Recursively reads a JSON value (supports object, array, boolean, number, string, null).
            /// </summary>
            private static object ReadValue(ref Utf8JsonReader reader)
            {
                switch (reader.TokenType)
                {
                    case JsonTokenType.StartObject:
                        return ReadDictionary(ref reader);
                    case JsonTokenType.StartArray:
                        var list = new List<object>();
                        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                            list.Add(ReadValue(ref reader));
                        return list;
                    case JsonTokenType.True:
                        return true;
                    case JsonTokenType.False:
                        return false;
                    case JsonTokenType.Null:
                        return null;
                    case JsonTokenType.Number:
                        // 优先按 Int64 读取，失败再按 Double
                        // Prefer Int64, fall back to Double
                        if (reader.TryGetInt64(out long l)) return l;
                        return reader.GetDouble();
                    case JsonTokenType.String:
                        return reader.GetString();
                    default:
                        return reader.GetString();
                }
            }

            public override void Write(Utf8JsonWriter writer, Dictionary<string, object> value, JsonSerializerOptions options)
            {
                writer.WriteStartObject();
                foreach (KeyValuePair<string, object> kvp in value)
                {
                    writer.WritePropertyName(kvp.Key);
                    WriteValue(writer, kvp.Value);
                }
                writer.WriteEndObject();
            }

            /// <summary>
            /// 递归写入 JSON 值，处理字典、列表、基本类型。
            /// Recursively writes a JSON value, handling dictionaries, lists, and primitive types.
            /// </summary>
            private static void WriteValue(Utf8JsonWriter writer, object value)
            {
                if (value == null)
                {
                    writer.WriteNullValue();
                    return;
                }
                if (value is Dictionary<string, object> dict)
                {
                    writer.WriteStartObject();
                    foreach (KeyValuePair<string, object> kvp in dict)
                    {
                        writer.WritePropertyName(kvp.Key);
                        WriteValue(writer, kvp.Value);
                    }
                    writer.WriteEndObject();
                    return;
                }
                // 使用非泛型 IList 接口以兼容更多集合类型
                // Use non-generic IList interface for broader collection type compatibility
                if (value is System.Collections.IList list)
                {
                    writer.WriteStartArray();
                    foreach (object item in list)
                        WriteValue(writer, item);
                    writer.WriteEndArray();
                    return;
                }
                if (value is string s) { writer.WriteStringValue(s); return; }
                if (value is bool b) { writer.WriteBooleanValue(b); return; }
                if (value is long l) { writer.WriteNumberValue(l); return; }
                if (value is double d) { writer.WriteNumberValue(d); return; }
                if (value is int i) { writer.WriteNumberValue(i); return; }
                if (value is decimal m) { writer.WriteNumberValue(m); return; }
                writer.WriteStringValue(value.ToString());
            }
        }

        /// <summary>
        /// 解析 JSON 字符串为 BridgeRequest。
        /// Parses a JSON string into a BridgeRequest.
        /// </summary>
        public static BridgeRequest ParseRequest(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                throw new BridgeCommandException("命令请求为空。");
            }

            Dictionary<string, object> root;
            try
            {
                root = JsonSerializer.Deserialize<Dictionary<string, object>>(json, JsonOptions);
            }
            catch (Exception ex)
            {
                throw new BridgeCommandException("命令 JSON 无法解析：" + ex.Message);
            }

            if (root == null)
            {
                throw new BridgeCommandException("命令 JSON 顶层必须是对象。");
            }

            return BuildRequest(root);
        }

        /// <summary>
        /// 将任意对象序列化为 JSON 字符串。
        /// Serializes any object to a JSON string.
        /// </summary>
        public static string Serialize(object value)
        {
            return JsonSerializer.Serialize(value, JsonOptions);
        }

        /// <summary>
        /// 从解析后的字典构建 BridgeRequest 实例。
        /// Builds a BridgeRequest instance from a parsed dictionary.
        /// </summary>
        private static BridgeRequest BuildRequest(IDictionary<string, object> root)
        {
            var request = new BridgeRequest();
            string id = ReadString(root, "id");
            if (!string.IsNullOrWhiteSpace(id))
            {
                request.Id = id.Trim();
            }

            // 读取 operation 字段，默认值为 "command"
            // Read the operation field, defaulting to "command"
            request.Operation = ReadString(root, "operation", "command");
            if (string.IsNullOrWhiteSpace(request.Operation))
            {
                throw new BridgeCommandException("缺少 operation 字段。");
            }

            // preview 支持 "preview" 和 "dry_run" 两个字段名
            // preview accepts both "preview" and "dry_run" field names
            request.Preview = ReadBoolean(root, false, "preview", "dry_run");
            request.DocumentTitle = ReadString(root, "document_title", "documentTitle");
            request.Source = ReadString(root, "source") ?? "external";
            // args 支持 "args" 和 "arguments" 两个字段名
            // args accepts both "args" and "arguments" field names
            request.Arguments = ReadDictionary(root, "args", "arguments");
            return request;
        }

        /// <summary>
        /// 将 BridgeRequest 序列化为 JSON 字符串。
        /// Serializes a BridgeRequest to a JSON string.
        /// </summary>
        public static string SerializeRequest(BridgeRequest request)
        {
            var payload = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            {
                { "id", request.Id },
                { "operation", request.Operation },
                { "args", request.Arguments ?? new Dictionary<string, object>() },
                { "preview", request.Preview },
                { "document_title", request.DocumentTitle },
                { "source", request.Source },
                { "created_utc", request.CreatedUtc.ToString("o", CultureInfo.InvariantCulture) }
            };
            return Serialize(payload);
        }

        /// <summary>
        /// 将 BridgeResponse 序列化为 JSON 字符串。
        /// Serializes a BridgeResponse to a JSON string.
        /// </summary>
        public static string SerializeResponse(string requestId, BridgeResponse response)
        {
            return Serialize(response.ToDictionary(requestId));
        }

        /// <summary>
        /// 从字典中按多个可能的键名读取字符串值。
        /// Reads a string value from a dictionary by trying multiple possible key names.
        /// </summary>
        public static string ReadString(IDictionary<string, object> values, params string[] names)
        {
            object value;
            if (!TryRead(values, out value, names) || value == null)
            {
                return null;
            }

            return Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// 从字典中按多个可能的键名读取布尔值，解析失败时返回默认值。
        /// Reads a boolean from a dictionary by trying multiple key names; returns default on parse failure.
        /// </summary>
        public static bool ReadBoolean(IDictionary<string, object> values, bool defaultValue, params string[] names)
        {
            object value;
            if (!TryRead(values, out value, names) || value == null)
            {
                return defaultValue;
            }

            bool parsed;
            if (bool.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), out parsed))
            {
                return parsed;
            }

            return defaultValue;
        }

        /// <summary>
        /// 从字典中按多个可能的键名读取子字典，并返回副本。
        /// Reads a sub-dictionary from a dictionary by trying multiple key names, returning a copy.
        /// </summary>
        public static Dictionary<string, object> ReadDictionary(IDictionary<string, object> values, params string[] names)
        {
            object value;
            if (!TryRead(values, out value, names) || value == null)
            {
                return new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            }

            IDictionary<string, object> source = value as IDictionary<string, object>;
            if (source == null)
            {
                throw new BridgeCommandException("args 必须是对象。");
            }

            // 创建副本避免外部修改影响内部状态
            // Create a copy to prevent external mutation from affecting internal state
            var copy = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, object> pair in source)
            {
                copy[pair.Key] = pair.Value;
            }

            return copy;
        }

        /// <summary>
        /// 尝试在字典中按多个可能的键名查找值（不区分大小写）。
        /// Attempts to find a value in a dictionary by multiple possible key names (case-insensitive).
        /// </summary>
        private static bool TryRead(IDictionary<string, object> values, out object value, params string[] names)
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

            value = null;
            return false;
        }
    }

    /// <summary>
    /// 桥接参数提取工具，从 BridgeRequest.Arguments 中按不区分大小写的键名读取各类值。
    /// Bridge argument extraction utility for reading typed values from BridgeRequest.Arguments by case-insensitive key names.
    /// </summary>
    internal static class BridgeArguments
    {
        /// <summary>
        /// 判断请求参数中是否包含指定键（任一匹配即返回 true）。
        /// Checks whether the request arguments contain any of the specified keys.
        /// </summary>
        public static bool Contains(BridgeRequest request, params string[] names)
        {
            object ignored;
            return TryGet(request, out ignored, names);
        }

        /// <summary>
        /// 读取字符串参数，不存在时返回默认值。
        /// Reads a string argument, returning the default if not found.
        /// </summary>
        public static string GetString(BridgeRequest request, string defaultValue, params string[] names)
        {
            object value;
            if (!TryGet(request, out value, names) || value == null)
            {
                return defaultValue;
            }

            return Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// 读取长度参数（以毫米为单位），支持 "m" 和 "mm" 后缀以及中文单位。
        /// Reads a length argument in millimeters, supporting "m"/"mm" suffixes and Chinese units.
        /// </summary>
        public static double GetMillimeters(BridgeRequest request, double defaultValue, params string[] names)
        {
            object value;
            if (!TryGet(request, out value, names) || value == null)
            {
                return defaultValue;
            }

            return ParseMillimeters(value, string.Join("/", names));
        }

        /// <summary>
        /// 读取长度参数（以毫米为单位），不存在时抛出异常。
        /// Reads a required length argument in millimeters, throwing if not found.
        /// </summary>
        public static double RequireMillimeters(BridgeRequest request, params string[] names)
        {
            object value;
            if (!TryGet(request, out value, names) || value == null)
            {
                throw new BridgeCommandException("缺少参数：" + string.Join("/", names));
            }

            return ParseMillimeters(value, string.Join("/", names));
        }

        /// <summary>
        /// 读取以分隔符隔开的长度列表参数（毫米），支持中文逗号/分号分隔。
        /// Reads a delimited list of length values in millimeters, supporting Chinese commas/semicolons as delimiters.
        /// </summary>
        public static IList<double> RequireMillimeterList(BridgeRequest request, params string[] names)
        {
            object value;
            if (!TryGet(request, out value, names) || value == null)
            {
                throw new BridgeCommandException("缺少参数：" + string.Join("/", names));
            }

            string text = Convert.ToString(value, CultureInfo.InvariantCulture);
            // 支持英文逗号、中文逗号、英文分号、中文分号、空格分割
            // Supports splitting by ASCII comma, Chinese comma, semicolon, Chinese semicolon, and space
            string[] chunks = text.Split(new[] { ',', '，', ';', '；', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (chunks.Length == 0)
            {
                throw new BridgeCommandException("参数 " + string.Join("/", names) + " 不能为空。");
            }

            var result = new List<double>();
            foreach (string chunk in chunks)
            {
                result.Add(ParseMillimeters(chunk, string.Join("/", names)));
            }

            return result;
        }

        /// <summary>
        /// 读取布尔参数，解析失败时返回默认值。
        /// Reads a boolean argument, returning the default if parsing fails.
        /// </summary>
        public static bool GetBoolean(BridgeRequest request, bool defaultValue, params string[] names)
        {
            object value;
            if (!TryGet(request, out value, names) || value == null)
            {
                return defaultValue;
            }

            bool parsed;
            if (bool.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), out parsed))
            {
                return parsed;
            }

            return defaultValue;
        }

        /// <summary>
        /// 在请求的参数中按多个可能的键名查找值（不区分大小写）。
        /// Looks up a value in request arguments by multiple possible key names (case-insensitive).
        /// </summary>
        private static bool TryGet(BridgeRequest request, out object value, params string[] names)
        {
            foreach (KeyValuePair<string, object> pair in request.Arguments)
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

            value = null;
            return false;
        }

        /// <summary>
        /// 将值解析为毫米长度：数字直接返回，字符串支持 "m"/"mm" 后缀和中文单位换算。
        /// Parses a value to millimeters: numeric values are returned directly, strings support "m"/"mm" suffixes and Chinese unit conversion.
        /// </summary>
        private static double ParseMillimeters(object value, string name)
        {
            // 数字类型直接转换
            // Convert numeric types directly
            if (value is byte || value is short || value is int || value is long || value is float || value is double || value is decimal)
            {
                return Convert.ToDouble(value, CultureInfo.InvariantCulture);
            }

            string text = Convert.ToString(value, CultureInfo.InvariantCulture);
            if (string.IsNullOrWhiteSpace(text))
            {
                throw new BridgeCommandException("参数 " + name + " 不能为空。");
            }

            // 将中文单位替换为英文字母单位
            // Replace Chinese unit characters with English unit letters
            text = text.Trim().Replace("毫米", "mm").Replace("米", "m");
            // 只有 "m" 后缀而没有 "mm" 后缀时才视为米
            // Only treat as meters when the suffix is "m" but not "mm"
            bool meters = text.EndsWith("mm", StringComparison.OrdinalIgnoreCase) == false &&
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

            // 米转毫米
            // Convert meters to millimeters
            return meters ? parsed * 1000.0 : parsed;
        }
    }
}
