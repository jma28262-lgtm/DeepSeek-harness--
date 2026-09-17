using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace DeepSeekHarnessLauncher
{
    public enum ServerState { Stopped, Starting, Running, Error }

    public class MainForm : Form
    {
        // ---- single instance ----
        private static Mutex _mutex;
        private const string MUTEX_NAME = "Global\\DeepSeekHarnessLauncher_2026";

        // ---- paths ----
        private string _rootDir;
        private string _startPs1;
        private string _userEnv;
        private string _settingsYaml;
        private string _profileDir;
        private string _portableNode;
        private string _dshCmd;
        private string _port = "3080";

        // ---- process ----
        private Process _dshProcess;
        private ServerState _state = ServerState.Stopped;

        // ---- UI: general ----
        private TabControl _tabs;
        private TabPage _tabConsole;
        private TabPage _tabConfig;
        private TabPage _tabPlugins;
        private System.Windows.Forms.Timer _pollTimer;

        // ---- UI: console tab ----
        private Label _statusDot;
        private Label _statusText;
        private Label _urlLabel;
        private TextBox _logBox;
        private Button _startBtn;
        private Button _stopBtn;
        private Button _openBtn;

        // ---- UI: config tab ----
        private TextBox _apiKeyBox;
        private Button _saveApiBtn;
        private CheckBox _autoStartChk;
        private ComboBox _backendCmb;
        private TextBox _llamaDirBox;
        private TextBox _llamaModelBox;
        private NumericUpDown _llamaPortNum;
        private NumericUpDown _llamaCtxNum;
        private NumericUpDown _llamaGpuNum;
        private TextBox _llamaExtraBox;
        private Button _browseModelBtn;
        private Button _testConnBtn;
        private Button _saveModelBtn;
        private TextBox _ollamaExeBox;
        private TextBox _ollamaModelBox;

        // ---- UI: plugins tab ----
        private ListView _pluginList;
        private TextBox _pluginInstallBox;
        private Button _installPluginBtn;
        private Button _removePluginBtn;
        private Button _refreshPluginBtn;
        private Label _pluginStatus;
        private ListBox _recommendList;

        // ---- tray ----
        private NotifyIcon _trayIcon;
        private ContextMenuStrip _trayMenu;
        private bool _forceClose = false;

        // ---- colors ----
        private static readonly Color BG       = Color.FromArgb(30, 30, 30);
        private static readonly Color PANEL    = Color.FromArgb(45, 45, 48);
        private static readonly Color INPUT_BG = Color.FromArgb(37, 37, 38);
        private static readonly Color TEXT     = Color.FromArgb(220, 220, 220);
        private static readonly Color SUBTEXT  = Color.FromArgb(150, 150, 150);
        private static readonly Color ACCENT   = Color.FromArgb(0, 122, 204);
        private static readonly Color ACCENT_HOVER = Color.FromArgb(0, 140, 230);
        private static readonly Color GREEN    = Color.FromArgb(108, 203, 95);
        private static readonly Color YELLOW   = Color.FromArgb(226, 192, 92);
        private static readonly Color RED      = Color.FromArgb(232, 94, 94);
        private static readonly Color LOG_BG   = Color.FromArgb(20, 20, 20);
        private static readonly Color TAB_SEL  = Color.FromArgb(0, 122, 204);
        private static readonly Color TAB_UNSEL = Color.FromArgb(40, 40, 40);

        // =====================================================================
        //  Entry: single instance check
        // =====================================================================
        [STAThread]
        public static void Main()
        {
            bool createdNew;
            _mutex = new Mutex(true, MUTEX_NAME, out createdNew);
            if (!createdNew)
            {
                // Another instance is running — bring it to front and exit.
                try
                {
                    Process current = Process.GetCurrentProcess();
                    foreach (Process p in Process.GetProcessesByName(current.ProcessName))
                    {
                        if (p.Id != current.Id)
                        {
                            NativeMethods.ShowWindow(p.MainWindowHandle, 9); // SW_RESTORE
                            NativeMethods.SetForegroundWindow(p.MainWindowHandle);
                            break;
                        }
                    }
                }
                catch { }
                return;
            }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
            _mutex.ReleaseMutex();
        }

        public MainForm()
        {
            _rootDir = Path.GetDirectoryName(Application.ExecutablePath);
            if (Path.GetFileName(_rootDir).Equals("bin", StringComparison.OrdinalIgnoreCase))
                _rootDir = Directory.GetParent(_rootDir).FullName;
            if (Path.GetFileName(_rootDir).Equals("launcher", StringComparison.OrdinalIgnoreCase))
                _rootDir = Directory.GetParent(_rootDir).FullName;

            _startPs1     = Path.Combine(_rootDir, "start.ps1");
            _userEnv      = Path.Combine(_rootDir, "config", "user.env");
            _settingsYaml = Path.Combine(_rootDir, "home", "settings.yaml");
            _profileDir   = Path.Combine(_rootDir, "home", "profiles", "web");
            _portableNode = Path.Combine(_rootDir, "tools", "node", "node.exe");
            _dshCmd       = Path.Combine(_rootDir, "tools", "global", "dsh.cmd");

            LoadUserEnv();
            InitializeComponent();
            SetupTray();
            UpdateStateUI();
            LoadConfigIntoUI();

            _pollTimer = new System.Windows.Forms.Timer();
            _pollTimer.Interval = 1500;
            _pollTimer.Tick += PollTimer_Tick;
        }

        // =====================================================================
        //  Tray icon
        // =====================================================================
        private void SetupTray()
        {
            _trayMenu = new ContextMenuStrip();
            _trayMenu.BackColor = PANEL;
            _trayMenu.ForeColor = TEXT;
            _trayMenu.RenderMode = ToolStripRenderMode.System;

            ToolStripMenuItem showItem = new ToolStripMenuItem("显示主窗口");
            showItem.Click += (s, e) => { this.Show(); this.WindowState = FormWindowState.Normal; this.Activate(); };
            _trayMenu.Items.Add(showItem);

            ToolStripMenuItem openItem = new ToolStripMenuItem("打开 Web UI");
            openItem.Click += (s, e) => { if (_state == ServerState.Running) OpenWebUI(); };
            _trayMenu.Items.Add(openItem);

            _trayMenu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem startItem = new ToolStripMenuItem("启动服务");
            startItem.Click += (s, e) => { StartServer(); };
            _trayMenu.Items.Add(startItem);

            ToolStripMenuItem stopItem = new ToolStripMenuItem("停止服务");
            stopItem.Click += (s, e) => { StopServer(); };
            _trayMenu.Items.Add(stopItem);

            _trayMenu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem exitItem = new ToolStripMenuItem("退出");
            exitItem.Click += (s, e) => { _forceClose = true; this.Close(); };
            _trayMenu.Items.Add(exitItem);

            _trayIcon = new NotifyIcon();
            _trayIcon.Text = "DeepSeek Harness";
            _trayIcon.ContextMenuStrip = _trayMenu;
            try { _trayIcon.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
            catch { _trayIcon.Icon = SystemIcons.Application; }
            _trayIcon.Visible = true;
            _trayIcon.MouseClick += TrayIcon_MouseClick;
            _trayIcon.DoubleClick += (s, e) => { this.Show(); this.WindowState = FormWindowState.Normal; this.Activate(); };
        }

        private void TrayIcon_MouseClick(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                if (this.Visible) { this.Hide(); }
                else { this.Show(); this.WindowState = FormWindowState.Normal; this.Activate(); }
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!_forceClose && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                this.Hide();
                _trayIcon.ShowBalloonTip(2000, "DeepSeek Harness", "程序已最小化到托盘，右键托盘图标可退出。", ToolTipIcon.Info);
                return;
            }
            if (_state == ServerState.Running || _state == ServerState.Starting)
            {
                StopServer();
            }
            if (_trayIcon != null) { _trayIcon.Visible = false; _trayIcon.Dispose(); }
            base.OnFormClosing(e);
        }

        // =====================================================================
        //  UI Construction
        // =====================================================================
        private void InitializeComponent()
        {
            this.Text = "DeepSeek Harness 控制台";
            this.Size = new Size(860, 640);
            this.MinimumSize = new Size(720, 520);
            this.BackColor = BG;
            this.Font = new Font("Segoe UI", 9F);
            this.StartPosition = FormStartPosition.CenterScreen;

            _tabs = new TabControl();
            _tabs.Dock = DockStyle.Fill;
            _tabs.BackColor = BG;
            _tabs.ForeColor = TEXT;
            _tabs.Font = new Font("Segoe UI", 9.5F);
            _tabs.Padding = new Point(18, 8);
            _tabs.DrawMode = TabDrawMode.OwnerDrawFixed;
            _tabs.DrawItem += Tabs_DrawItem;

            BuildConsoleTab();
            BuildConfigTab();
            BuildPluginsTab();

            _tabs.TabPages.Add(_tabConsole);
            _tabs.TabPages.Add(_tabConfig);
            _tabs.TabPages.Add(_tabPlugins);

            this.Controls.Add(_tabs);
        }

        private void Tabs_DrawItem(object sender, DrawItemEventArgs e)
        {
            TabPage page = _tabs.TabPages[e.Index];
            Rectangle tabRect = _tabs.GetTabRect(e.Index);
            bool selected = (e.State & DrawItemState.Selected) == DrawItemState.Selected;

            // Background
            using (SolidBrush brush = new SolidBrush(selected ? TAB_SEL : TAB_UNSEL))
            {
                e.Graphics.FillRectangle(brush, tabRect);
            }

            // Text
            TextRenderer.DrawText(e.Graphics, page.Text, _tabs.Font,
                new Rectangle(tabRect.X + 6, tabRect.Y + 4, tabRect.Width - 12, tabRect.Height - 6),
                selected ? Color.White : SUBTEXT,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        }

        private void BuildConsoleTab()
        {
            _tabConsole = new TabPage("控制台");
            _tabConsole.BackColor = BG;
            _tabConsole.Padding = new Padding(0);

            // status bar
            Panel statusBar = new Panel();
            statusBar.Dock = DockStyle.Top;
            statusBar.Height = 48;
            statusBar.BackColor = PANEL;

            _statusDot = new Label();
            _statusDot.Size = new Size(12, 12);
            _statusDot.Location = new Point(16, 18);
            _statusDot.BackColor = RED;

            _statusText = new Label();
            _statusText.Text = "已停止";
            _statusText.ForeColor = TEXT;
            _statusText.Location = new Point(36, 13);
            _statusText.AutoSize = true;
            _statusText.Font = new Font("Segoe UI Semibold", 11F);

            _urlLabel = new Label();
            _urlLabel.Text = "";
            _urlLabel.ForeColor = SUBTEXT;
            _urlLabel.Location = new Point(150, 17);
            _urlLabel.AutoSize = true;
            _urlLabel.Font = new Font("Segoe UI", 9F);

            statusBar.Controls.Add(_statusDot);
            statusBar.Controls.Add(_statusText);
            statusBar.Controls.Add(_urlLabel);

            // log
            _logBox = new TextBox();
            _logBox.Dock = DockStyle.Fill;
            _logBox.Multiline = true;
            _logBox.ScrollBars = ScrollBars.Vertical;
            _logBox.ReadOnly = true;
            _logBox.BackColor = LOG_BG;
            _logBox.ForeColor = Color.FromArgb(180, 180, 180);
            _logBox.Font = new Font("Consolas", 9F);
            _logBox.BorderStyle = BorderStyle.None;
            _logBox.Margin = new Padding(0);

            // button bar
            Panel btnBar = new Panel();
            btnBar.Dock = DockStyle.Bottom;
            btnBar.Height = 56;
            btnBar.BackColor = PANEL;

            _startBtn = MakeButton("启动服务", ACCENT);
            _startBtn.Location = new Point(14, 12);
            _startBtn.Width = 100;
            _startBtn.Click += StartBtn_Click;

            _stopBtn = MakeButton("停止服务", RED);
            _stopBtn.Location = new Point(122, 12);
            _stopBtn.Width = 100;
            _stopBtn.Enabled = false;
            _stopBtn.Click += StopBtn_Click;

            _openBtn = MakeButton("打开 Web UI", Color.FromArgb(60, 60, 60));
            _openBtn.Location = new Point(230, 12);
            _openBtn.Width = 120;
            _openBtn.Enabled = false;
            _openBtn.Click += OpenBtn_Click;

            btnBar.Controls.Add(_startBtn);
            btnBar.Controls.Add(_stopBtn);
            btnBar.Controls.Add(_openBtn);

            _tabConsole.Controls.Add(_logBox);
            _tabConsole.Controls.Add(btnBar);
            _tabConsole.Controls.Add(statusBar);
        }

        private void BuildConfigTab()
        {
            _tabConfig = new TabPage("模型与 API");
            _tabConfig.BackColor = BG;
            _tabConfig.AutoScroll = true;
            _tabConfig.Padding = new Padding(20);

            int y = 0;
            const int LBL_W = 140;
            const int INPUT_W = 440;
            const int ROW_H = 34;

            y = AddSectionHeader(_tabConfig, "DeepSeek 云端 API", y);
            _apiKeyBox = AddLabeledTextBox(_tabConfig, "API Key:", ref y, LBL_W, INPUT_W, true);
            _saveApiBtn = MakeButton("保存", ACCENT);
            _saveApiBtn.Location = new Point(LBL_W + 8, y);
            _saveApiBtn.Width = 100;
            _saveApiBtn.Click += SaveApiBtn_Click;
            _tabConfig.Controls.Add(_saveApiBtn);
            y += ROW_H + 12;

            y = AddSectionHeader(_tabConfig, "本地模型服务自动启动", y);
            _autoStartChk = new CheckBox();
            _autoStartChk.Text = "启动 dsh 前自动拉起本地推理服务（llama.cpp / Ollama）";
            _autoStartChk.ForeColor = TEXT;
            _autoStartChk.BackColor = BG;
            _autoStartChk.Location = new Point(0, y);
            _autoStartChk.AutoSize = true;
            _tabConfig.Controls.Add(_autoStartChk);
            y += ROW_H;

            AddLabel(_tabConfig, "推理后端:", 0, y, LBL_W);
            _backendCmb = new ComboBox();
            _backendCmb.DropDownStyle = ComboBoxStyle.DropDownList;
            _backendCmb.Items.AddRange(new object[] { "llama.cpp", "Ollama" });
            _backendCmb.Location = new Point(LBL_W + 8, y);
            _backendCmb.Size = new Size(180, 28);
            _backendCmb.BackColor = INPUT_BG;
            _backendCmb.ForeColor = TEXT;
            _backendCmb.FlatStyle = FlatStyle.Flat;
            _tabConfig.Controls.Add(_backendCmb);
            y += ROW_H + 8;

            y = AddSectionHeader(_tabConfig, "llama.cpp 配置", y);
            _llamaDirBox = AddLabeledTextBox(_tabConfig, "llama.cpp 目录:", ref y, LBL_W, INPUT_W, false);
            _llamaModelBox = AddLabeledTextBox(_tabConfig, "模型文件 (GGUF):", ref y, LBL_W, INPUT_W - 44, false);
            _browseModelBtn = MakeButton("...", Color.FromArgb(60, 60, 60));
            _browseModelBtn.Size = new Size(36, 28);
            _browseModelBtn.Location = new Point(LBL_W + 8 + INPUT_W - 44 + 4, y - ROW_H + 3);
            _browseModelBtn.Click += BrowseModelBtn_Click;
            _tabConfig.Controls.Add(_browseModelBtn);

            AddLabel(_tabConfig, "端口:", 0, y, LBL_W);
            _llamaPortNum = MakeNum(11435, 1, 65535);
            _llamaPortNum.Location = new Point(LBL_W + 8, y);
            _tabConfig.Controls.Add(_llamaPortNum);
            y += ROW_H;

            AddLabel(_tabConfig, "上下文长度:", 0, y, LBL_W);
            _llamaCtxNum = MakeNum(4096, 512, 131072);
            _llamaCtxNum.Location = new Point(LBL_W + 8, y);
            _tabConfig.Controls.Add(_llamaCtxNum);
            y += ROW_H;

            AddLabel(_tabConfig, "GPU 卸载层数:", 0, y, LBL_W);
            _llamaGpuNum = MakeNum(99, 0, 999);
            _llamaGpuNum.Location = new Point(LBL_W + 8, y);
            _tabConfig.Controls.Add(_llamaGpuNum);
            y += ROW_H;

            _llamaExtraBox = AddLabeledTextBox(_tabConfig, "额外参数:", ref y, LBL_W, INPUT_W, false);

            _testConnBtn = MakeButton("测试连接", Color.FromArgb(60, 60, 60));
            _testConnBtn.Location = new Point(LBL_W + 8, y);
            _testConnBtn.Width = 100;
            _testConnBtn.Click += TestConnBtn_Click;
            _tabConfig.Controls.Add(_testConnBtn);

            _saveModelBtn = MakeButton("保存配置", ACCENT);
            _saveModelBtn.Location = new Point(LBL_W + 116, y);
            _saveModelBtn.Width = 120;
            _saveModelBtn.Click += SaveModelBtn_Click;
            _tabConfig.Controls.Add(_saveModelBtn);
            y += ROW_H + 16;

            y = AddSectionHeader(_tabConfig, "Ollama 配置", y);
            _ollamaExeBox = AddLabeledTextBox(_tabConfig, "ollama.exe 路径:", ref y, LBL_W, INPUT_W, false);
            _ollamaModelBox = AddLabeledTextBox(_tabConfig, "预加载模型名:", ref y, LBL_W, INPUT_W, false);
            y += 8;

            Label hint = new Label();
            hint.Text = "提示：修改配置后需重启 dsh 生效。模型服务日志见 logs\\ 目录。";
            hint.ForeColor = SUBTEXT;
            hint.Location = new Point(0, y);
            hint.Size = new Size(580, 30);
            _tabConfig.Controls.Add(hint);
        }

        private void BuildPluginsTab()
        {
            _tabPlugins = new TabPage("插件管理");
            _tabPlugins.BackColor = BG;
            _tabPlugins.Padding = new Padding(14);

            Label title = new Label();
            title.Text = "已安装插件（web profile）";
            title.ForeColor = TEXT;
            title.Font = new Font("Segoe UI Semibold", 11F);
            title.Location = new Point(14, 10);
            title.AutoSize = true;
            _tabPlugins.Controls.Add(title);

            _pluginList = new ListView();
            _pluginList.Location = new Point(14, 38);
            _pluginList.Size = new Size(500, 380);
            _pluginList.View = View.Details;
            _pluginList.FullRowSelect = true;
            _pluginList.BackColor = INPUT_BG;
            _pluginList.ForeColor = TEXT;
            _pluginList.BorderStyle = BorderStyle.FixedSingle;
            _pluginList.Columns.Add("插件包名", 260);
            _pluginList.Columns.Add("版本", 100);
            _pluginList.Columns.Add("类型", 120);
            _tabPlugins.Controls.Add(_pluginList);

            // Recommended plugins panel
            Label recLabel = new Label();
            recLabel.Text = "社区推荐插件";
            recLabel.ForeColor = TEXT;
            recLabel.Font = new Font("Segoe UI Semibold", 10F);
            recLabel.Location = new Point(530, 10);
            recLabel.AutoSize = true;
            _tabPlugins.Controls.Add(recLabel);

            _recommendList = new ListBox();
            _recommendList.Location = new Point(530, 38);
            _recommendList.Size = new Size(280, 200);
            _recommendList.BackColor = INPUT_BG;
            _recommendList.ForeColor = TEXT;
            _recommendList.BorderStyle = BorderStyle.FixedSingle;
            _recommendList.Font = new Font("Consolas", 8.5F);
            _recommendList.Items.AddRange(new object[]
            {
                "@deepseek-ai/dsh-better-sidebar",
                "@deepseek-ai/dsh-openai-bridge",
                "@deepseek-ai/dsh-plugin-mcp",
                "@deepseek-ai/dsh-plugin-git",
                "dsh-plugin-web-search",
                "dsh-plugin-terminal"
            });
            _recommendList.DoubleClick += (s, e) =>
            {
                if (_recommendList.SelectedItem != null)
                    _pluginInstallBox.Text = _recommendList.SelectedItem.ToString();
            };
            _tabPlugins.Controls.Add(_recommendList);

            Label recHint = new Label();
            recHint.Text = "双击填入安装框，然后点安装";
            recHint.ForeColor = SUBTEXT;
            recHint.Location = new Point(530, 242);
            recHint.AutoSize = true;
            recHint.Font = new Font("Segoe UI", 8F);
            _tabPlugins.Controls.Add(recHint);

            // install row
            Label instLbl = new Label();
            instLbl.Text = "安装插件（npm 包名）：";
            instLbl.ForeColor = TEXT;
            instLbl.Location = new Point(14, 432);
            instLbl.AutoSize = true;
            _tabPlugins.Controls.Add(instLbl);

            _pluginInstallBox = new TextBox();
            _pluginInstallBox.Location = new Point(14, 454);
            _pluginInstallBox.Size = new Size(420, 28);
            _pluginInstallBox.BackColor = INPUT_BG;
            _pluginInstallBox.ForeColor = TEXT;
            _pluginInstallBox.BorderStyle = BorderStyle.FixedSingle;
            _pluginInstallBox.Font = new Font("Consolas", 9F);
            _tabPlugins.Controls.Add(_pluginInstallBox);

            _installPluginBtn = MakeButton("安装", ACCENT);
            _installPluginBtn.Location = new Point(444, 452);
            _installPluginBtn.Width = 80;
            _installPluginBtn.Click += InstallPluginBtn_Click;
            _tabPlugins.Controls.Add(_installPluginBtn);

            _removePluginBtn = MakeButton("卸载选中", RED);
            _removePluginBtn.Location = new Point(534, 452);
            _removePluginBtn.Width = 100;
            _removePluginBtn.Click += RemovePluginBtn_Click;
            _tabPlugins.Controls.Add(_removePluginBtn);

            _refreshPluginBtn = MakeButton("刷新", Color.FromArgb(60, 60, 60));
            _refreshPluginBtn.Location = new Point(644, 452);
            _refreshPluginBtn.Width = 80;
            _refreshPluginBtn.Click += RefreshPluginBtn_Click;
            _tabPlugins.Controls.Add(_refreshPluginBtn);

            _pluginStatus = new Label();
            _pluginStatus.Text = "";
            _pluginStatus.ForeColor = SUBTEXT;
            _pluginStatus.Location = new Point(14, 490);
            _pluginStatus.Size = new Size(780, 24);
            _tabPlugins.Controls.Add(_pluginStatus);
        }

        // ---- UI helpers ----
        private Button MakeButton(string text, Color bg)
        {
            Button b = new Button();
            b.Text = text;
            b.BackColor = bg;
            b.ForeColor = Color.White;
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderSize = 0;
            b.FlatAppearance.MouseOverBackColor = ControlPaint.Light(bg, 0.15f);
            b.Cursor = Cursors.Hand;
            b.Size = new Size(90, 32);
            b.Font = new Font("Segoe UI Semibold", 9F);
            return b;
        }

        private NumericUpDown MakeNum(int val, int min, int max)
        {
            NumericUpDown n = new NumericUpDown();
            n.Minimum = min; n.Maximum = max; n.Value = val;
            n.Size = new Size(100, 28);
            n.BackColor = INPUT_BG; n.ForeColor = TEXT;
            n.BorderStyle = BorderStyle.FixedSingle;
            return n;
        }

        private int AddSectionHeader(Control parent, string text, int y)
        {
            Label lbl = new Label();
            lbl.Text = text;
            lbl.ForeColor = ACCENT;
            lbl.Font = new Font("Segoe UI Semibold", 10.5F);
            lbl.Location = new Point(0, y);
            lbl.AutoSize = true;
            parent.Controls.Add(lbl);
            return y + 32;
        }

        private void AddLabel(Control parent, string text, int x, int y, int w)
        {
            Label lbl = new Label();
            lbl.Text = text;
            lbl.ForeColor = TEXT;
            lbl.Location = new Point(x, y + 5);
            lbl.Size = new Size(w, 24);
            lbl.TextAlign = ContentAlignment.MiddleRight;
            parent.Controls.Add(lbl);
        }

        private TextBox AddLabeledTextBox(Control parent, string label, ref int y, int lblW, int inputW, bool password)
        {
            AddLabel(parent, label, 0, y, lblW);
            TextBox tb = new TextBox();
            tb.Location = new Point(lblW + 8, y);
            tb.Size = new Size(inputW, 28);
            tb.BackColor = INPUT_BG;
            tb.ForeColor = TEXT;
            tb.BorderStyle = BorderStyle.FixedSingle;
            if (password) tb.UseSystemPasswordChar = true;
            parent.Controls.Add(tb);
            y += 34;
            return tb;
        }

        // =====================================================================
        //  Config file I/O
        // =====================================================================
        private Dictionary<string, string> ReadUserEnv()
        {
            Dictionary<string, string> dict = new Dictionary<string, string>();
            try
            {
                if (File.Exists(_userEnv))
                {
                    foreach (string line in File.ReadAllLines(_userEnv))
                    {
                        string t = line.Trim();
                        if (t.Length == 0 || t.StartsWith("#")) continue;
                        int idx = t.IndexOf('=');
                        if (idx > 0)
                        {
                            string k = t.Substring(0, idx).Trim();
                            string v = t.Substring(idx + 1).Trim().Trim('"');
                            dict[k] = v;
                        }
                    }
                }
            }
            catch { }
            return dict;
        }

        private void WriteUserEnv(Dictionary<string, string> values)
        {
            List<string> lines = new List<string>();
            HashSet<string> seen = new HashSet<string>();
            if (File.Exists(_userEnv))
            {
                foreach (string line in File.ReadAllLines(_userEnv))
                {
                    string t = line.Trim();
                    if (t.Length > 0 && !t.StartsWith("#"))
                    {
                        int idx = t.IndexOf('=');
                        if (idx > 0)
                        {
                            string k = t.Substring(0, idx).Trim();
                            if (values.ContainsKey(k))
                            {
                                lines.Add(k + "=" + values[k]);
                                seen.Add(k);
                                continue;
                            }
                        }
                    }
                    lines.Add(line);
                }
            }
            foreach (KeyValuePair<string, string> kv in values)
            {
                if (!seen.Contains(kv.Key))
                    lines.Add(kv.Key + "=" + kv.Value);
            }
            File.WriteAllLines(_userEnv, lines, Encoding.UTF8);
        }

        private void LoadUserEnv()
        {
            Dictionary<string, string> env = ReadUserEnv();
            if (env.ContainsKey("DSH_PORT") && env["DSH_PORT"].Length > 0)
                _port = env["DSH_PORT"];
        }

        private void LoadConfigIntoUI()
        {
            Dictionary<string, string> env = ReadUserEnv();
            if (env.ContainsKey("DEEPSEEK_API_KEY")) _apiKeyBox.Text = env["DEEPSEEK_API_KEY"];
            _autoStartChk.Checked = env.ContainsKey("DSH_AUTO_START_MODEL") && env["DSH_AUTO_START_MODEL"] == "1";
            if (env.ContainsKey("DSH_MODEL_BACKEND")) _backendCmb.SelectedItem = env["DSH_MODEL_BACKEND"];
            else _backendCmb.SelectedIndex = 0;
            if (env.ContainsKey("DSH_LLAMA_DIR")) _llamaDirBox.Text = env["DSH_LLAMA_DIR"];
            if (env.ContainsKey("DSH_LLAMA_MODEL")) _llamaModelBox.Text = env["DSH_LLAMA_MODEL"];
            if (env.ContainsKey("DSH_LLAMA_PORT")) { int p; if (int.TryParse(env["DSH_LLAMA_PORT"], out p)) _llamaPortNum.Value = p; }
            if (env.ContainsKey("DSH_LLAMA_CONTEXT")) { int c; if (int.TryParse(env["DSH_LLAMA_CONTEXT"], out c)) _llamaCtxNum.Value = c; }
            if (env.ContainsKey("DSH_LLAMA_GPU_LAYERS")) { int g; if (int.TryParse(env["DSH_LLAMA_GPU_LAYERS"], out g)) _llamaGpuNum.Value = g; }
            if (env.ContainsKey("DSH_LLAMA_EXTRA_ARGS")) _llamaExtraBox.Text = env["DSH_LLAMA_EXTRA_ARGS"];
            if (env.ContainsKey("DSH_OLLAMA_EXE")) _ollamaExeBox.Text = env["DSH_OLLAMA_EXE"];
            if (env.ContainsKey("DSH_OLLAMA_MODEL")) _ollamaModelBox.Text = env["DSH_OLLAMA_MODEL"];
        }

        // =====================================================================
        //  State / Log
        // =====================================================================
        private void UpdateStateUI()
        {
            if (this.InvokeRequired) { this.BeginInvoke(new Action(UpdateStateUI)); return; }
            switch (_state)
            {
                case ServerState.Stopped:
                    _statusDot.BackColor = RED; _statusText.Text = "已停止"; _urlLabel.Text = "";
                    _startBtn.Enabled = true; _stopBtn.Enabled = false; _openBtn.Enabled = false;
                    break;
                case ServerState.Starting:
                    _statusDot.BackColor = YELLOW; _statusText.Text = "启动中..."; _urlLabel.Text = "等待 " + GetUrl();
                    _startBtn.Enabled = false; _stopBtn.Enabled = true; _openBtn.Enabled = false;
                    break;
                case ServerState.Running:
                    _statusDot.BackColor = GREEN; _statusText.Text = "运行中"; _urlLabel.Text = GetUrl();
                    _startBtn.Enabled = false; _stopBtn.Enabled = true; _openBtn.Enabled = true;
                    break;
                case ServerState.Error:
                    _statusDot.BackColor = RED; _statusText.Text = "错误"; _urlLabel.Text = "";
                    _startBtn.Enabled = true; _stopBtn.Enabled = false; _openBtn.Enabled = false;
                    break;
            }
            if (_trayIcon != null)
            {
                string stateText = "";
                switch (_state)
                {
                    case ServerState.Stopped: stateText = "已停止"; break;
                    case ServerState.Starting: stateText = "启动中"; break;
                    case ServerState.Running: stateText = "运行中 - " + GetUrl(); break;
                    case ServerState.Error: stateText = "错误"; break;
                }
                _trayIcon.Text = "DeepSeek Harness - " + stateText;
            }
        }

        private void AppendLog(string msg)
        {
            if (this.InvokeRequired) { this.BeginInvoke(new Action<string>(AppendLog), msg); return; }
            string ts = DateTime.Now.ToString("HH:mm:ss");
            _logBox.AppendText("[" + ts + "] " + msg + Environment.NewLine);
            _logBox.ScrollToCaret();
        }

        private string GetUrl() { return "http://127.0.0.1:" + _port; }

        // =====================================================================
        //  Server Start / Stop
        // =====================================================================
        private void StartBtn_Click(object sender, EventArgs e) { StartServer(); }

        private void StartServer()
        {
            if (_state == ServerState.Starting || _state == ServerState.Running) return;
            if (!File.Exists(_startPs1))
            {
                AppendLog("错误: 找不到 start.ps1 (" + _startPs1 + ")");
                _state = ServerState.Error; UpdateStateUI(); return;
            }

            LoadUserEnv();
            _state = ServerState.Starting; UpdateStateUI();
            AppendLog("正在启动 DeepSeek Harness...");
            AppendLog("根目录: " + _rootDir);

            ProcessStartInfo psi = new ProcessStartInfo();
            psi.FileName = "powershell.exe";
            psi.Arguments = "-NoProfile -ExecutionPolicy Bypass -File \"" + _startPs1 + "\"";
            psi.WorkingDirectory = _rootDir;
            psi.UseShellExecute = false;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.CreateNoWindow = true;
            psi.StandardOutputEncoding = Encoding.UTF8;
            psi.StandardErrorEncoding = Encoding.UTF8;

            _dshProcess = new Process();
            _dshProcess.StartInfo = psi;
            _dshProcess.EnableRaisingEvents = true;
            _dshProcess.OutputDataReceived += (s, ev) => { if (ev.Data != null) AppendLog(ev.Data); };
            _dshProcess.ErrorDataReceived += (s, ev) => { if (ev.Data != null) AppendLog("ERR: " + ev.Data); };
            _dshProcess.Exited += (s, ev) =>
            {
                int code = _dshProcess.HasExited ? _dshProcess.ExitCode : -1;
                AppendLog("进程退出 (code " + code + ")");
                if (_state == ServerState.Starting || _state == ServerState.Running)
                {
                    _state = (code == 0) ? ServerState.Stopped : ServerState.Error;
                    _pollTimer.Stop();
                    UpdateStateUI();
                }
            };

            try
            {
                _dshProcess.Start();
                _dshProcess.BeginOutputReadLine();
                _dshProcess.BeginErrorReadLine();
                _pollTimer.Start();
                AppendLog("进程已启动 (PID " + _dshProcess.Id + ")，等待服务就绪...");
            }
            catch (Exception ex)
            {
                AppendLog("启动失败: " + ex.Message);
                _state = ServerState.Error; UpdateStateUI();
            }
        }

        private void StopBtn_Click(object sender, EventArgs e) { StopServer(); }

        private void StopServer()
        {
            _pollTimer.Stop();
            if (_dshProcess != null && !_dshProcess.HasExited)
            {
                AppendLog("正在停止服务（终止进程树）...");
                try
                {
                    ProcessStartInfo psi = new ProcessStartInfo();
                    psi.FileName = "taskkill.exe";
                    psi.Arguments = "/PID " + _dshProcess.Id + " /T /F";
                    psi.UseShellExecute = false; psi.CreateNoWindow = true;
                    psi.RedirectStandardOutput = true;
                    Process killer = Process.Start(psi);
                    killer.WaitForExit(5000);
                }
                catch (Exception ex)
                {
                    AppendLog("taskkill 错误: " + ex.Message);
                    try { _dshProcess.Kill(); } catch { }
                }
            }
            _state = ServerState.Stopped; UpdateStateUI();
            AppendLog("服务已停止。");
        }

        private void PollTimer_Tick(object sender, EventArgs e) { Task.Run(() => CheckServer()); }

        private void CheckServer()
        {
            try
            {
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(GetUrl());
                req.Timeout = 2000; req.ReadWriteTimeout = 2000;
                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                {
                    if (resp.StatusCode == HttpStatusCode.OK && _state == ServerState.Starting)
                    {
                        _state = ServerState.Running; UpdateStateUI();
                        AppendLog("服务已就绪! " + GetUrl());
                    }
                }
            }
            catch { }
        }

        private void OpenBtn_Click(object sender, EventArgs e) { OpenWebUI(); }

        private void OpenWebUI()
        {
            try { Process.Start(GetUrl()); AppendLog("已在默认浏览器打开 " + GetUrl()); }
            catch (Exception ex) { AppendLog("打开浏览器失败: " + ex.Message); }
        }

        // =====================================================================
        //  Config tab actions
        // =====================================================================
        private void SaveApiBtn_Click(object sender, EventArgs e)
        {
            Dictionary<string, string> vals = new Dictionary<string, string>();
            vals["DEEPSEEK_API_KEY"] = _apiKeyBox.Text.Trim();
            WriteUserEnv(vals);
            AppendLog("API Key 已保存到 user.env。重启 dsh 后生效。");
            MessageBox.Show("API Key 已保存。重启 dsh 后生效。", "保存成功", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void SaveModelBtn_Click(object sender, EventArgs e)
        {
            Dictionary<string, string> vals = new Dictionary<string, string>();
            vals["DSH_AUTO_START_MODEL"] = _autoStartChk.Checked ? "1" : "0";
            vals["DSH_MODEL_BACKEND"] = _backendCmb.SelectedItem != null ? _backendCmb.SelectedItem.ToString() : "llama-cpp";
            vals["DSH_LLAMA_DIR"] = _llamaDirBox.Text.Trim();
            vals["DSH_LLAMA_MODEL"] = _llamaModelBox.Text.Trim();
            vals["DSH_LLAMA_PORT"] = ((int)_llamaPortNum.Value).ToString();
            vals["DSH_LLAMA_CONTEXT"] = ((int)_llamaCtxNum.Value).ToString();
            vals["DSH_LLAMA_GPU_LAYERS"] = ((int)_llamaGpuNum.Value).ToString();
            vals["DSH_LLAMA_EXTRA_ARGS"] = _llamaExtraBox.Text.Trim();
            vals["DSH_OLLAMA_EXE"] = _ollamaExeBox.Text.Trim();
            vals["DSH_OLLAMA_MODEL"] = _ollamaModelBox.Text.Trim();
            WriteUserEnv(vals);
            AppendLog("模型配置已保存。重启 dsh 后生效。");
            MessageBox.Show("模型配置已保存。重启 dsh 后生效。", "保存成功", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void BrowseModelBtn_Click(object sender, EventArgs e)
        {
            OpenFileDialog dlg = new OpenFileDialog();
            dlg.Filter = "GGUF 模型文件 (*.gguf)|*.gguf|所有文件 (*.*)|*.*";
            dlg.Title = "选择 GGUF 模型文件";
            if (Directory.Exists(_llamaDirBox.Text)) dlg.InitialDirectory = _llamaDirBox.Text;
            if (dlg.ShowDialog() == DialogResult.OK)
                _llamaModelBox.Text = dlg.FileName;
        }

        private void TestConnBtn_Click(object sender, EventArgs e)
        {
            int port = (int)_llamaPortNum.Value;
            string url = "http://127.0.0.1:" + port + "/v1/models";
            AppendLog("测试连接: " + url);
            Task.Run(() =>
            {
                try
                {
                    HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
                    req.Timeout = 3000;
                    using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                    {
                        if (resp.StatusCode == HttpStatusCode.OK)
                            AppendLog("连接成功! 服务正在运行。");
                        else
                            AppendLog("连接返回状态: " + resp.StatusCode);
                    }
                }
                catch (Exception ex)
                {
                    AppendLog("连接失败: " + ex.Message + " （服务未启动？）");
                }
            });
        }

        // =====================================================================
        //  Plugin management
        // =====================================================================
        private void RefreshPluginBtn_Click(object sender, EventArgs e) { RefreshPlugins(); }

        private void RefreshPlugins()
        {
            _pluginList.Items.Clear();
            _pluginStatus.Text = "正在刷新...";
            AppendLog("刷新插件列表...");

            string pkgJson = Path.Combine(_profileDir, "package.json");
            if (File.Exists(pkgJson))
            {
                try
                {
                    string json = File.ReadAllText(pkgJson);
                    int depIdx = json.IndexOf("\"dependencies\"");
                    if (depIdx >= 0)
                    {
                        int braceStart = json.IndexOf('{', depIdx);
                        int braceEnd = FindMatchingBrace(json, braceStart);
                        if (braceEnd > braceStart)
                        {
                            string deps = json.Substring(braceStart + 1, braceEnd - braceStart - 1);
                            MatchCollection matches = Regex.Matches(deps, "\"([^\"]+)\"\\s*:\\s*\"([^\"]+)\"");
                            foreach (Match m in matches)
                            {
                                string name = m.Groups[1].Value;
                                string ver = m.Groups[2].Value;
                                string type = name.StartsWith("@deepseek-ai/") ? "官方" : "社区";
                                ListViewItem item = new ListViewItem(new string[] { name, ver, type });
                                _pluginList.Items.Add(item);
                            }
                        }
                    }
                    Match bundleMatch = Regex.Match(json, "\"bundles\"\\s*:\\s*\\[([^\\]]+)\\]");
                    if (bundleMatch.Success)
                    {
                        MatchCollection bMatches = Regex.Matches(bundleMatch.Groups[1].Value, "\"([^\"]+)\"");
                        foreach (Match m in bMatches)
                        {
                            string name = m.Groups[1].Value;
                            ListViewItem item = new ListViewItem(new string[] { name, "(内置 bundle)", "核心" });
                            _pluginList.Items.Add(item);
                        }
                    }
                }
                catch (Exception ex) { AppendLog("读取插件列表失败: " + ex.Message); }
            }

            _pluginStatus.Text = "共 " + _pluginList.Items.Count + " 个插件/模块";
            AppendLog("插件列表已刷新 (" + _pluginList.Items.Count + " 项)");
        }

        private int FindMatchingBrace(string s, int start)
        {
            int depth = 0;
            for (int i = start; i < s.Length; i++)
            {
                if (s[i] == '{') depth++;
                else if (s[i] == '}') { depth--; if (depth == 0) return i; }
            }
            return -1;
        }

        private void InstallPluginBtn_Click(object sender, EventArgs e)
        {
            string pkg = _pluginInstallBox.Text.Trim();
            if (pkg.Length == 0)
            {
                MessageBox.Show("请输入插件包名，或从右侧推荐列表双击选择", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            _pluginStatus.Text = "正在安装 " + pkg + " ...";
            AppendLog("安装插件: " + pkg);
            _installPluginBtn.Enabled = false;

            Task.Run(() =>
            {
                try
                {
                    string result = RunDshPlugin("add", pkg);
                    AppendLog(result);
                    this.BeginInvoke(new Action(() =>
                    {
                        _pluginInstallBox.Text = "";
                        RefreshPlugins();
                        _installPluginBtn.Enabled = true;
                    }));
                }
                catch (Exception ex)
                {
                    AppendLog("安装失败: " + ex.Message);
                    this.BeginInvoke(new Action(() => { _installPluginBtn.Enabled = true; _pluginStatus.Text = "安装失败"; }));
                }
            });
        }

        private void RemovePluginBtn_Click(object sender, EventArgs e)
        {
            if (_pluginList.SelectedItems.Count == 0)
            {
                MessageBox.Show("请先选中要卸载的插件", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            string pkg = _pluginList.SelectedItems[0].Text;
            if (pkg.Contains("dsh-base") || pkg.Contains("dsh-web-app"))
            {
                MessageBox.Show("核心 bundle 不可卸载", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (MessageBox.Show("确认卸载插件 " + pkg + " ?", "确认", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;

            _pluginStatus.Text = "正在卸载 " + pkg + " ...";
            AppendLog("卸载插件: " + pkg);
            _removePluginBtn.Enabled = false;

            Task.Run(() =>
            {
                try
                {
                    string result = RunDshPlugin("remove", pkg);
                    AppendLog(result);
                    this.BeginInvoke(new Action(() => { RefreshPlugins(); _removePluginBtn.Enabled = true; }));
                }
                catch (Exception ex)
                {
                    AppendLog("卸载失败: " + ex.Message);
                    this.BeginInvoke(new Action(() => { _removePluginBtn.Enabled = true; _pluginStatus.Text = "卸载失败"; }));
                }
            });
        }

        private string RunDshPlugin(string command, string pkg)
        {
            ProcessStartInfo psi = new ProcessStartInfo();
            psi.FileName = "cmd.exe";
            psi.Arguments = "/c \"" +
                "set PATH=" + Path.GetDirectoryName(_portableNode) + ";" + Path.GetDirectoryName(_dshCmd) + ";%PATH%&& " +
                "set DSH_HOME=" + Path.Combine(_rootDir, "home") + "&& " +
                "set npm_config_cache=" + Path.Combine(_rootDir, "tools", "npm-cache") + "&& " +
                "\"" + _dshCmd + "\" plugin --profile web " + command + " \"" + pkg + "\"" +
                "\"";
            psi.WorkingDirectory = _profileDir;
            psi.UseShellExecute = false;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.CreateNoWindow = true;
            psi.StandardOutputEncoding = Encoding.UTF8;
            psi.StandardErrorEncoding = Encoding.UTF8;

            Process p = Process.Start(psi);
            string output = p.StandardOutput.ReadToEnd();
            string error = p.StandardError.ReadToEnd();
            p.WaitForExit(180000);
            if (error.Length > 0) output += Environment.NewLine + error;
            return output.Length > 0 ? output : "(命令执行完成，无输出)";
        }

        // =====================================================================
        //  Form lifecycle
        // =====================================================================
        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            AppendLog("DeepSeek Harness 控制台已就绪。");
            AppendLog("根目录: " + _rootDir);
            AppendLog("Web UI: " + GetUrl());
            AppendLog("提示：关闭窗口会最小化到托盘，右键托盘图标可退出。");
            RefreshPlugins();
            // Auto-start after short delay
            System.Windows.Forms.Timer autoStart = new System.Windows.Forms.Timer();
            autoStart.Interval = 1000;
            autoStart.Tick += (s, ev) => { autoStart.Stop(); StartServer(); };
            autoStart.Start();
        }
    }

    // P/Invoke for bringing another instance to front
    internal static class NativeMethods
    {
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr hWnd);
    }
}
