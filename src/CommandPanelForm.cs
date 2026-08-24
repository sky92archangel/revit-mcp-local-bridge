using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text;
#if !REVIT_NET8
using System.Web.Script.Serialization;
#endif
using System.Windows.Forms;
namespace RevitCommandBridge
{
    internal sealed class CommandPanelForm : Form
    {
        private readonly Label _status;
        private readonly TextBox _command;
        private readonly Timer _timer;
        public CommandPanelForm()
        {
            Text = "Revit 命令桥";
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(590, 245);
            MinimumSize = new Size(606, 284);
            MaximumSize = new Size(606, 284);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            BackColor = Color.White;
            Font = new Font("Microsoft YaHei UI", 9.0f);
            _status = new Label { Dock = DockStyle.Top, Height = 42, Padding = new Padding(14, 11, 14, 5), BackColor = Color.FromArgb(232, 243, 253), ForeColor = Color.FromArgb(27, 86, 142), Text = StatusText() };
            var body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(16, 12, 16, 10) };
            var heading = new Label { Dock = DockStyle.Top, Height = 28, Font = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold), Text = "复制测试命令，粘贴到常用 AI 对话框发送" };
            _command = new TextBox { Dock = DockStyle.Top, Height = 46, Multiline = true, ReadOnly = true, BorderStyle = BorderStyle.FixedSingle, BackColor = Color.White, Font = new Font("Microsoft YaHei UI", 9.0f), Text = "请通过 Revit 命令桥查询当前打开的项目和可用标高，只查询，不要修改模型。" };
            _command.Click += delegate { _command.Focus(); _command.SelectAll(); };
            var actions = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 39, Padding = new Padding(0, 5, 0, 0), FlowDirection = FlowDirection.LeftToRight };
            var copy = ButtonFor("复制测试命令", true);
            copy.Click += CopyCommand;
            var refresh = ButtonFor("刷新状态", false);
            refresh.Click += delegate { _status.Text = StatusText(); };
            var help = ButtonFor("连接有问题？", false);
            help.Click += HelpClick;
            actions.Controls.Add(copy);
            actions.Controls.Add(refresh);
            actions.Controls.Add(help);
            var note = new Label { Dock = DockStyle.Top, Height = 32, Padding = new Padding(1, 6, 1, 0), ForeColor = Color.FromArgb(91, 105, 120), Text = "AI 返回项目或标高后，再直接描述你要建什么。" };
            body.Controls.Add(note);
            body.Controls.Add(actions);
            body.Controls.Add(_command);
            body.Controls.Add(heading);
            Controls.Add(body);
            Controls.Add(_status);
            _timer = new Timer { Interval = 1000 };
            _timer.Tick += delegate { _status.Text = StatusText(); };
            _timer.Start();
            FormClosed += delegate { _timer.Stop(); _timer.Dispose(); };
        }
        private static Button ButtonFor(string text, bool primary)
        {
            var button = new Button { Text = text, AutoSize = true, Margin = new Padding(0, 0, 8, 0), Padding = new Padding(10, 3, 10, 3), FlatStyle = FlatStyle.Flat, BackColor = primary ? Color.FromArgb(38, 106, 181) : Color.White, ForeColor = primary ? Color.White : Color.FromArgb(55, 78, 101) };
            button.FlatAppearance.BorderColor = primary ? Color.FromArgb(38, 106, 181) : Color.FromArgb(202, 212, 222);
            return button;
        }
        private void CopyCommand(object sender, EventArgs eventArgs)
        {
            try { Clipboard.SetText(_command.Text); _status.Text = "✓ 已复制。请到 AI 对话框粘贴并发送。"; }
            catch { _command.Focus(); _command.SelectAll(); _status.Text = "请按 Ctrl+C 复制已选中的测试命令。"; }
        }
        private void HelpClick(object sender, EventArgs eventArgs)
        {
            MessageBox.Show(this, "1. 保持 Revit 项目打开。\r\n\r\n2. 完全退出并重新打开安装时选择的 AI 软件。\r\n\r\n3. 复制测试命令并发送。若 AI 不能返回项目或标高，请重新运行安装包，在“连接应用”中选择该 AI。", "命令桥排查", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        private static string StatusText()
        {
            if (!File.Exists(BridgeFileQueue.StatusFilePath)) return "正在连接 Revit…";
            try
            {
#if REVIT_NET8
                string raw = File.ReadAllText(BridgeFileQueue.StatusFilePath, Encoding.UTF8);
                var status = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(raw);
#else
                var status = new JavaScriptSerializer().DeserializeObject(File.ReadAllText(BridgeFileQueue.StatusFilePath, Encoding.UTF8)) as IDictionary<string, object>;
#endif
                if (status == null) return "Revit 命令桥状态无效。";
                string state = Value(status, "state", "unknown");
                var data = Dictionary(status, "data");
                string document = data == null ? string.Empty : Value(data, "document_title", string.Empty);
                if (string.Equals(state, "running", StringComparison.OrdinalIgnoreCase)) return "✓ Revit 命令桥已连接" + (string.IsNullOrWhiteSpace(document) ? string.Empty : "    当前项目：" + document);
                return "命令桥尚未准备好（" + state + "）。";
            }
            catch { return "暂时无法读取连接状态。请关闭并重新打开 Revit。"; }
        }
        private static string Value(IDictionary<string, object> values, string name, string fallback)
        {
            object value;
            return values != null && values.TryGetValue(name, out value) && value != null ? Convert.ToString(value) ?? fallback : fallback;
        }
        private static IDictionary<string, object> Dictionary(IDictionary<string, object> values, string name)
        {
            object value;
            return values != null && values.TryGetValue(name, out value) ? value as IDictionary<string, object> : null;
        }
    }
    internal static class CommandPanelManager
    {
        private static CommandPanelForm _current;
        public static void Show()
        {
            if (_current == null || _current.IsDisposed) { _current = new CommandPanelForm(); _current.FormClosed += delegate { _current = null; }; }
            _current.Show();
            _current.BringToFront();
            _current.Activate();
        }
        public static void Close()
        {
            if (_current != null && !_current.IsDisposed) _current.Close();
            _current = null;
        }
    }
}
