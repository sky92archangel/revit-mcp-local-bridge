using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
#if REVIT_NET8
using System.Text.Json;
using System.Text.Json.Serialization;
#else
using System.Web.Script.Serialization;
#endif

namespace RevitCommandBridge
{
    internal sealed class BridgeRequest
    {
        public BridgeRequest()
        {
            Arguments = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            Id = Guid.NewGuid().ToString("N");
            Source = "unknown";
            CreatedUtc = DateTime.UtcNow;
        }

        public string Id { get; set; }
        public string Operation { get; set; }
        public Dictionary<string, object> Arguments { get; set; }
        public bool Preview { get; set; }
        public string DocumentTitle { get; set; }
        public string Source { get; set; }
        public DateTime CreatedUtc { get; set; }
    }

    internal sealed class BridgeResponse
    {
        private BridgeResponse(bool ok, string state, string message, Dictionary<string, object> data)
        {
            Ok = ok;
            State = state;
            Message = message;
            Data = data ?? new Dictionary<string, object>();
            CompletedUtc = DateTime.UtcNow;
        }

        public bool Ok { get; private set; }
        public string State { get; private set; }
        public string Message { get; private set; }
        public Dictionary<string, object> Data { get; private set; }
        public DateTime CompletedUtc { get; private set; }

        public static BridgeResponse Success(string state, string message, Dictionary<string, object> data)
        {
            return new BridgeResponse(true, state, message, data);
        }

        public static BridgeResponse Failure(string message, Dictionary<string, object> data)
        {
            return new BridgeResponse(false, "failed", message, data);
        }

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

        public string ToDisplayText(string requestId)
        {
            return BridgeJson.Serialize(ToDictionary(requestId));
        }
    }

    internal sealed class BridgeCommandException : Exception
    {
        public BridgeCommandException(string message)
            : base(message)
        {
        }
    }

    internal static class BridgeJson
    {
#if REVIT_NET8
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            MaxDepth = 10,
            PropertyNameCaseInsensitive = true,
            Converters = { new DictionaryConverter() }
        };

        private sealed class DictionaryConverter : JsonConverter<Dictionary<string, object>>
        {
            public override Dictionary<string, object> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            {
                return ReadDictionary(ref reader);
            }

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
                JsonSerializer.Serialize(writer, value, options);
            }
        }

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

        public static string Serialize(object value)
        {
            return JsonSerializer.Serialize(value, JsonOptions);
        }
#else
        private static readonly JavaScriptSerializer Serializer = new JavaScriptSerializer
        {
            MaxJsonLength = 1024 * 1024
        };

        public static BridgeRequest ParseRequest(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                throw new BridgeCommandException("命令请求为空。");
            }

            object parsed;
            try
            {
                parsed = Serializer.DeserializeObject(json);
            }
            catch (Exception ex)
            {
                throw new BridgeCommandException("命令 JSON 无法解析：" + ex.Message);
            }

            IDictionary<string, object> root = parsed as IDictionary<string, object>;
            if (root == null)
            {
                throw new BridgeCommandException("命令 JSON 顶层必须是对象。");
            }

            return BuildRequest(root);
        }

        public static string Serialize(object value)
        {
            return Serializer.Serialize(value);
        }
#endif

        private static BridgeRequest BuildRequest(IDictionary<string, object> root)
        {
            var request = new BridgeRequest();
            string id = ReadString(root, "id");
            if (!string.IsNullOrWhiteSpace(id))
            {
                request.Id = id.Trim();
            }

            request.Operation = ReadString(root, "operation", "command");
            if (string.IsNullOrWhiteSpace(request.Operation))
            {
                throw new BridgeCommandException("缺少 operation 字段。");
            }

            request.Preview = ReadBoolean(root, false, "preview", "dry_run");
            request.DocumentTitle = ReadString(root, "document_title", "documentTitle");
            request.Source = ReadString(root, "source") ?? "external";
            request.Arguments = ReadDictionary(root, "args", "arguments");
            return request;
        }

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

        public static string SerializeResponse(string requestId, BridgeResponse response)
        {
            return Serialize(response.ToDictionary(requestId));
        }

        public static string ReadString(IDictionary<string, object> values, params string[] names)
        {
            object value;
            if (!TryRead(values, out value, names) || value == null)
            {
                return null;
            }

            return Convert.ToString(value, CultureInfo.InvariantCulture);
        }

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

            var copy = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, object> pair in source)
            {
                copy[pair.Key] = pair.Value;
            }

            return copy;
        }

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

    internal static class BridgeArguments
    {
        public static bool Contains(BridgeRequest request, params string[] names)
        {
            object ignored;
            return TryGet(request, out ignored, names);
        }

        public static string GetString(BridgeRequest request, string defaultValue, params string[] names)
        {
            object value;
            if (!TryGet(request, out value, names) || value == null)
            {
                return defaultValue;
            }

            return Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        public static double GetMillimeters(BridgeRequest request, double defaultValue, params string[] names)
        {
            object value;
            if (!TryGet(request, out value, names) || value == null)
            {
                return defaultValue;
            }

            return ParseMillimeters(value, string.Join("/", names));
        }

        public static double RequireMillimeters(BridgeRequest request, params string[] names)
        {
            object value;
            if (!TryGet(request, out value, names) || value == null)
            {
                throw new BridgeCommandException("缺少参数：" + string.Join("/", names));
            }

            return ParseMillimeters(value, string.Join("/", names));
        }

        public static IList<double> RequireMillimeterList(BridgeRequest request, params string[] names)
        {
            object value;
            if (!TryGet(request, out value, names) || value == null)
            {
                throw new BridgeCommandException("缺少参数：" + string.Join("/", names));
            }

            string text = Convert.ToString(value, CultureInfo.InvariantCulture);
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

        private static double ParseMillimeters(object value, string name)
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

            return meters ? parsed * 1000.0 : parsed;
        }
    }
}
