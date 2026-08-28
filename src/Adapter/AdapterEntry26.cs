using Autodesk.Revit.UI;

namespace RevitCommandBridge
{
    public sealed class RevitCommandBridgeApp26 : RevitCommandBridgeApp
    {
        public override Result OnStartup(UIControlledApplication application)
        {
            BridgeBuildInfo.SetApiYear(2026);
            return base.OnStartup(application);
        }

        public override Result OnShutdown(UIControlledApplication application)
        {
            return base.OnShutdown(application);
        }
    }
}
