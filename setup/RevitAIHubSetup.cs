using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

namespace RevitAIHubSetup
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            CleanupOldInstallerCopies();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new SetupForm());
        }

        // The released installer keeps one stable name. When that file is launched,
        // old test builds next to it are no longer useful and are removed quietly.
        private static void CleanupOldInstallerCopies()
        {
            try
            {
                string current = Assembly.GetExecutingAssembly().Location;
                if (!string.Equals(Path.GetFileName(current), "RevitCommandBridgeSetup.exe", StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                string directory = Path.GetDirectoryName(current);
                if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
                {
                    return;
                }

                foreach (string oldName in new[]
                {
                    "RevitAIHubSetup.exe", "RevitAIHubSetup.pdb",
                    "RevitCommandBridgeSetup-new.exe", "RevitCommandBridgeSetup-new.pdb",
                    "RevitCommandBridgeSetup-v2.exe", "RevitCommandBridgeSetup-v2.pdb",
                    "RevitCommandBridgeSetup-v3.exe", "RevitCommandBridgeSetup-v3.pdb",
                    "RevitCommandBridgeSetup.pdb"
                })
                {
                    string oldPath = Path.Combine(directory, oldName);
                    if (File.Exists(oldPath))
                    {
                        File.Delete(oldPath);
                    }
                }
            }
            catch
            {
                // Cleanup must never prevent installation from starting.
            }
        }
    }

    internal sealed class SetupForm : Form
    {
        private readonly ComboBox _packageSelector;
        private readonly ComboBox _connectorSelector;
        private readonly Label _connectorValueLabel;
        private readonly Label _connectorHint;
        private readonly Label _apiSettingsLabel;
        private readonly TableLayoutPanel _apiSettings;
        private readonly TextBox _apiBaseUrl;
        private readonly TextBox _apiModel;
        private readonly TextBox _apiKey;
        private readonly TextBox _revitDirectory;
        private readonly Label _environment;
        private readonly Label _statusSummary;
        private readonly TextBox _output;
        private readonly Button _detailsButton;
        private readonly Button _previewButton;
        private readonly Button _installButton;
        private readonly Button _copyMcpButton;
        private readonly Button _uninstallButton;
        private readonly Button _closeRevitButton;
        private readonly Button _browseRevitButton;
        private readonly Button _refreshButton;
        private readonly ProgressBar _progress;
        private readonly List<PackageInfo> _packages;
        private readonly Dictionary<string, PackageInfo> _bundledPackages;
        private string _payloadDirectory;
        private bool _installed;
        private string _installedVersion;
        private string _installationResultSummary;
        private string _lastPowerShellError;
        private string _latestAdapterFailure;
        private string _lastAdapterStatusSignature;
        private DateTime _stageStartedUtc;
        private string _stageName;
        private int _stageProgress;

        public SetupForm()
        {
            _packages = new List<PackageInfo>();
            _bundledPackages = new Dictionary<string, PackageInfo>(StringComparer.OrdinalIgnoreCase);
            Text = "Revit 命令桥安装";
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(760, 650);
            MaximumSize = new Size(1100, 900);
            Size = new Size(900, 720);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            Font = new Font("Microsoft YaHei UI", 9.0f);
            BackColor = Color.FromArgb(245, 247, 250);

            var header = new Panel
            {
                Dock = DockStyle.Top,
                Height = 68,
                BackColor = Color.FromArgb(30, 88, 148),
                Padding = new Padding(22, 13, 22, 10)
            };
            var productTitle = new Label
            {
                AutoSize = false,
                Dock = DockStyle.Top,
                Height = 28,
                Font = new Font("Microsoft YaHei UI", 14.0f, FontStyle.Bold),
                ForeColor = Color.White,
                Text = "Revit 命令桥"
            };
            var productSubtitle = new Label
            {
                AutoSize = false,
                Dock = DockStyle.Fill,
                Font = new Font("Microsoft YaHei UI", 9.0f),
                ForeColor = Color.FromArgb(222, 235, 249),
                Text = "支持 Revit 2025–2027 · 自动识别本机版本并生成适配插件"
            };
            header.Controls.Add(productSubtitle);
            header.Controls.Add(productTitle);

            _packageSelector = new ComboBox
            {
                Dock = DockStyle.Fill,
                DropDownStyle = ComboBoxStyle.DropDownList
            };
            _packageSelector.SelectedIndexChanged += delegate { RefreshEnvironment(); };

            _connectorSelector = new ComboBox
            {
                Dock = DockStyle.Fill,
                DropDownStyle = ComboBoxStyle.DropDownList
            };
            AddConnector("auto", "自动识别并配置本机 AI 客户端（推荐）");
            _connectorValueLabel = new Label
            {
                AutoSize = false,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Color.FromArgb(39, 57, 76),
                Text = "自动识别并配置本机 AI 客户端"
            };
            _connectorHint = new Label
            {
                AutoSize = true,
                Dock = DockStyle.Fill,
                ForeColor = Color.DimGray,
                Margin = new Padding(0, 2, 0, 6)
            };
            _connectorSelector.SelectedIndexChanged += delegate { RefreshConnectorHint(); };

            _apiBaseUrl = new TextBox { Dock = DockStyle.Fill, Text = "https://api.example.com/v1" };
            _apiModel = new TextBox { Dock = DockStyle.Fill, Text = "your-model-name" };
            _apiKey = new TextBox { Dock = DockStyle.Fill, UseSystemPasswordChar = true };
            _apiSettings = new TableLayoutPanel
            {
                AutoSize = true,
                Dock = DockStyle.Top,
                ColumnCount = 2,
                Visible = false,
                Margin = new Padding(0)
            };
            _apiSettings.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 126));
            _apiSettings.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            _apiSettings.Controls.Add(FieldLabel("Base URL"), 0, 0);
            _apiSettings.Controls.Add(_apiBaseUrl, 1, 0);
            _apiSettings.Controls.Add(FieldLabel("模型名称"), 0, 1);
            _apiSettings.Controls.Add(_apiModel, 1, 1);
            _apiSettings.Controls.Add(FieldLabel("API Key"), 0, 2);
            _apiSettings.Controls.Add(_apiKey, 1, 2);
            _apiSettingsLabel = FieldLabel("模型 API");
            _apiSettingsLabel.Visible = false;
            _connectorSelector.SelectedIndex = 0;
            _connectorSelector.Enabled = false;

            _revitDirectory = new TextBox { Dock = DockStyle.Fill };
            _revitDirectory.TextChanged += delegate { RefreshEnvironment(); };
            var browse = new Button { Text = "选择 Revit 目录", AutoSize = true };
            browse.Click += BrowseForRevit;

            var fields = new TableLayoutPanel
            {
                AutoSize = true,
                Dock = DockStyle.Top,
                ColumnCount = 3,
                Padding = new Padding(16, 6, 16, 6)
            };
            fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 122));
            fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            fields.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            fields.Controls.Add(_apiSettingsLabel, 0, 0);
            fields.Controls.Add(_apiSettings, 1, 0);
            fields.SetColumnSpan(_apiSettings, 2);
            fields.Controls.Add(FieldLabel("Revit 安装目录"), 0, 1);
            fields.Controls.Add(_revitDirectory, 1, 1);
            fields.Controls.Add(browse, 2, 1);
            fields.Visible = false;

            var choiceCard = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                Height = 100,
                ColumnCount = 2,
                Padding = new Padding(22, 10, 22, 8),
                BackColor = Color.White
            };
            choiceCard.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 86));
            choiceCard.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            choiceCard.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            choiceCard.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            choiceCard.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            var revitLabel = FieldLabel("Revit 版本");
            revitLabel.Margin = new Padding(0, 7, 8, 0);
            var connectorLabel = FieldLabel("AI 连接");
            connectorLabel.Margin = new Padding(0, 7, 8, 0);
            choiceCard.Controls.Add(revitLabel, 0, 0);
            choiceCard.Controls.Add(_packageSelector, 1, 0);
            choiceCard.Controls.Add(connectorLabel, 0, 1);
            choiceCard.Controls.Add(_connectorValueLabel, 1, 1);
            _connectorHint.Margin = new Padding(88, 0, 0, 0);
            _connectorHint.AutoEllipsis = true;
            choiceCard.Controls.Add(_connectorHint, 0, 2);
            choiceCard.SetColumnSpan(_connectorHint, 2);

            _environment = new Label
            {
                AutoSize = false,
                Dock = DockStyle.Fill,
                Padding = new Padding(14, 2, 14, 8),
                ForeColor = Color.FromArgb(39, 57, 76),
                Text = "正在检查本机 Revit…"
            };
            var environmentCard = new Panel
            {
                Dock = DockStyle.Top,
                Height = 56,
                BackColor = Color.White,
                Padding = new Padding(8, 5, 8, 5)
            };
            var environmentCaption = new Label
            {
                AutoSize = false,
                Dock = DockStyle.Top,
                Height = 18,
                Padding = new Padding(14, 2, 14, 0),
                ForeColor = Color.FromArgb(90, 108, 128),
                Text = "检测结果"
            };
            environmentCard.Controls.Add(_environment);
            environmentCard.Controls.Add(environmentCaption);

            _previewButton = new Button
            {
                Text = "预览安装（不写入）",
                AutoSize = true,
                Margin = new Padding(8)
            };
            _previewButton.Click += delegate { RunInstaller(true); };
            _previewButton.Visible = false;
            _installButton = new Button
            {
                Text = "安装到 Revit",
                Font = new Font("Microsoft YaHei UI", 10.0f, FontStyle.Bold),
                AutoSize = true,
                Margin = new Padding(8),
                Padding = new Padding(18, 6, 18, 6),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(30, 101, 178),
                ForeColor = Color.White
            };
            _installButton.FlatAppearance.BorderSize = 0;
            _installButton.Click += delegate
            {
                if (_installed)
                {
                    Close();
                    return;
                }
                RunInstaller(false);
            };
            _copyMcpButton = new Button
            {
                Text = "复制 MCP 配置",
                AutoSize = true,
                Margin = new Padding(8),
                Visible = false
            };
            _copyMcpButton.Click += CopyMcpConfiguration;
            _uninstallButton = new Button
            {
                Text = "卸载命令桥",
                AutoSize = true,
                Margin = new Padding(8)
            };
            _uninstallButton.Click += RunUninstaller;
            _closeRevitButton = new Button
            {
                Text = "关闭 Revit",
                AutoSize = true,
                Margin = new Padding(8),
                Visible = false
            };
            _closeRevitButton.Click += CloseRevitGracefully;
            _browseRevitButton = new Button
            {
                Text = "选择 Revit 图标或应用",
                AutoSize = true,
                Margin = new Padding(8),
                Visible = false
            };
            _browseRevitButton.Click += BrowseForRevitApplication;
            _refreshButton = new Button
            {
                Text = "重新检查",
                AutoSize = true,
                Margin = new Padding(8)
            };
            _refreshButton.Click += delegate { RefreshEnvironment(); };
            _progress = new ProgressBar
            {
                Style = ProgressBarStyle.Continuous,
                Minimum = 0,
                Maximum = 100,
                Value = 0,
                Width = 220,
                Height = 22,
                Visible = false,
                Margin = new Padding(8, 10, 8, 8)
            };
            var more = new Button
            {
                Text = "更多选项  ▾",
                AutoSize = true,
                Margin = new Padding(8),
                Padding = new Padding(11, 4, 11, 4),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.White,
                ForeColor = Color.FromArgb(55, 76, 99)
            };
            more.FlatAppearance.BorderColor = Color.FromArgb(204, 214, 225);
            var moreMenu = new ContextMenuStrip();
            moreMenu.Items.Add("重新检查", null, delegate { RefreshEnvironment(); });
            moreMenu.Items.Add("使用说明", null, OpenGuide);
            moreMenu.Items.Add(new ToolStripSeparator());
            moreMenu.Items.Add("重新安装", null, delegate { RunInstaller(false); });
            moreMenu.Items.Add("卸载命令桥", null, RunUninstaller);
            moreMenu.Items.Add("复制 MCP 配置", null, CopyMcpConfiguration);
            moreMenu.Items.Add("打开 MCP 配置目录", null, OpenMcpConfigurationFolder);
            moreMenu.Items.Add(new ToolStripSeparator());
            moreMenu.Items.Add("高级设置…", null, delegate
            {
                ShowAdvancedOptions(fields);
            });
            more.Click += delegate { moreMenu.Show(more, new Point(0, more.Height)); };
            var actions = new FlowLayoutPanel
            {
                Height = 56,
                Dock = DockStyle.Top,
                Padding = new Padding(14, 8, 14, 6),
                FlowDirection = FlowDirection.LeftToRight
            };
            actions.Controls.Add(_installButton);
            actions.Controls.Add(_copyMcpButton);
            actions.Controls.Add(_closeRevitButton);
            actions.Controls.Add(_browseRevitButton);
            actions.Controls.Add(more);
            actions.Controls.Add(_progress);

            _statusSummary = new Label
            {
                Dock = DockStyle.Fill,
                BorderStyle = BorderStyle.None,
                Padding = new Padding(16, 8, 16, 4),
                AutoSize = false,
                Text = "正在检测本机的 Revit…",
                ForeColor = Color.DarkSlateGray
            };

            _output = new TextBox
            {
                Dock = DockStyle.Fill,
                Font = new Font("Consolas", 9.0f),
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Both,
                WordWrap = false,
                Text = "详细日志：\r\n仅在排查问题时需要查看。",
                Visible = false
            };

            _detailsButton = new Button
            {
                Text = "查看日志",
                AutoSize = true,
                Margin = new Padding(4),
                FlatStyle = FlatStyle.Flat,
                ForeColor = Color.FromArgb(76, 101, 128),
                BackColor = Color.White
            };
            _detailsButton.FlatAppearance.BorderSize = 0;
            _detailsButton.Click += delegate
            {
                _output.Visible = !_output.Visible;
                _detailsButton.Text = _output.Visible ? "隐藏详细日志" : "查看详细日志";
            };

            var statusPanel = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.White,
                Margin = new Padding(14, 0, 14, 14),
                Padding = new Padding(14, 8, 14, 6)
            };
            var nextStepCaption = new Label
            {
                AutoSize = false,
                Dock = DockStyle.Top,
                Height = 21,
                ForeColor = Color.FromArgb(90, 108, 128),
                Text = "下一步"
            };
            statusPanel.Controls.Add(_output);
            statusPanel.Controls.Add(_statusSummary);
            var statusActions = new FlowLayoutPanel
            {
                AutoSize = true,
                Dock = DockStyle.Bottom,
                FlowDirection = FlowDirection.LeftToRight
            };
            statusActions.Controls.Add(_detailsButton);
            statusPanel.Controls.Add(statusActions);
            statusPanel.Controls.Add(nextStepCaption);

            Controls.Add(statusPanel);
            Controls.Add(actions);
            Controls.Add(environmentCard);
            Controls.Add(choiceCard);
            Controls.Add(header);
            Shown += delegate { LoadPayload(); };
            FormClosed += delegate { CleanupPayload(); };
        }

        private static Label FieldLabel(string text)
        {
            return new Label
            {
                AutoSize = true,
                Text = text,
                TextAlign = ContentAlignment.MiddleLeft,
                Margin = new Padding(0, 8, 8, 8)
            };
        }

        private void ShowAdvancedOptions(TableLayoutPanel fields)
        {
            using (var dialog = new Form
            {
                Text = "高级设置",
                StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MinimizeBox = false,
                MaximizeBox = false,
                ClientSize = new Size(640, 310),
                Font = Font,
                BackColor = Color.White
            })
            {
                var heading = new Label
                {
                    AutoSize = false,
                    Dock = DockStyle.Top,
                    Height = 48,
                    Padding = new Padding(18, 12, 18, 6),
                    Font = new Font("Microsoft YaHei UI", 11.0f, FontStyle.Bold),
                    Text = "高级设置"
                };
                var footer = new FlowLayoutPanel
                {
                    Dock = DockStyle.Bottom,
                    Height = 52,
                    Padding = new Padding(10, 6, 10, 6),
                    FlowDirection = FlowDirection.RightToLeft
                };
                var close = new Button
                {
                    Text = "完成",
                    AutoSize = true,
                    Padding = new Padding(14, 4, 14, 4)
                };
                close.Click += delegate { dialog.Close(); };
                footer.Controls.Add(close);
                footer.Controls.Add(_previewButton);

                fields.Visible = true;
                fields.Dock = DockStyle.Fill;
                dialog.Controls.Add(fields);
                dialog.Controls.Add(footer);
                dialog.Controls.Add(heading);
                dialog.FormClosing += delegate
                {
                    footer.Controls.Remove(_previewButton);
                    dialog.Controls.Remove(fields);
                    fields.Visible = false;
                    _previewButton.Visible = false;
                };
                _previewButton.Visible = true;
                dialog.ShowDialog(this);
            }
        }

        private void AddConnector(string value, string display)
        {
            _connectorSelector.Items.Add(new ConnectorInfo(value, display));
        }

        private void LoadPayload()
        {
            try
            {
                _payloadDirectory = Path.Combine(Path.GetTempPath(), "RevitAIHubSetup", Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(_payloadDirectory);
                string zipPath = Path.Combine(_payloadDirectory, "payload.zip");
                Assembly assembly = Assembly.GetExecutingAssembly();
                using (Stream resource = assembly.GetManifestResourceStream("RevitAIHub.payload.zip"))
                {
                    if (resource == null)
                    {
                        throw new InvalidOperationException("安装包资源缺失。请重新下载 Revit AI Hub Setup。 ");
                    }

                    using (FileStream destination = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        CopyStream(resource, destination);
                    }
                }

                ZipFile.ExtractToDirectory(zipPath, _payloadDirectory);
                File.Delete(zipPath);
                foreach (string directory in Directory.GetDirectories(_payloadDirectory, "RevitCommandBridge-*"))
                {
                    PackageInfo package = PackageInfo.Read(directory);
                    if (package != null)
                    {
                        _bundledPackages[package.RevitVersion] = package;
                    }
                }

                if (_bundledPackages.Count == 0)
                {
                    throw new InvalidOperationException("安装包中没有有效的 Revit 适配包。 ");
                }

Dictionary<string, string> detectedInstallations = DetectAllRevitInstallations();
                for (int year = 2025; year <= 2027; year++)
                {
                    string version = year.ToString();
                    string detectedDirectory;
                    detectedInstallations.TryGetValue(version, out detectedDirectory);
PackageInfo bundled;
                    if (_bundledPackages.TryGetValue(version, out bundled))
                    {
                        bundled.DetectedRevitDirectory = detectedDirectory;
                        _packages.Add(bundled);
                    }
                }
                foreach (PackageInfo detectedPackage in _packages)
                {
                    _packageSelector.Items.Add(detectedPackage);
                }
                if (_packages.Count == 0)
                {
                    throw new InvalidOperationException("安装包缺少 Revit 2020–2024 自动适配模板。");
                }

                int selectedIndex = 0;
                for (int index = 0; index < _packages.Count; index++)
                {
                    if (!string.IsNullOrWhiteSpace(_packages[index].DetectedRevitDirectory))
                    {
                        selectedIndex = index;
                        break;
                    }
                }
                _packageSelector.SelectedIndex = selectedIndex;
                RefreshEnvironment();
                Append(detectedInstallations.Count > 0
                    ? "已识别本机 Revit，可直接选择版本安装。"
                    : "未自动识别 Revit。请点击“选择 Revit 图标或应用”。");
            }
            catch (Exception ex)
            {
                _previewButton.Enabled = false;
                _installButton.Enabled = false;
                _environment.Text = "安装包无法启动：" + ex.Message;
                Append(ex.ToString());
            }
        }

        private void BrowseForRevit(object sender, EventArgs eventArgs)
        {
            using (var dialog = new FolderBrowserDialog())
            {
                dialog.Description = "请选择包含 Revit.exe 和 RevitAPI.dll 的 Revit 安装目录。";
                if (Directory.Exists(_revitDirectory.Text))
                {
                    dialog.SelectedPath = _revitDirectory.Text;
                }

                if (dialog.ShowDialog(this) == DialogResult.OK)
                {
                    string selectedDirectory = dialog.SelectedPath;
                    string detectedVersion = GetRevitVersion(selectedDirectory, Path.Combine(selectedDirectory, "Revit.exe"));
                    if (!string.IsNullOrWhiteSpace(detectedVersion))
                    {
                        for (int index = 0; index < _packages.Count; index++)
                        {
                            if (string.Equals(_packages[index].RevitVersion, detectedVersion, StringComparison.OrdinalIgnoreCase))
                            {
                                _packages[index].DetectedRevitDirectory = selectedDirectory;
                                _packageSelector.SelectedIndex = index;
                                break;
                            }
                        }
                    }
                    _revitDirectory.Text = selectedDirectory;
                }
            }
        }

        private void BrowseForRevitApplication(object sender, EventArgs eventArgs)
        {
            using (var dialog = new OpenFileDialog())
            {
                dialog.Title = "选择 Revit 图标或应用";
                dialog.Filter = "Revit 图标或应用|*.lnk;*.exe|所有文件|*.*";
                dialog.FilterIndex = 1;
                dialog.InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                dialog.CheckFileExists = true;
                dialog.Multiselect = false;
                if (dialog.ShowDialog(this) != DialogResult.OK) return;

                string targetPath = string.Equals(Path.GetExtension(dialog.FileName), ".lnk", StringComparison.OrdinalIgnoreCase)
                    ? ResolveShortcutTarget(dialog.FileName)
                    : dialog.FileName;
                string selectedDirectory = string.IsNullOrWhiteSpace(targetPath) ? null : Path.GetDirectoryName(targetPath);
                string detectedVersion = GetRevitVersion(selectedDirectory, targetPath);
                if (!IsRevitDirectory(selectedDirectory) || !IsSupportedRevitVersion(detectedVersion))
                {
                    MessageBox.Show(this,
                        "所选文件没有指向受支持的 Revit 2025–2027。请重新选择 Revit 图标或应用。",
                        "未识别到 Revit",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);
                    return;
                }

                for (int index = 0; index < _packages.Count; index++)
                {
                    if (!string.Equals(_packages[index].RevitVersion, detectedVersion, StringComparison.OrdinalIgnoreCase)) continue;
                    _packages[index].DetectedRevitDirectory = selectedDirectory;
                    _packageSelector.SelectedIndex = index;
                    _revitDirectory.Text = selectedDirectory;
                    Append("已识别 Revit " + detectedVersion + "。");
                    return;
                }
            }
        }

        private void RefreshEnvironment()
        {
            PackageInfo package = _packageSelector.SelectedItem as PackageInfo;
            if (package == null)
            {
                return;
            }

            string configuredDirectory = _revitDirectory.Text.Trim();
            if (!string.IsNullOrWhiteSpace(package.DetectedRevitDirectory) &&
                !string.Equals(configuredDirectory, package.DetectedRevitDirectory, StringComparison.OrdinalIgnoreCase))
            {
                _revitDirectory.Text = package.DetectedRevitDirectory;
                return;
            }
            if (string.IsNullOrWhiteSpace(configuredDirectory))
            {
                configuredDirectory = FindRevitDirectory(package.RevitVersion) ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(configuredDirectory))
                {
                    _revitDirectory.Text = configuredDirectory;
                    return;
                }
            }

            bool validDirectory = IsRevitDirectoryForVersion(configuredDirectory, package.RevitVersion);
            bool revitRunning = Process.GetProcessesByName("Revit").Length > 0;
            if (_installed && string.Equals(_installedVersion, package.RevitVersion, StringComparison.OrdinalIgnoreCase))
            {
                _environment.Text = "Revit " + package.RevitVersion + " 已安装命令桥。";
                _statusSummary.Text = string.IsNullOrWhiteSpace(_installationResultSummary)
                    ? "Revit 插件已安装。请检查 AI 连接状态后再发送命令。"
                    : _installationResultSummary;
                _installButton.Text = "完成";
                _installButton.Enabled = true;
                _copyMcpButton.Visible = true;
                _copyMcpButton.Enabled = true;
                _closeRevitButton.Visible = false;
                _browseRevitButton.Visible = false;
                return;
            }

            if (!validDirectory)
            {
                _environment.Text = "未确认 Revit " + package.RevitVersion + " 的安装位置。";
                _statusSummary.Text = "没有自动找到 Revit。\r\n点击“选择 Revit 图标或应用”，可从任意位置选择。";
                _installButton.Text = "选择 Revit 图标或应用";
                _installButton.Enabled = false;
                _copyMcpButton.Visible = false;
                _closeRevitButton.Visible = false;
                _browseRevitButton.Visible = true;
                return;
            }

            if (revitRunning)
            {
                _environment.Text = "已找到 Revit " + package.RevitVersion + "，但它正在打开。";
                _statusSummary.Text = "请先保存并关闭 Revit，再继续安装。\r\n“关闭 Revit”会正常提示保存，不会强制结束。";
                _installButton.Text = "请先关闭 Revit";
                _installButton.Enabled = false;
                _copyMcpButton.Visible = false;
                _closeRevitButton.Visible = true;
                _browseRevitButton.Visible = false;
                return;
            }

            string adapterHint = package.RequiresLocalBuild
                ? "将使用本机 Revit API 自动生成适配插件。"
                : "将使用已匹配的年份适配插件。";
            _environment.Text = "已找到 Revit " + package.RevitVersion + "，可以安装。";
            _statusSummary.Text = "准备完成\r\n" + adapterHint + "\r\n点击“安装到 Revit " + package.RevitVersion + "”。安装后不需要再打开本安装器。";
            _installButton.Text = "安装到 Revit " + package.RevitVersion;
            _installButton.Enabled = true;
            _copyMcpButton.Visible = false;
            _closeRevitButton.Visible = false;
            _browseRevitButton.Visible = false;
        }

        private void CloseRevitGracefully(object sender, EventArgs eventArgs)
        {
            Process[] revitProcesses = Process.GetProcessesByName("Revit");
            if (revitProcesses.Length == 0)
            {
                RefreshEnvironment();
                return;
            }

            _statusSummary.Text = "正在请求 Revit 正常关闭…\r\n请在 Revit 的保存提示中选择保存或取消；本安装器不会强制结束 Revit。";
            _closeRevitButton.Enabled = false;
            try
            {
                foreach (Process process in revitProcesses)
                {
                    try
                    {
                        if (process.MainWindowHandle != IntPtr.Zero)
                        {
                            process.CloseMainWindow();
                        }
                    }
                    catch (InvalidOperationException)
                    {
                        // The process has already exited.
                    }
                    finally
                    {
                        process.Dispose();
                    }
                }
            }
            finally
            {
                _closeRevitButton.Enabled = true;
            }

            var timer = new Timer { Interval = 700 };
            int checks = 0;
            timer.Tick += delegate
            {
                checks++;
                if (Process.GetProcessesByName("Revit").Length == 0 || checks >= 45)
                {
                    timer.Stop();
                    timer.Dispose();
                    RefreshEnvironment();
                }
            };
            timer.Start();
        }

        private void RefreshConnectorHint()
        {
            _apiSettings.Visible = false;
            _apiSettingsLabel.Visible = false;
            _connectorHint.Text = "自动扫描本机已安装的 MCP 客户端并完成配置；未识别的软件可在 Revit 中点击“复制 MCP”。";
        }

        private void RunInstaller(bool preview)
        {
            PackageInfo package = _packageSelector.SelectedItem as PackageInfo;
            ConnectorInfo connector = _connectorSelector.SelectedItem as ConnectorInfo;
            if (package == null || connector == null)
            {
                MessageBox.Show(this, "请选择 Revit 适配包。", "Revit 命令桥", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (!ValidateAiProvider(connector, preview))
            {
                return;
            }

            string revitDirectory = _revitDirectory.Text.Trim();
            if (!IsRevitDirectoryForVersion(revitDirectory, package.RevitVersion))
            {
                MessageBox.Show(this, "所选目录不是 Revit " + package.RevitVersion + "，或缺少 Revit.exe / RevitAPI.dll / RevitAPIUI.dll。", "Revit AI Hub", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (!preview && Process.GetProcessesByName("Revit").Length > 0)
            {
                RefreshEnvironment();
                return;
            }

            SetBusy(true, preview ? "第 1/4 步：检查安装条件…" : "第 1/4 步：检查安装条件…");
            UpdateStage("第 1/4 步：已确认 Revit " + package.RevitVersion + "，准备开始安装", 10);
            bool installed = false;
            try
            {
                string packageDirectory = PreparePackage(package, revitDirectory);
                if (string.IsNullOrWhiteSpace(packageDirectory))
                {
                    return;
                }
                UpdateStage(preview ? "第 3/4 步：校验安装目标…" : "第 3/4 步：安装命令桥…", 55);
                Application.DoEvents();
                string installer = Path.Combine(packageDirectory, "install-revit.ps1");
                const string installConnector = "none";
                string arguments = "-NoProfile -ExecutionPolicy Bypass -File " + Quote(installer) +
                                   " -RevitVersion " + Quote(package.RevitVersion) +
                                   " -RevitInstallDirectory " + Quote(revitDirectory) +
                                   " -PackageDirectory " + Quote(packageDirectory) +
                                   " -Connector " + Quote(installConnector) +
                                   (preview ? " -WhatIf" : string.Empty);
                Append((preview ? "预览安装：" : "开始安装：") + "Revit " + package.RevitVersion + " / " + connector.Display);
                installed = RunPowerShell(arguments, preview ? "第 3/4 步：校验安装目标" : "第 3/4 步：安装命令桥", 55, 75, 300, 180);
                if (installed && !preview)
                {
                    ConfigureDetectedClients(package);
                }
            }
            finally
            {
                SetBusy(false, string.Empty);
            }
            if (installed && !preview && connector.Value == "openai-compatible")
            {
                try
                {
                    string profilePath = SaveAiProviderConfiguration(package);
                    _apiKey.Text = string.Empty;
                    Append("通用模型配置已保存（Windows DPAPI 加密）：" + profilePath);
                }
                catch (Exception ex)
                {
                    Append("Revit 插件已安装，但模型 API 配置保存失败：" + ex.Message);
                    MessageBox.Show(this,
                        "Revit 插件已安装，但模型 API 配置保存失败。请在安装目录 scripts 文件夹运行 configure-ai-provider.ps1 重新保存 Key。\r\n\r\n" + ex.Message,
                        "模型配置未完成",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);
                }
            }
            if (installed && !preview)
            {
                _installed = true;
                _installedVersion = package.RevitVersion;
                SetInstallationResultSummary(package, connector);
            }
            RefreshEnvironment();
        }

        private void RunUninstaller(object sender, EventArgs eventArgs)
        {
            PackageInfo package = _packageSelector.SelectedItem as PackageInfo;
            if (package == null)
            {
                return;
            }
            if (Process.GetProcessesByName("Revit").Length > 0)
            {
                RefreshEnvironment();
                MessageBox.Show(this, "请先保存并关闭 Revit，再卸载命令桥。", "请关闭 Revit", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            DialogResult confirmation = MessageBox.Show(
                this,
                "将从 Revit " + package.RevitVersion + " 移除命令桥及其 AI 连接配置。\r\n不会卸载 Revit，也不会删除其它 Revit 插件。是否继续？",
                "确认卸载命令桥",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);
            if (confirmation != DialogResult.Yes)
            {
                return;
            }

            string script = Path.Combine(package.Directory, "uninstall-revit.ps1");
            if (!File.Exists(script))
            {
                MessageBox.Show(this, "安装包中缺少卸载脚本，请使用最新安装包。", "无法卸载", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            SetBusy(true, "正在卸载命令桥…");
            UpdateStage("正在卸载命令桥", 10);
            try
            {
                string arguments = "-NoProfile -ExecutionPolicy Bypass -File " + Quote(script) +
                                   " -RevitVersion " + Quote(package.RevitVersion) +
                                   " -Confirm:$false";
                if (RunPowerShell(arguments, "正在卸载命令桥", 10, 95, 180, 120))
                {
                    _installed = false;
                    _installedVersion = null;
                    _statusSummary.Text = "已卸载命令桥。\r\nRevit 和其它插件没有被删除。";
                    _environment.Text = "命令桥已从 Revit " + package.RevitVersion + " 移除。";
                }
            }
            finally
            {
                SetBusy(false, string.Empty);
            }
            RefreshEnvironment();
        }

        private void SetBusy(bool busy, string statusText)
        {
            _previewButton.Enabled = !busy;
            _installButton.Enabled = !busy;
            _copyMcpButton.Enabled = !busy && _installed;
            _uninstallButton.Enabled = !busy;
            _packageSelector.Enabled = !busy;
            _connectorSelector.Enabled = false;
            _revitDirectory.Enabled = !busy;
            _refreshButton.Enabled = !busy;
            _closeRevitButton.Enabled = !busy;
            _progress.Visible = busy;
            if (!busy)
            {
                _progress.Value = 0;
                _stageName = null;
            }
            if (!string.IsNullOrWhiteSpace(statusText))
            {
                Append(statusText);
                _environment.Text = statusText;
                Application.DoEvents();
            }
        }

        private void UpdateStage(string stageText, int progress, bool writeLog = true)
        {
            _stageName = stageText ?? string.Empty;
            _stageStartedUtc = DateTime.UtcNow;
            _stageProgress = Math.Max(_progress.Minimum, Math.Min(_progress.Maximum, progress));
            _progress.Value = _stageProgress;
            _environment.Text = _stageName + " · " + _stageProgress + "%";
            _statusSummary.Text = _environment.Text;
            if (writeLog) Append(_environment.Text);
            Application.DoEvents();
        }

        private void RefreshStageElapsed()
        {
            if (string.IsNullOrWhiteSpace(_stageName)) return;
            TimeSpan elapsed = DateTime.UtcNow - _stageStartedUtc;
            string elapsedText = string.Format("{0:D2}:{1:D2}", (int)elapsed.TotalMinutes, elapsed.Seconds);
            _environment.Text = _stageName + " · " + _stageProgress + "% · 已运行 " + elapsedText;
            _statusSummary.Text = _environment.Text;
        }

        private bool ValidateAiProvider(ConnectorInfo connector, bool preview)
        {
            if (connector.Value != "openai-compatible")
            {
                return true;
            }

            string baseUrl = _apiBaseUrl.Text.Trim().TrimEnd('/');
            Uri parsedUrl;
            bool validUrl = Uri.TryCreate(baseUrl, UriKind.Absolute, out parsedUrl) &&
                            (parsedUrl.Scheme == Uri.UriSchemeHttp || parsedUrl.Scheme == Uri.UriSchemeHttps) &&
                            !parsedUrl.Host.Equals("api.example.com", StringComparison.OrdinalIgnoreCase);
            if (!validUrl)
            {
                MessageBox.Show(this,
                    "请填写模型供应商提供的 OpenAI 兼容 Base URL。\r\nDeepSeek 示例：https://api.deepseek.com/v1",
                    "模型 API 地址",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return false;
            }

            string model = _apiModel.Text.Trim();
            if (string.IsNullOrWhiteSpace(model) || model.Equals("your-model-name", StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show(this,
                    "请填写模型名称，例如供应商控制台中显示的模型 ID。",
                    "模型名称",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return false;
            }

            if (!preview && string.IsNullOrWhiteSpace(_apiKey.Text))
            {
                MessageBox.Show(this,
                    "确认安装时需要填写 API Key；预览安装不会保存 Key。",
                    "API Key",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return false;
            }
            return true;
        }

        private string SaveAiProviderConfiguration(PackageInfo package)
        {
            string baseUrl = _apiBaseUrl.Text.Trim().TrimEnd('/');
            string model = _apiModel.Text.Trim();
            string apiKey = _apiKey.Text;
            byte[] keyBytes = Encoding.UTF8.GetBytes(apiKey);
            byte[] entropy = Encoding.UTF8.GetBytes("RevitCommandBridge:ai-provider:1:" + package.RevitVersion);
            byte[] protectedKey = null;
            try
            {
                protectedKey = ProtectedData.Protect(keyBytes, entropy, DataProtectionScope.CurrentUser);
            }
            finally
            {
                Array.Clear(keyBytes, 0, keyBytes.Length);
                Array.Clear(entropy, 0, entropy.Length);
            }

            string profileDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "RevitCommandBridge",
                package.RevitVersion,
                "ai-providers");
            Directory.CreateDirectory(profileDirectory);
            string profilePath = Path.Combine(profileDirectory, "default.json");
            try
            {
                var profile = new Dictionary<string, object>
                {
                    { "schema_version", 1 },
                    { "provider_kind", "openai-compatible" },
                    { "revit_version", package.RevitVersion },
                    { "base_url", baseUrl },
                    { "model", model },
                    { "api_key_protected", Convert.ToBase64String(protectedKey) },
                    { "credential_scheme", "dpapi-current-user-v1" },
                    { "updated_utc", DateTime.UtcNow.ToString("o") }
                };
                var serializer = new JavaScriptSerializer();
                File.WriteAllText(profilePath, serializer.Serialize(profile) + Environment.NewLine, new UTF8Encoding(false));
                return profilePath;
            }
            finally
            {
                if (protectedKey != null)
                {
                    Array.Clear(protectedKey, 0, protectedKey.Length);
                }
            }
        }

        private bool RunPowerShell(string arguments, string stageText = null, int progressStart = 0, int progressEnd = 100, int timeoutSeconds = 600, int silenceTimeoutSeconds = 120, string statusFile = null)
        {
            string powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell\\v1.0\\powershell.exe");
            if (!File.Exists(powershell))
            {
                powershell = "powershell.exe";
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = powershell,
                Arguments = arguments,
                WorkingDirectory = _payloadDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            using (Process process = Process.Start(startInfo))
            {
                _lastPowerShellError = null;
                _latestAdapterFailure = null;
                _lastAdapterStatusSignature = null;
                var outputBuffer = new StringBuilder();
                var errorBuffer = new StringBuilder();
                int displayedOutputLength = 0;
                int displayedErrorLength = 0;
                DateTime startedUtc = DateTime.UtcNow;
                DateTime lastActivityUtc = startedUtc;
                if (!string.IsNullOrWhiteSpace(stageText)) UpdateStage(stageText, progressStart);
                process.OutputDataReceived += delegate(object sender, DataReceivedEventArgs eventArgs)
                {
                    if (eventArgs.Data != null)
                    {
                        lock (outputBuffer)
                        {
                            outputBuffer.AppendLine(eventArgs.Data);
                        }
                    }
                };
                process.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs eventArgs)
                {
                    if (eventArgs.Data != null)
                    {
                        lock (errorBuffer)
                        {
                            errorBuffer.AppendLine(eventArgs.Data);
                        }
                    }
                };
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                while (!process.WaitForExit(100))
                {
                    if (ReadAdapterStatus(statusFile, stageText, progressStart, progressEnd))
                    {
                        lastActivityUtc = DateTime.UtcNow;
                    }
                    if (!string.IsNullOrWhiteSpace(_latestAdapterFailure))
                    {
                        Append("适配失败原因：" + _latestAdapterFailure);
                        try { process.Kill(); } catch { }
                        process.WaitForExit();
                        return false;
                    }
                    string pendingOutput;
                    string pendingError;
                    lock (outputBuffer) { pendingOutput = outputBuffer.ToString(); }
                    lock (errorBuffer) { pendingError = errorBuffer.ToString(); }
                    if (pendingOutput.Length > displayedOutputLength)
                    {
                        string newOutput = pendingOutput.Substring(displayedOutputLength);
                        Append(newOutput);
                        ApplyScriptProgress(newOutput, stageText, progressStart, progressEnd);
                        displayedOutputLength = pendingOutput.Length;
                        lastActivityUtc = DateTime.UtcNow;
                    }
                    if (pendingError.Length > displayedErrorLength)
                    {
                        Append(pendingError.Substring(displayedErrorLength));
                        displayedErrorLength = pendingError.Length;
                        lastActivityUtc = DateTime.UtcNow;
                    }
                    if (silenceTimeoutSeconds > 0 && (DateTime.UtcNow - lastActivityUtc).TotalSeconds >= silenceTimeoutSeconds)
                    {
                        Append("当前步骤连续 " + silenceTimeoutSeconds + " 秒没有任何进度，已停止。请查看详细日志。");
                        try { process.Kill(); } catch { }
                        process.WaitForExit();
                        return false;
                    }
                    if ((DateTime.UtcNow - startedUtc).TotalSeconds >= timeoutSeconds)
                    {
                        Append("当前步骤在 " + timeoutSeconds + " 秒内未完成，已停止。请查看详细日志中的最后错误。");
                        try { process.Kill(); } catch { }
                        process.WaitForExit();
                        return false;
                    }
                    RefreshStageElapsed();
                    Application.DoEvents();
                }
                process.WaitForExit();
                string output;
                string error;
                lock (outputBuffer)
                {
                    output = outputBuffer.ToString();
                }
                lock (errorBuffer)
                {
                    error = errorBuffer.ToString();
                }
                if (output.Length > displayedOutputLength) Append(output.Substring(displayedOutputLength));
                if (error.Length > displayedErrorLength) Append(error.Substring(displayedErrorLength));
                _lastPowerShellError = error.Trim();
                Append("PowerShell exit code: " + process.ExitCode);
                if (process.ExitCode == 0)
                {
                    if (!string.IsNullOrWhiteSpace(stageText)) UpdateStage(stageText + "完成", progressEnd);
                    Append("当前步骤完成。请查看上方识别、安装和配置结果。");
                }
                return process.ExitCode == 0;
            }
        }

        private void ApplyScriptProgress(string output, string stageText, int progressStart, int progressEnd)
        {
            if (string.IsNullOrWhiteSpace(output) || string.IsNullOrWhiteSpace(stageText)) return;

            // The installer script emits explicit copy/manifest markers. They
            // are more useful than a single static 55% bar while a bundled
            // Node runtime is being copied from the self-contained EXE.
            string[] installMarkers = { "copy-files", "copy-complete", "write-manifest", "write-inventory", "complete" };
            string[] installNames = { "复制插件文件", "文件复制完成", "写入 Revit 加载项清单", "更新文件清单", "安装文件校验完成" };
            for (int markerIndex = installMarkers.Length - 1; markerIndex >= 0; markerIndex--)
            {
                string marker = "RCB_INSTALL_STAGE=" + installMarkers[markerIndex];
                if (output.IndexOf(marker, StringComparison.OrdinalIgnoreCase) < 0) continue;
                int installProgress = progressStart + ((progressEnd - progressStart) * (markerIndex + 1) / installMarkers.Length);
                UpdateStage(stageText + " · " + installNames[markerIndex], installProgress, false);
                return;
            }
            for (int step = 5; step >= 1; step--)
            {
                string marker = "RCB_STAGE=" + step;
                if (output.IndexOf(marker, StringComparison.OrdinalIgnoreCase) < 0) continue;
                int progress = progressStart + ((progressEnd - progressStart) * step / 5);
                string[] names = { string.Empty, "检查本机组件", "准备插件文件", "读取 Revit 版本", "编译插件", "验证生成结果" };
                UpdateStage(stageText + " · " + names[step], progress);
                return;
            }
        }

        private bool ReadAdapterStatus(string statusFile, string stageText, int progressStart, int progressEnd)
        {
            if (string.IsNullOrWhiteSpace(statusFile) || !File.Exists(statusFile) || string.IsNullOrWhiteSpace(stageText)) return false;
            try
            {
                string rawStatus = File.ReadAllText(statusFile, Encoding.UTF8).Trim().TrimStart('\ufeff');
                if (string.Equals(rawStatus, _lastAdapterStatusSignature, StringComparison.Ordinal)) return false;
                _lastAdapterStatusSignature = rawStatus;
                string[] parts = rawStatus.Split('|');
                if (parts.Length >= 3 && string.Equals(parts[0], "ERROR", StringComparison.OrdinalIgnoreCase))
                {
                    _latestAdapterFailure = "第 " + parts[1] + "/5 步：" + parts[2];
                    return true;
                }
                if (parts.Length >= 3 && string.Equals(parts[0], "RUN", StringComparison.OrdinalIgnoreCase))
                {
                    string[] normalized = new string[parts.Length - 1];
                    Array.Copy(parts, 1, normalized, 0, normalized.Length);
                    parts = normalized;
                }
                int step;
                if (parts.Length < 2 || !Int32.TryParse(parts[0], out step) || step < 1 || step > 5) return false;
                int progress = progressStart + ((progressEnd - progressStart) * step / 5);
                string[] names = { string.Empty, "检查本机组件", "准备插件文件", "读取 Revit 版本", "编译插件", "验证生成结果" };
                UpdateStage(stageText + " · " + names[step], progress, false);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private void ConfigureDetectedClients(PackageInfo package)
        {
            string installedRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "RevitCommandBridge",
                package.RevitVersion);
            string configurator = Path.Combine(installedRoot, "scripts", "configure-detected-clients.ps1");
            if (!File.Exists(configurator))
            {
                Append("客户端自动配置脚本缺失：" + configurator);
                return;
            }
            string resultPath = Path.Combine(installedRoot, "connections", "detected-clients-" + package.RevitVersion + ".json");
            try
            {
                if (File.Exists(resultPath))
                {
                    File.Delete(resultPath);
                }
            }
            catch (Exception ex)
            {
                Append("清理旧连接检测结果失败：" + ex.Message);
            }

            UpdateStage("第 4/4 步：识别并连接本机 AI 应用", 75);
            string arguments = "-NoProfile -ExecutionPolicy Bypass -File " + Quote(configurator) +
                               " -RevitVersion " + Quote(package.RevitVersion) +
                               " -RootDirectory " + Quote(installedRoot);
            if (!RunPowerShell(arguments, "第 4/4 步：识别并连接本机 AI 应用", 75, 100, 300, 120))
            {
                Append("Revit 插件已安装；部分 AI 客户端可能需要使用 connections 目录中的通用 MCP 配置。");
            }
        }

        private void SetInstallationResultSummary(PackageInfo package, ConnectorInfo connector)
        {
            string installedRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "RevitCommandBridge",
                package.RevitVersion);
            string resultPath = Path.Combine(installedRoot, "connections", "detected-clients-" + package.RevitVersion + ".json");
            var configuredNames = new List<string>();
            try
            {
                if (File.Exists(resultPath))
                {
                    var serializer = new JavaScriptSerializer();
                    var result = serializer.DeserializeObject(File.ReadAllText(resultPath, Encoding.UTF8)) as IDictionary<string, object>;
                    object rawNames;
                    if (result != null && result.TryGetValue("configured_client_names", out rawNames))
                    {
                        object[] nameArray = rawNames as object[];
                        if (nameArray != null)
                        {
                            foreach (object rawName in nameArray)
                            {
                                string name = Convert.ToString(rawName);
                                if (!string.IsNullOrWhiteSpace(name) && !configuredNames.Contains(name)) configuredNames.Add(name);
                            }
                        }
                        else
                        {
                            string name = Convert.ToString(rawNames);
                            if (!string.IsNullOrWhiteSpace(name)) configuredNames.Add(name);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Append("读取 AI 连接结果失败：" + ex.Message);
            }

            if (configuredNames.Count > 0)
            {
                _installationResultSummary = "✓ Revit " + package.RevitVersion + " 插件已安装\r\n" +
                    "✓ 已连接：" + string.Join(" / ", configuredNames.ToArray()) + "\r\n" +
                    "✓ 通用 MCP 配置已生成\r\n" +
                    "下一步：打开 Revit 项目，并完全退出后重新打开上述 AI，再在 AI 对话框发送测试命令。";
                return;
            }

            if (connector.Value == "openai-compatible")
            {
                _installationResultSummary = "✓ Revit " + package.RevitVersion + " 插件已安装\r\n" +
                    "✓ 模型 API 配置已保存\r\n下一步：打开 Revit 项目，再启动安装目录中的本机助手。";
                return;
            }

            string genericPath = Path.Combine(installedRoot, "connections", "generic-mcp-revit-" + package.RevitVersion + ".mcp.json");
            _installationResultSummary = "✓ Revit " + package.RevitVersion + " 插件已安装\r\n" +
                (File.Exists(genericPath) ? "✓ 通用 MCP 配置已生成\r\n" : "") +
                "未识别到具体 AI 客户端。点击“复制 MCP 配置”，粘贴到 Codex、WorkBuddy 或其它 MCP 客户端。";
        }

        private string PreparePackage(PackageInfo package, string revitDirectory)
        {
            if (!package.RequiresLocalBuild)
            {
                return package.Directory;
            }

            string outputDirectory = Path.Combine(_payloadDirectory, "generated-adapters", "RevitCommandBridge-" + package.RevitVersion);
            string compiler = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Microsoft.NET", "Framework64", "v4.0.30319", "csc.exe");
            string revitApi = Path.Combine(revitDirectory, "RevitAPI.dll");
            string revitApiUi = Path.Combine(revitDirectory, "RevitAPIUI.dll");
            string sourceDirectory = Path.Combine(package.Directory, "src");
            foreach (string required in new[] { compiler, revitApi, revitApiUi, sourceDirectory })
            {
                if (!File.Exists(required) && !Directory.Exists(required))
                {
                    ShowAdapterFailure(package.RevitVersion, "检查本机组件", "缺少文件：" + required);
                    return null;
                }
            }

            try
            {
                UpdateStage("第 2/4 步：检查 Revit " + package.RevitVersion + " 组件", 15);
                CopyAdapterTemplate(package.Directory, outputDirectory);

                UpdateStage("第 2/4 步：读取 Revit " + package.RevitVersion + " 版本", 25);
                Version apiVersion = AssemblyName.GetAssemblyName(revitApi).Version;
                string[] sourceFiles = Directory.GetFiles(sourceDirectory, "*.cs", SearchOption.TopDirectoryOnly);
                if (sourceFiles.Length == 0) throw new InvalidOperationException("安装包中没有插件源文件。");

                string assemblyPath = Path.Combine(outputDirectory, "RevitCommandBridge.dll");
                var arguments = new StringBuilder();
                arguments.Append("/nologo /target:library /platform:anycpu /optimize+ /debug:pdbonly ");
                if (apiVersion != null && apiVersion.Major >= 21) arguments.Append("/define:REVIT_FORGE_UNITS ");
                arguments.Append("/out:").Append(Quote(assemblyPath)).Append(' ');
                arguments.Append("/reference:").Append(Quote(revitApi)).Append(' ');
                arguments.Append("/reference:").Append(Quote(revitApiUi)).Append(' ');
                arguments.Append("/reference:System.Web.Extensions.dll /reference:System.Windows.Forms.dll /reference:System.Drawing.dll ");
                arguments.Append("/reference:").Append(Quote(@"C:\Windows\Microsoft.NET\Framework64\v4.0.30319\WPF\WindowsBase.dll")).Append(' ');
                arguments.Append("/reference:").Append(Quote(@"C:\Windows\Microsoft.NET\Framework64\v4.0.30319\WPF\PresentationCore.dll")).Append(' ');
                foreach (string sourceFile in sourceFiles) arguments.Append(Quote(sourceFile)).Append(' ');

                UpdateStage("第 2/4 步：正在编译 Revit " + package.RevitVersion + " 插件", 40);
                string compilerError;
                if (!RunCompiler(compiler, arguments.ToString(), out compilerError))
                {
                    ShowAdapterFailure(package.RevitVersion, "编译插件", compilerError);
                    return null;
                }
                if (!File.Exists(assemblyPath))
                {
                    ShowAdapterFailure(package.RevitVersion, "验证生成结果", "编译器未生成 RevitCommandBridge.dll。");
                    return null;
                }

                UpdateStage("第 2/4 步：验证 Revit " + package.RevitVersion + " 插件", 50);
                var metadata = new Dictionary<string, object>
                {
                    { "product", "RevitCommandBridge" },
                    { "revit_version", package.RevitVersion },
                    { "protocol", "revit-command-bridge/2.0" },
                    { "runtime", "net48" },
                    { "build_mode", "local-api-adapter" }
                };
                File.WriteAllText(Path.Combine(outputDirectory, "bridge.config.json"),
                    new JavaScriptSerializer().Serialize(metadata) + Environment.NewLine, new UTF8Encoding(false));
                UpdateStage("第 2/4 步：Revit " + package.RevitVersion + " 插件生成完成", 55);
                return outputDirectory;
            }
            catch (Exception ex)
            {
                ShowAdapterFailure(package.RevitVersion, "准备适配插件", ex.ToString());
                return null;
            }
        }

        private bool RunCompiler(string compiler, string arguments, out string error)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = compiler,
                Arguments = arguments,
                WorkingDirectory = _payloadDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using (Process process = Process.Start(startInfo))
            {
                var output = new StringBuilder();
                var errors = new StringBuilder();
                process.OutputDataReceived += delegate(object sender, DataReceivedEventArgs e) { if (e.Data != null) { lock (output) output.AppendLine(e.Data); } };
                process.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs e) { if (e.Data != null) { lock (errors) errors.AppendLine(e.Data); } };
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                DateTime started = DateTime.UtcNow;
                while (!process.WaitForExit(100))
                {
                    TimeSpan elapsed = DateTime.UtcNow - started;
                    _environment.Text = _stageName + " · " + _stageProgress + "% · 已运行 " +
                        string.Format("{0:D2}:{1:D2}", (int)elapsed.TotalMinutes, elapsed.Seconds);
                    Application.DoEvents();
                    if (elapsed.TotalMinutes >= 10)
                    {
                        try { process.Kill(); } catch { }
                        error = "编译超过 10 分钟，已停止。";
                        return false;
                    }
                }
                process.WaitForExit();
                string stdout;
                string stderr;
                lock (output) stdout = output.ToString().Trim();
                lock (errors) stderr = errors.ToString().Trim();
                Append(stdout);
                Append(stderr);
                error = !string.IsNullOrWhiteSpace(stderr) ? stderr : stdout;
                if (process.ExitCode != 0 && string.IsNullOrWhiteSpace(error)) error = "编译器退出码：" + process.ExitCode;
                return process.ExitCode == 0;
            }
        }

        private void ShowAdapterFailure(string version, string stage, string detail)
        {
            string message = "失败阶段：" + stage + "\r\n\r\n原始错误：\r\n" + detail;
            Append("Revit " + version + " 适配失败。\r\n" + message);
            _environment.Text = "Revit " + version + " 适配失败：" + stage;
            _statusSummary.Text = message;
            MessageBox.Show(this, message, "Revit " + version + " 适配失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        private static void CopyAdapterTemplate(string source, string destination)
        {
            Directory.CreateDirectory(destination);
            foreach (string file in Directory.GetFiles(source))
            {
                string name = Path.GetFileName(file);
                if (string.Equals(name, "RevitCommandBridge.dll", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(name, "RevitCommandBridge.pdb", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(name, "bridge.config.json", StringComparison.OrdinalIgnoreCase)) continue;
                File.Copy(file, Path.Combine(destination, name), true);
            }
            foreach (string directory in Directory.GetDirectories(source))
            {
                CopyAdapterTemplate(directory, Path.Combine(destination, Path.GetFileName(directory)));
            }
        }

        private void OpenGuide(object sender, EventArgs eventArgs)
        {
            PackageInfo package = _packageSelector.SelectedItem as PackageInfo;
            if (package == null)
            {
                return;
            }

            string guide = Path.Combine(package.Directory, "README.md");
            if (File.Exists(guide))
            {
                Process.Start(new ProcessStartInfo { FileName = guide, UseShellExecute = true });
            }
        }

        private string GetInstalledRoot(PackageInfo package)
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "RevitCommandBridge",
                package.RevitVersion);
        }

        private string GetGenericMcpPath(PackageInfo package)
        {
            return Path.Combine(
                GetInstalledRoot(package),
                "connections",
                "generic-mcp-revit-" + package.RevitVersion + ".mcp.json");
        }

        private void CopyMcpConfiguration(object sender, EventArgs eventArgs)
        {
            PackageInfo package = _packageSelector.SelectedItem as PackageInfo;
            if (package == null)
            {
                return;
            }

            string path = GetGenericMcpPath(package);
            if (!File.Exists(path))
            {
                MessageBox.Show(this,
                    "通用 MCP 配置尚未生成。请先完成 Revit 插件安装。",
                    "MCP 配置不存在",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }

            try
            {
                Clipboard.SetText(File.ReadAllText(path, Encoding.UTF8));
                _statusSummary.Text = "已复制通用 MCP 配置。\r\n现在可粘贴到 Codex、WorkBuddy 或其它 MCP 客户端。";
                Append("已复制 MCP 配置：" + path);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this,
                    "复制 MCP 配置失败：\r\n" + ex.Message,
                    "MCP 配置",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        }

        private void OpenMcpConfigurationFolder(object sender, EventArgs eventArgs)
        {
            PackageInfo package = _packageSelector.SelectedItem as PackageInfo;
            if (package == null)
            {
                return;
            }

            string folder = Path.Combine(GetInstalledRoot(package), "connections");
            try
            {
                Directory.CreateDirectory(folder);
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = Quote(folder),
                    UseShellExecute = false
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show(this,
                    "打开 MCP 配置目录失败：\r\n" + ex.Message,
                    "MCP 配置",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        }

        private void CleanupPayload()
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(_payloadDirectory) && Directory.Exists(_payloadDirectory))
                {
                    Directory.Delete(_payloadDirectory, true);
                }
            }
            catch
            {
                // A failed temporary cleanup must not affect the installed bridge.
            }
        }

        private void Append(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            _output.AppendText(Environment.NewLine + value.Trim());
        }

        private static void CopyStream(Stream input, Stream output)
        {
            byte[] buffer = new byte[81920];
            int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                output.Write(buffer, 0, read);
            }
        }

        private static bool IsRevitDirectory(string directory)
        {
            try
            {
                return !string.IsNullOrWhiteSpace(directory) &&
                       File.Exists(Path.Combine(directory, "Revit.exe")) &&
                       File.Exists(Path.Combine(directory, "RevitAPI.dll")) &&
                       File.Exists(Path.Combine(directory, "RevitAPIUI.dll"));
            }
            catch
            {
                return false;
            }
        }

        private static bool IsSupportedRevitVersion(string version)
        {
            int value;
            return Int32.TryParse(version, out value) && value >= 2025 && value <= 2027;
        }

        private static bool IsRevitDirectoryForVersion(string directory, string version)
        {
            if (!IsRevitDirectory(directory))
            {
                return false;
            }
            try
            {
                Version apiVersion = AssemblyName.GetAssemblyName(Path.Combine(directory, "RevitAPI.dll")).Version;
                int expectedMajor = Convert.ToInt32(version) - 2000;
                return apiVersion != null && apiVersion.Major == expectedMajor;
            }
            catch
            {
                return false;
            }
        }

        private static string FindRevitDirectory(string version)
        {
            foreach (string registryRoot in new[]
            {
                @"SOFTWARE\Autodesk\Revit",
                @"SOFTWARE\WOW6432Node\Autodesk\Revit"
            })
            {
                using (RegistryKey root = Registry.LocalMachine.OpenSubKey(registryRoot))
                {
                    if (root == null)
                    {
                        continue;
                    }

                    string recursiveMatch = FindRegistryRevitDirectory(root, version, 4);
                    if (!string.IsNullOrWhiteSpace(recursiveMatch))
                    {
                        return recursiveMatch;
                    }

                    foreach (string keyName in root.GetSubKeyNames())
                    {
                        if (keyName.IndexOf(version, StringComparison.OrdinalIgnoreCase) < 0)
                        {
                            continue;
                        }

                        using (RegistryKey child = root.OpenSubKey(keyName))
                        {
                            if (child == null)
                            {
                                continue;
                            }

                            foreach (string propertyName in RevitRegistryPathValueNames())
                            {
                                string directory = NormalizeRevitDirectoryCandidate(Convert.ToString(child.GetValue(propertyName)));
                                if (IsRevitDirectory(directory))
                                {
                                    return directory;
                                }
                            }
                        }
                    }
                }
            }

            var candidates = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Autodesk", "Revit " + version)
            };
            foreach (string candidate in candidates)
            {
                if (IsRevitDirectory(candidate))
                {
                    return candidate;
                }
            }

            return null;
        }

        private static Dictionary<string, string> DetectAllRevitInstallations()
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int year = 2020; year <= 2024; year++)
            {
                string version = year.ToString();
                string directory = FindRevitDirectory(version);
                if (IsRevitDirectory(directory)) result[version] = directory;
            }
            // Shortcut lookup is a fallback only. Registry and standard paths
            // cover normal installs without walking a redirected Desktop.
            if (result.Count < 5) AddShortcutRevitDirectories(result);
            return result;
        }

        private static void AddShortcutRevitDirectories(Dictionary<string, string> result)
        {
            var shortcutRoots = new List<string>
            {
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory)
            };
            string startMenu = Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu);
            if (!string.IsNullOrWhiteSpace(startMenu)) shortcutRoots.Add(Path.Combine(startMenu, "Programs", "Autodesk"));
            foreach (string shortcutRoot in shortcutRoots)
            {
                if (string.IsNullOrWhiteSpace(shortcutRoot) || !Directory.Exists(shortcutRoot)) continue;
                string[] shortcutPaths;
                try { shortcutPaths = Directory.GetFiles(shortcutRoot, "*Revit*.lnk", SearchOption.AllDirectories); }
                catch { shortcutPaths = new string[0]; }
                foreach (string shortcutPath in shortcutPaths)
                {
                    string targetPath = ResolveShortcutTarget(shortcutPath);
                    if (string.IsNullOrWhiteSpace(targetPath) ||
                        !string.Equals(Path.GetFileName(targetPath), "Revit.exe", StringComparison.OrdinalIgnoreCase)) continue;
                    string directory = Path.GetDirectoryName(targetPath);
                    string version = GetRevitVersion(directory, targetPath);
                    if (IsSupportedRevitVersion(version) && IsRevitDirectory(directory) && !result.ContainsKey(version)) result[version] = directory;
                }
            }
        }

        private static string ResolveShortcutTarget(string shortcutPath)
        {
            object shell = null;
            object shortcut = null;
            try
            {
                Type shellType = Type.GetTypeFromProgID("WScript.Shell");
                if (shellType == null) return null;
                shell = Activator.CreateInstance(shellType);
                shortcut = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { shortcutPath });
                return Convert.ToString(shortcut.GetType().InvokeMember("TargetPath", BindingFlags.GetProperty, null, shortcut, null));
            }
            catch
            {
                return null;
            }
            finally
            {
                if (shortcut != null && Marshal.IsComObject(shortcut)) Marshal.FinalReleaseComObject(shortcut);
                if (shell != null && Marshal.IsComObject(shell)) Marshal.FinalReleaseComObject(shell);
            }
        }

        private static IEnumerable<string> FindFilesSafely(string root, string fileName)
        {
            var pending = new Stack<string>();
            pending.Push(root);
            while (pending.Count > 0)
            {
                string current = pending.Pop();
                string[] files;
                try { files = Directory.GetFiles(current, fileName, SearchOption.TopDirectoryOnly); }
                catch { files = new string[0]; }
                foreach (string file in files) yield return file;

                string[] directories;
                try { directories = Directory.GetDirectories(current); }
                catch { directories = new string[0]; }
                foreach (string directory in directories) pending.Push(directory);
            }
        }

        private static void AddRegistryRevitDirectories(RegistryKey key, Dictionary<string, string> result, int depth)
        {
            foreach (string propertyName in RevitRegistryPathValueNames())
            {
                string directory = NormalizeRevitDirectoryCandidate(Convert.ToString(key.GetValue(propertyName)));
                if (!IsRevitDirectory(directory)) continue;
                string version = GetRevitVersion(directory, Path.Combine(directory, "Revit.exe"));
                if (!string.IsNullOrWhiteSpace(version) && !result.ContainsKey(version)) result[version] = directory;
            }
            if (depth <= 0) return;
            foreach (string childName in key.GetSubKeyNames())
            {
                using (RegistryKey child = key.OpenSubKey(childName))
                {
                    if (child != null) AddRegistryRevitDirectories(child, result, depth - 1);
                }
            }
        }

        private static string GetRevitVersion(string directory, string exePath)
        {
            try
            {
                Version apiVersion = AssemblyName.GetAssemblyName(Path.Combine(directory, "RevitAPI.dll")).Version;
                if (apiVersion != null && apiVersion.Major >= 20 && apiVersion.Major <= 99) return (2000 + apiVersion.Major).ToString();
            }
            catch { }
            try
            {
                string text = directory + " " + (File.Exists(exePath) ? FileVersionInfo.GetVersionInfo(exePath).ProductVersion : string.Empty);
                Match match = Regex.Match(text, "(?<!\\d)(20\\d{2})(?!\\d)");
                return match.Success ? match.Groups[1].Value : null;
            }
            catch { return null; }
        }

        private static bool CanUseBundledCompiler(string directory)
        {
            // Keep this helper compatible with PowerShell 7/.NET Core hosts.
            // ReflectionOnlyLoadFrom was removed there; the installer only needs
            // to confirm that the API assembly is readable before compiling.
            try
            {
                return AssemblyName.GetAssemblyName(Path.Combine(directory, "RevitAPI.dll")) != null;
            }
            catch
            {
                return false;
            }
        }

        private static string FindRegistryRevitDirectory(RegistryKey key, string version, int depth)
        {
            foreach (string propertyName in RevitRegistryPathValueNames())
            {
                string directory = NormalizeRevitDirectoryCandidate(Convert.ToString(key.GetValue(propertyName)));
                if (IsRevitDirectoryForVersion(directory, version))
                {
                    return directory;
                }
            }
            if (depth <= 0)
            {
                return null;
            }
            foreach (string childName in key.GetSubKeyNames())
            {
                using (RegistryKey child = key.OpenSubKey(childName))
                {
                    if (child == null)
                    {
                        continue;
                    }
                    string match = FindRegistryRevitDirectory(child, version, depth - 1);
                    if (!string.IsNullOrWhiteSpace(match))
                    {
                        return match;
                    }
                }
            }
            return null;
        }

        private static string[] RevitRegistryPathValueNames()
        {
            return new[]
            {
                "InstallLocation",
                "InstallationLocation",
                "InstallPath",
                "InstallationPath",
                "RevitInstallPath",
                "INSTALLDIR",
                "DisplayIcon"
            };
        }

        private static string NormalizeRevitDirectoryCandidate(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            string candidate = value.Trim().Trim('"');
            int executableEnd = candidate.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            if (executableEnd >= 0)
            {
                candidate = candidate.Substring(0, executableEnd + 4).Trim().Trim('"');
            }
            if (File.Exists(candidate) && string.Equals(Path.GetFileName(candidate), "Revit.exe", StringComparison.OrdinalIgnoreCase))
            {
                return Path.GetDirectoryName(candidate);
            }
            return candidate;
        }

        private static string Quote(string value)
        {
            // Windows command-line parsing treats backslashes immediately before
            // a closing quote as quote escapes. Double trailing backslashes so a
            // detected directory such as "C:\\Program Files\\Autodesk\\Revit 2024\\"
            // does not swallow the following PowerShell arguments.
            value = value ?? string.Empty;
            var quoted = new StringBuilder(value.Length + 2);
            quoted.Append('"');
            int backslashes = 0;
            foreach (char character in value)
            {
                if (character == '\\')
                {
                    backslashes++;
                    continue;
                }
                if (character == '"')
                {
                    quoted.Append('\\', backslashes * 2 + 1);
                    quoted.Append('"');
                    backslashes = 0;
                    continue;
                }
                if (backslashes > 0)
                {
                    quoted.Append('\\', backslashes);
                    backslashes = 0;
                }
                quoted.Append(character);
            }
            if (backslashes > 0) quoted.Append('\\', backslashes * 2);
            quoted.Append('"');
            return quoted.ToString();
        }
    }

    internal sealed class PackageInfo
    {
        public string RevitVersion { get; private set; }
        public string Directory { get; private set; }
        public string DetectedRevitDirectory { get; set; }
        public bool RequiresLocalBuild { get; private set; }

public static PackageInfo Read(string directory)
        {
            string metadataPath = Path.Combine(directory, "bridge.config.json");
            string assemblyPath = Path.Combine(directory, "RevitCommandBridge.dll");
            if (!File.Exists(metadataPath) || !File.Exists(assemblyPath))
            {
                return null;
            }

            try
            {
                var serializer = new JavaScriptSerializer();
                var metadata = serializer.DeserializeObject(File.ReadAllText(metadataPath, Encoding.UTF8)) as IDictionary<string, object>;
                object version;
                if (metadata == null || !metadata.TryGetValue("revit_version", out version))
                {
                    return null;
                }

                string revitVersion = Convert.ToString(version);
                if (string.IsNullOrWhiteSpace(revitVersion))
                {
                    return null;
                }

                return new PackageInfo
                {
                    RevitVersion = revitVersion,
                    Directory = directory,
                    RequiresLocalBuild = false
                };
            }
            catch
            {
                return null;
            }
        }

        public override string ToString()
        {
            return "Revit " + RevitVersion;
        }
    }

    internal sealed class ConnectorInfo
    {
        public ConnectorInfo(string value, string display)
        {
            Value = value;
            Display = display;
        }

        public string Value { get; private set; }
        public string Display { get; private set; }

        public override string ToString()
        {
            return Display;
        }
    }
}
