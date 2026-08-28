using Autodesk.Revit.UI;

namespace RevitCommandBridge
{
    public sealed class RevitCommandBridgeApp23 : RevitCommandBridgeApp
    {
        public override Result OnStartup(UIControlledApplication application)
        {
            BridgeBuildInfo.SetApiYear(2023);
            return base.OnStartup(application);
        }

        public override Result OnShutdown(UIControlledApplication application)
        {
            return base.OnShutdown(application);
        }
    }
}
