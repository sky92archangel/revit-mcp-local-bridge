using Autodesk.Revit.DB;

namespace RevitCommandBridge
{
    internal static class RevitApiExtensions
    {
#if REVIT2024_OR_GREATER
        public static long GetValue(this ElementId id) => id.GetValue();
#else
        public static long GetValue(this ElementId id) => id.IntegerValue;
#endif

        public static string GetDisplayUnitType(this Definition def)
        {
#if REVIT2022_OR_GREATER
            return def.GetDataType()?.TypeId;
#else
            return def.ParameterType.ToString();
#endif
        }

        public static Floor CreateFloor(Document doc, CurveArray profile, FloorType floorType, Level level, bool structural)
        {
#if REVIT2022_OR_GREATER
            var loop = new CurveLoop();
            foreach (Curve c in profile)
                loop.Append(c);
            return Floor.Create(doc, new[] { loop }, floorType.Id, level.Id, structural, null, 0.0);
#else
            return doc.Create.NewFloor(profile, floorType, level, structural);
#endif
        }
    }
}
