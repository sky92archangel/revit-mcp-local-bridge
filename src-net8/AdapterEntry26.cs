using Autodesk.Revit.UI;

namespace RevitCommandBridge
{
    /// <summary>
    /// Revit 2026 入口适配器。
    /// 与 RevitCommandBridgeApp 共享 IExternalApplication 实现（src/RevitCommandBridgeApp.cs），
    /// 此处仅提供年份专属的启动/关闭辅助逻辑（如有需要）。
    /// </summary>
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
