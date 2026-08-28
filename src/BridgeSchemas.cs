namespace RevitCommandBridge
{
    /// <summary>
    /// 桥接固定持有�?Extensible Storage Schema（manage_schema_data 的存储载体）�?
    /// GUID 一经发布不可更改，否则旧数据读不回；不开放用户自定义 Schema 结构�?
    /// Fixed Extensible Storage Schema held by the bridge (storage carrier for manage_schema_data).
    /// GUID must never change once published, or old data becomes unreadable; user-defined schema structures are not supported.
    /// </summary>
    internal static class BridgeSchemas
    {
        /// <summary>
        /// AI 元数�?Schema 的固�?GUID�?
        /// Fixed GUID for the AI metadata Schema.
        /// </summary>
        public const string AiMetadataGuid = "8F1D5B2A-6C4E-4E7A-9D3B-1A2B3C4D5E6F";

        /// <summary>
        /// 存储字段的名称�?
        /// Name of the storage field.
        /// </summary>
        public const string AiMetadataFieldName = "values";

        private static Schema _aiMetadata;

        /// <summary>
        /// 获取或惰性创�?AI 元数�?Schema（单例）�?
        /// Gets or lazily creates the AI metadata Schema (singleton).
        /// </summary>
        public static Schema AiMetadata
        {
            get
            {
                if (_aiMetadata == null)
                {
                    Guid guid = new Guid(AiMetadataGuid);
                    // 先查找是否已存在同名 Schema，避免重复注�?
                    // First check if the Schema already exists to avoid duplicate registration
                    Schema existing = Schema.Lookup(guid);
                    if (existing != null)
                    {
                        _aiMetadata = existing;
                    }
                    else
                    {
                        SchemaBuilder builder = new SchemaBuilder(guid);
                        builder.SetSchemaName("RcbAiMetadata");
                        builder.SetReadAccessLevel(AccessLevel.Public);
                        builder.SetWriteAccessLevel(AccessLevel.Public);
                        builder.SetDocumentation("Revit Command Bridge AI metadata (string map).");
                        // MapField: string→string 的键值对存储
                        // MapField: string→string key-value pair storage
                        builder.AddMapField(AiMetadataFieldName, typeof(string), typeof(string));
                        _aiMetadata = builder.Finish();
                    }
                }
                return _aiMetadata;
            }
        }

        /// <summary>
        /// 获取 Schema 中用于存储元数据的字段�?
        /// Gets the field in the Schema used for storing metadata.
        /// </summary>
        public static Field AiMetadataField
        {
            get { return AiMetadata.GetField(AiMetadataFieldName); }
        }

        /// <summary>
        /// �?Revit 元素�?Extensible Storage 中读取元数据字典�?
        /// Reads a metadata dictionary from a Revit element's Extensible Storage.
        /// </summary>
        public static Dictionary<string, string> ReadMap(Element element)
        {
            Entity entity = element.GetEntity(AiMetadata);
            if (entity == null || !entity.IsValid())
            {
                return null;
            }
            IDictionary<string, string> stored = entity.Get<IDictionary<string, string>>(AiMetadataField);
            if (stored == null)
            {
                return null;
            }
            // 返回不区分大小写的副�?
            // Return a case-insensitive copy
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, string> pair in stored)
            {
                result[pair.Key] = pair.Value;
            }
            return result;
        }

        /// <summary>
        /// �?Revit 元素�?Extensible Storage 写入元数据字典�?
        /// Writes a metadata dictionary to a Revit element's Extensible Storage.
        /// </summary>
        public static void WriteMap(Element element, Dictionary<string, string> values)
        {
            // 获取或新�?Entity
            // Retrieve or create a new Entity
            Entity entity = element.GetEntity(AiMetadata);
            if (entity == null || !entity.IsValid())
            {
                entity = new Entity(AiMetadata);
            }
            entity.Set(AiMetadataField, (IDictionary<string, string>)values);
            element.SetEntity(entity);
        }
    }
}
