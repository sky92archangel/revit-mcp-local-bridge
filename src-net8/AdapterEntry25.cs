// ============================================================================
// Revit Command Bridge — Revit 2025 适配器入口 / Adapter Entry
// ============================================================================
// 本文件是 Revit 2025 的 IExternalApplication 入口点，
// 继承泛用基类 RevitCommandBridgeApp，仅通过 BridgeBuildInfo.SetApiYear(2025)
// 标记年份以做版本隔离。
//
// This file is the IExternalApplication entry point for Revit 2025.
// It inherits the shared base class RevitCommandBridgeApp and only calls
// BridgeBuildInfo.SetApiYear(2025) for version isolation and self‑inspection.
// ============================================================================

using Autodesk.Revit.UI;

namespace RevitCommandBridge
{
    /// <summary>
    /// Revit 2025 适配器入口。
    /// </summary>
    /// <remarks>
    /// 与 RevitCommandBridgeApp 共享 IExternalApplication 实现（src/RevitCommandBridgeApp.cs），
    /// 此处仅提供年份专属的启动/关闭辅助逻辑。
    /// Shares the IExternalApplication implementation with RevitCommandBridgeApp (src/RevitCommandBridgeApp.cs);
    /// this class only provides year‑specific startup/shutdown helpers.
    /// </remarks>
    public sealed class RevitCommandBridgeApp25 : RevitCommandBridgeApp
    {
        /// <summary>
        /// Revit 2025 启动时调用。设置 API 年份后委托基类完成初始化。
        /// Called when Revit 2025 starts. Sets the API year then delegates to the base class for initialisation.
        /// </summary>
        public override Result OnStartup(UIControlledApplication application)
        {
BridgeBuildInfo.SetApiYear(2025);
            return base.OnStartup(application);
        }

        /// <summary>
        /// Revit 2025 关闭时调用。委托基类完成清理。
        /// Called when Revit 2025 shuts down. Delegates cleanup to the base class.
        /// </summary>
        public override Result OnShutdown(UIControlledApplication application)
        {
            return base.OnShutdown(application);
        }
    }
}
