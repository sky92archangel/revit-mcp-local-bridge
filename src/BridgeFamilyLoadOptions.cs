using Autodesk.Revit.DB;

namespace RevitCommandBridge
{
    /// <summary>
    /// load_family 使用的静默覆盖回调：族已存在时直接覆盖族与参数值，不弹对话框。
    /// Silent override callback used by load_family: when a family already exists, directly overwrites the family and parameter values without showing a dialog.
    /// </summary>
    internal sealed class BridgeFamilyLoadOptions : IFamilyLoadOptions
    {
        /// <summary>
        /// 族已存在时直接覆盖参数值并返回 true。
        /// When the family already exists, overwrites parameter values and returns true.
        /// </summary>
        public bool OnFamilyFound(bool familyInUse, out bool overwriteParameterValues)
        {
            overwriteParameterValues = true;
            return true;
        }

        /// <summary>
        /// 共享族已存在时直接覆盖参数值，并从 Family 来源加载。
        /// When the shared family already exists, overwrites parameter values and loads from the Family source.
        /// </summary>
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
