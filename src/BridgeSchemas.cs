using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;

namespace RevitCommandBridge
{
    /// <summary>
    /// 桥接固定持有的 Extensible Storage Schema（manage_schema_data 的存储载体）。
    /// GUID 一经发布不可更改，否则旧数据读不回；不开放用户自定义 Schema 结构。
    /// </summary>
    internal static class BridgeSchemas
    {
        public const string AiMetadataGuid = "8F1D5B2A-6C4E-4E7A-9D3B-1A2B3C4D5E6F";
        public const string AiMetadataFieldName = "values";

        private static Schema _aiMetadata;

        public static Schema AiMetadata
        {
            get
            {
                if (_aiMetadata == null)
                {
                    Guid guid = new Guid(AiMetadataGuid);
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
                        builder.AddMapField(AiMetadataFieldName, typeof(string), typeof(string));
                        _aiMetadata = builder.Finish();
                    }
                }
                return _aiMetadata;
            }
        }

        public static Field AiMetadataField
        {
            get { return AiMetadata.GetField(AiMetadataFieldName); }
        }

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
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, string> pair in stored)
            {
                result[pair.Key] = pair.Value;
            }
            return result;
        }

        public static void WriteMap(Element element, Dictionary<string, string> values)
        {
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
