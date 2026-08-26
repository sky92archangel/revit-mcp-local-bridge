// ============================================================================
// Revit Command Bridge — Revit 2027 适配器入口 / Adapter Entry (.NET 10)
// ============================================================================
// 本文件是 Revit 2027（.NET 10 运行时）的 IExternalApplication 入口点，
// 继承泛用基类 RevitCommandBridgeApp，仅通过 BridgeBuildInfo.SetApiYear(2027)
// 标记年份以做版本隔离。
//
// This file is the IExternalApplication entry point for Revit 2027 (.NET 10 runtime).
// It inherits the shared base class RevitCommandBridgeApp and only calls
// BridgeBuildInfo.SetApiYear(2027) for version isolation and self‑inspection.
// ============================================================================

using Autodesk.Revit.UI;

namespace RevitCommandBridge
{
    /// <summary>
    /// Revit 2027 适配器入口（.NET 10 运行时）。
    /// </summary>
    /// <remarks>
    /// 继承 RevitCommandBridgeApp（src/RevitCommandBridgeApp.cs），
    /// 在 OnStartup 中设定年份，用于队列隔离和版本自检。
    /// Inherits RevitCommandBridgeApp (src/RevitCommandBridgeApp.cs);
    /// sets the API year in OnStartup for queue isolation and version self‑inspection.
    /// </remarks>
    public sealed class RevitCommandBridgeApp27 : RevitCommandBridgeApp
    {
        /// <summary>
        /// Revit 2027 启动时调用。设置 API 年份后委托基类完成初始化。
        /// Called when Revit 2027 starts. Sets the API year then delegates to the base class for initialisation.
        /// </summary>
        public override Result OnStartup(UIControlledApplication application)
        {
BridgeBuildInfo.SetApiYear(2027);
            return base.OnStartup(application);
        }

        /// <summary>
        /// Revit 2027 关闭时调用。委托基类完成清理。
        /// Called when Revit 2027 shuts down. Delegates cleanup to the base class.
        /// </summary>
        public override Result OnShutdown(UIControlledApplication application)
        {
            return base.OnShutdown(application);
        }
    }
}
