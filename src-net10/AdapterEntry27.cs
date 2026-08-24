using Autodesk.Revit.UI;

namespace RevitCommandBridge
{
    /// <summary>
    /// Revit 2027 入口适配器（.NET 10 运行时）。
    /// 继承 RevitCommandBridgeApp（src/RevitCommandBridgeApp.cs），
    /// 在 OnStartup 中设定年份，用于队列隔离和版本自检。
    /// </summary>
    public sealed class RevitCommandBridgeApp27 : RevitCommandBridgeApp
    {
        public override Result OnStartup(UIControlledApplication application)
        {
BridgeBuildInfo.SetApiYear(2027);
            return base.OnStartup(application);
        }

        public override Result OnShutdown(UIControlledApplication application)
        {
            return base.OnShutdown(application);
        }
    }
}
