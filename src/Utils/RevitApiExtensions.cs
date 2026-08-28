using Autodesk.Revit.DB;

namespace RevitCommandBridge
{
    internal static class RevitApiExtensions
    {
#if REVIT2024_OR_GREATER
        public static long GetValue(this ElementId id) => id.Value;
#else
        public static long GetValue(this ElementId id) => id.IntegerValue;
#endif
    }
}
