using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows.Forms;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using WpfPoint = System.Windows.Point;
using WpfRect = System.Windows.Rect;
using WpfColor = System.Windows.Media.Color;
using WpfImageSource = System.Windows.Media.ImageSource;
using WpfDrawingGroup = System.Windows.Media.DrawingGroup;
using WpfDrawingContext = System.Windows.Media.DrawingContext;
using WpfSolidColorBrush = System.Windows.Media.SolidColorBrush;
using WpfPen = System.Windows.Media.Pen;
using WpfBrushes = System.Windows.Media.Brushes;
using WpfPenLineCap = System.Windows.Media.PenLineCap;
using WpfDrawingImage = System.Windows.Media.DrawingImage;
using WpfFormattedText = System.Windows.Media.FormattedText;
using WpfFlowDirection = System.Windows.FlowDirection;
using WpfTypeface = System.Windows.Media.Typeface;

namespace RevitCommandBridge
{
    public class RevitCommandBridgeApp : IExternalApplication
    {
        public virtual Result OnStartup(UIControlledApplication application)
        {
            try
            {
                BridgeFileQueue.Initialize();
                const string tabName = "命令桥";
                try
                {
                    application.CreateRibbonTab(tabName);
                }
                catch (Autodesk.Revit.Exceptions.ArgumentException)
                {
                    // Tab already exists after an update or reload.
                }
                RibbonPanel panel = application.CreateRibbonPanel(tabName, "Revit 命令桥");
                string assemblyPath = Assembly.GetExecutingAssembly().Location;
                AddButton(
                    panel,
                    "RCB_OpenPanel",
                    "命令\n面板",
                    "打开命令桥面板。桥接会自动启动；在面板中查看状态、预览和执行命令。",
                    assemblyPath,
                    typeof(OpenCommandPanelCommand),
                    RibbonIcon.Panel);
                AddButton(
                    panel,
                    "RCB_Connection",
                    "连接\n状态",
                    "查看 Revit 桥接、MCP/REST 和当前项目的连接状态。",
                    assemblyPath,
                    typeof(ShowConnectionCommand),
                    RibbonIcon.Status);
                AddButton(
                    panel,
                    "RCB_CopyMcp",
                    "复制\nMCP",
                    "复制当前 Revit 年份的通用 MCP 配置，可直接粘贴到 Codex、WorkBuddy 或其它 MCP 客户端。",
                    assemblyPath,
                    typeof(CopyMcpConfigCommand),
                    RibbonIcon.Mcp);
                AddButton(
                    panel,
                    "RCB_Help",
                    "使用\n说明",
                    "查看从连接检查到预览建模、确认执行的完整使用流程。",
                    assemblyPath,
                    typeof(ShowHelpCommand),
                    RibbonIcon.Help);
                BridgeRuntime.Start();
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                BridgeFileQueue.AppendLog("startup failed: " + ex);
                return Result.Failed;
            }
        }

        public virtual Result OnShutdown(UIControlledApplication application)
        {
            CommandPanelManager.Close();
            BridgeRuntime runtime = BridgeRuntime.Current;
            if (runtime != null)
            {
                runtime.Dispose();
            }
            else
            {
                BridgeFileQueue.PublishStatus("stopped", "Revit 已关闭，命令桥不可用。", new Dictionary<string, object>
                {
                    { "revit_api", BridgeBuildInfo.RevitVersion },
                    { "protocol", BridgeProtocol.Version }
                });
            }

            return Result.Succeeded;
        }

        private static void AddButton(
            RibbonPanel panel,
            string id,
            string text,
            string toolTip,
            string assemblyPath,
            Type commandType,
            RibbonIcon icon)
        {
            var button = new PushButtonData(id, text, assemblyPath, commandType.FullName)
            {
                ToolTip = toolTip
            };
            PushButton pushButton = panel.AddItem(button) as PushButton;
            if (pushButton != null)
            {
                WpfImageSource image = RibbonIconFactory.Create(icon);
                pushButton.LargeImage = image;
                pushButton.Image = image;
            }
        }
    }

    internal enum RibbonIcon
    {
        Panel,
        Status,
        Mcp,
        Help
    }

    internal static class RibbonIconFactory
    {
        // Images are generated in memory so the add-in has no loose icon files to lose.
        internal static WpfImageSource Create(RibbonIcon icon)
        {
            WpfColor background;
            switch (icon)
            {
                case RibbonIcon.Status:
                    background = WpfColor.FromRgb(36, 138, 91);
                    break;
                case RibbonIcon.Help:
                    background = WpfColor.FromRgb(226, 137, 38);
                    break;
                case RibbonIcon.Mcp:
                    background = WpfColor.FromRgb(0, 126, 140);
                    break;
                default:
                    background = WpfColor.FromRgb(35, 113, 178);
                    break;
            }

            var drawing = new WpfDrawingGroup();
            using (WpfDrawingContext context = drawing.Open())
            {
                context.DrawRoundedRectangle(
                    new WpfSolidColorBrush(background),
                    null,
                    new WpfRect(1, 1, 30, 30),
                    5,
                    5);

                WpfPen white = new WpfPen(WpfBrushes.White, 2.1);
                white.StartLineCap = WpfPenLineCap.Round;
                white.EndLineCap = WpfPenLineCap.Round;
                context.DrawLine(white, new WpfPoint(7, 24), new WpfPoint(25, 24));
                context.DrawLine(white, new WpfPoint(9, 8), new WpfPoint(9, 24));
                context.DrawLine(white, new WpfPoint(23, 8), new WpfPoint(23, 24));
                context.DrawLine(white, new WpfPoint(9, 12), new WpfPoint(23, 12));
                context.DrawLine(white, new WpfPoint(9, 8), new WpfPoint(16, 4));
                context.DrawLine(white, new WpfPoint(16, 4), new WpfPoint(23, 8));

                if (icon == RibbonIcon.Status)
                {
                    WpfPen tick = new WpfPen(WpfBrushes.White, 2.3);
                    tick.StartLineCap = WpfPenLineCap.Round;
                    tick.EndLineCap = WpfPenLineCap.Round;
                    context.DrawLine(tick, new WpfPoint(12, 17), new WpfPoint(15, 20));
                    context.DrawLine(tick, new WpfPoint(15, 20), new WpfPoint(21, 14));
                }
                else if (icon == RibbonIcon.Help)
                {
                    context.DrawEllipse(WpfBrushes.White, null, new WpfPoint(16, 17), 5.2, 5.2);
                    context.DrawText(
                        new WpfFormattedText("?", System.Globalization.CultureInfo.InvariantCulture, WpfFlowDirection.LeftToRight,
                            new WpfTypeface("Segoe UI"), 9.5, new WpfSolidColorBrush(background)),
                        new WpfPoint(13.25, 10.75));
                }
                else if (icon == RibbonIcon.Mcp)
                {
                    WpfPen link = new WpfPen(WpfBrushes.White, 2.2);
                    link.StartLineCap = WpfPenLineCap.Round;
                    link.EndLineCap = WpfPenLineCap.Round;
                    context.DrawEllipse(null, link, new WpfPoint(12, 18), 4.5, 4.5);
                    context.DrawEllipse(null, link, new WpfPoint(20, 12), 4.5, 4.5);
                    context.DrawLine(link, new WpfPoint(14.8, 15.2), new WpfPoint(17.2, 14.8));
                }
            }
            drawing.Freeze();
            var image = new WpfDrawingImage(drawing);
            image.Freeze();
            return image;
        }
    }

    [Transaction(TransactionMode.Manual)]
    public sealed class StartBridgeCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                BridgeRuntime.Start();
                TaskDialog.Show(
                    "Revit 命令桥",
                    "命令桥已启动。\n\n队列目录：\n" + BridgeFileQueue.RootDirectory +
                    "\n\n现在可使用命令面板、CLI、REST 网关或 MCP 客户端提交命令。");
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                return Result.Failed;
            }
        }
    }

    [Transaction(TransactionMode.Manual)]
    public sealed class OpenCommandPanelCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                BridgeRuntime.Start();
                CommandPanelManager.Show();
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                return Result.Failed;
            }
        }
    }

    [Transaction(TransactionMode.Manual)]
    public sealed class ShowConnectionCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                BridgeRuntime.Start();
                TaskDialog.Show(
                    "Revit 命令桥",
                    "✓ Revit 这一端已连接。\n\n下一步不是在这里输入命令。请回到 Codex、WorkBuddy 或已连接的 AI 对话框，发送：\n\n请通过 Revit 命令桥查询当前打开的项目和可用标高，只查询，不要修改模型。");
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                return Result.Failed;
            }
        }
    }

    [Transaction(TransactionMode.Manual)]
    public sealed class CopyMcpConfigCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                BridgeRuntime.Start();
                string version = BridgeBuildInfo.RevitVersion;
                string configPath = Path.Combine(
                    BridgeFileQueue.RootDirectory,
                    "connections",
                    "generic-mcp-revit-" + version + ".mcp.json");
                if (!File.Exists(configPath))
                {
                    TaskDialog.Show(
                        "Revit 命令桥 - MCP 配置",
                        "未找到当前版本的 MCP 配置。\n\n请重新运行安装器并完成“配置 AI 客户端”，然后再点击此按钮。\n\n预期文件：\n" + configPath);
                    return Result.Cancelled;
                }

                string config = File.ReadAllText(configPath, Encoding.UTF8);
                if (string.IsNullOrWhiteSpace(config))
                {
                    throw new InvalidOperationException("MCP 配置文件为空：" + configPath);
                }
                Clipboard.SetText(config);
                TaskDialog.Show(
                    "Revit 命令桥 - MCP 配置",
                    "✓ MCP 配置已复制到剪贴板。\n\n到 Codex、WorkBuddy 或其它 MCP 客户端的配置页面粘贴或导入即可。\n\n配置文件：\n" + configPath);
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                return Result.Failed;
            }
        }
    }

    [Transaction(TransactionMode.Manual)]
    public sealed class ShowHelpCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            TaskDialog.Show(
                "命令桥 - 使用说明",
                "只要三步：\n\n" +
                "1. 保持 Revit 和项目打开。\n\n" +
                "2. 在你的 AI 对话框发送：请通过 Revit 命令桥查询当前打开的项目和可用标高，只查询，不要修改模型。\n\n" +
                "3. 再发送你的建模要求，例如：请先预览，在标高 1，从 (0,0) 到 (6000,0) 创建一面 200mm 厚、3m 高的墙。确认预览无误后，再说：确认执行上一步。\n\n" +
                "不需要填写 JSON、路径或任何英文接口。");
            return Result.Succeeded;
        }
    }
}
