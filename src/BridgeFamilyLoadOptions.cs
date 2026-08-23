using Autodesk.Revit.DB;

namespace RevitCommandBridge
{
    /// <summary>
    /// load_family 使用的静默覆盖回调：族已存在时直接覆盖族与参数值，不弹对话框。
    /// </summary>
    internal sealed class BridgeFamilyLoadOptions : IFamilyLoadOptions
    {
        public bool OnFamilyFound(bool familyInUse, out bool overwriteParameterValues)
        {
            overwriteParameterValues = true;
            return true;
        }

        public bool OnSharedFamilyFound(
            Family sharedFamily,
            bool familyInUse,
            out FamilySource source,
            out bool overwriteParameterValues)
        {
            source = FamilySource.Family;
            overwriteParameterValues = true;
            return true;
        }
    }
}
