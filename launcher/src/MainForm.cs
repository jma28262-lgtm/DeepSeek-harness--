using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace DeepSeekHarnessLauncher
{
    public class MainForm : Form
    {
        // ---- 单实例 ----
        private static System.Threading.Mutex _mutex;
        private const string MUTEX_NAME = "Global\\DeepSeekHarnessLauncher_2026_v3";

        // ---- 全局服务 ----
        public static DshService Dsh;
        public static LocalModelService Models;
        public static ConfigManager Cfg;
        public static ThemeStore Themes;
        public static PluginService Plugins;

        // ---- 启动期一次性任务的报告（凭据迁移等），供 UI 展示 ----
        public static string[] StartupNotes = new string[0];

        // ---- WebView2 UI ----
        private WebView2 _web;
        private NativeBridge _bridge;

        // ---- 托盘 ----
        private NotifyIcon _trayIcon;
        private ContextMenuStrip _trayMenu;
        private bool _forceClose = false;

        // =====================================================================
        //  Entry
        // =====================================================================
        [STAThread]
        public static void Main()
        {
            // 进程 DPI 感知由 app.manifest（PerMonitorV2）声明，此处不再调用 API
            bool createdNew;
            _mutex = new System.Threading.Mutex(true, MUTEX_NAME, out createdNew);
            if (!createdNew)
            {
                try
                {
                    int cur = Process.GetCurrentProcess().Id;
                    foreach (var proc in Process.GetProcessesByName("DeepSeekHarness"))
                    {
                        if (proc.Id != cur)
                        {
                            NativeMethods.ShowWindow(proc.MainWindowHandle, 9);
                            NativeMethods.SetForegroundWindow(proc.MainWindowHandle);
                            break;
                        }
                    }
                }
                catch { }
                return;
            }

            // 初始化路径与配置
            string root = Path.GetDirectoryName(Application.ExecutablePath);
            if (Path.GetFileName(root).Equals("bin", StringComparison.OrdinalIgnoreCase))
                root = Directory.GetParent(root).FullName;
            if (Path.GetFileName(root).Equals("launcher", StringComparison.OrdinalIgnoreCase))
                root = Directory.GetParent(root).FullName;
            Paths.Init(root);
            Paths.EnsureDirs();

            // 崩溃/关闭日志：帮助定位进程意外退出的原因
            string crashLog = Path.Combine(Paths.ConfigDir, "crash.log");
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                try { File.AppendAllText(crashLog, DateTime.Now.ToString("HH:mm:ss") + " [unhandled] " + e.ExceptionObject + "\n"); } catch { }
            };
            Application.ThreadException += (s, e) =>
            {
                try { File.AppendAllText(crashLog, DateTime.Now.ToString("HH:mm:ss") + " [thread] " + e.Exception + "\n"); } catch { }
            };
            Application.ApplicationExit += (s, e) =>
            {
                try { File.AppendAllText(crashLog, DateTime.Now.ToString("HH:mm:ss") + " [appexit]\n"); } catch { }
            };
            AppDomain.CurrentDomain.ProcessExit += (s, e) =>
            {
                try { File.AppendAllText(crashLog, DateTime.Now.ToString("HH:mm:ss") + " [processexit]\n"); } catch { }
            };

            Cfg = new ConfigManager(Paths.UserEnv);
            Themes = new ThemeStore(Path.Combine(Paths.ConfigDir, "theme.json"));
            Dsh = new DshService();
            Models = new LocalModelService();
            Plugins = new PluginService();

            // 凭据迁移：把历史明文（config\user.env、home\.credentials.yaml）收进 DPAPI 加密库并抹掉明文。
            // 幂等；报告留给「环境配置」页展示，让用户确认密钥确实被移走了。
            try { StartupNotes = SecretMigration.Run(); }
            catch (Exception ex) { StartupNotes = new string[] { "凭据迁移异常：" + ex.Message }; }

            // 子进程托管：把自身放进 Job Object。此后派生的 node / powershell /
            // llama-server / ollama 全部继承 Job，退出时由内核一并终止 ——
            // 这是解决"关掉启动器后模型服务仍占着显存"的唯一可靠办法。
            ProcessSupervisor.Init();
            ProcessSupervisor.AdoptSelf();

            // 端口：优先使用配置，未配置或被占用则自动探测
            int p;
            if (int.TryParse(Cfg.Get("DSH_PORT", "0"), out p) && p >= 1024)
            {
                if (!Util.IsPortListening(p)) Dsh.Port = p;
                else { Dsh.Port = DshService.DetectAvailablePort(p); Cfg.Set("DSH_PORT", Dsh.Port.ToString()); Cfg.Save(); }
            }
            else
            {
                Dsh.Port = DshService.DetectAvailablePort(3080);
            }
            int lp;
            if (int.TryParse(Cfg.Get("DSH_LLAMA_PORT", "11435"), out lp) && lp >= 1024) Models.LlamaPort = lp;

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
            _mutex.ReleaseMutex();
        }

        public MainForm()
        {
            BuildWindow();
            BuildTray();
            // WebView2 控件在 InitWeb()（OnShown 后）创建一次，避免重复控件叠加
        }

        private void BuildWindow()
        {
            this.Text = "DeepSeek Harness 桌面工作台";
            this.FormBorderStyle = FormBorderStyle.None;   // 无边框，标题栏由前端实现
            this.AutoScaleMode = AutoScaleMode.None;       // 尺寸由 manifest/app.config(PerMonitorV2) 统一处理
            this.StartPosition = FormStartPosition.CenterScreen;
            // 初始尺寸：默认 1800x1140（物理像素，WebView2 CSS 视口 1200x760，与前端设计一致），
            // 并按屏幕工作区收边适配，避免超出屏幕；最小 1200x760（与 CSS 设计稿一致，可自由拉伸）
            this.MinimumSize = new Size(1200, 760);
            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            int w = Math.Min(1800, wa.Width - 24);
            int h = Math.Min(1140, wa.Height - 24);
            if (w < 1200) w = 1200;
            if (h < 760) h = 760;
            this.Size = new Size(w, h);
            this.BackColor = Color.FromArgb(15, 17, 26);
            this.DoubleBuffered = true;
        }

        // 无边框窗口：窗口拖动/缩放由前端 pointer 检测 + 后台线程 SetWindowPos 手动循环完成（见下），
        // 不设置 WS_THICKFRAME，避免系统把边缘当非客户区拦截 WebView2 鼠标（导致前端循环收不到）。
        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                return cp;
            }
        }

        // =====================================================================
        //  WebView2 初始化：加载本地 UI（https://app.local）
        // =====================================================================
        private async void InitWeb()
        {
            _web = new WebView2();
            _web.Dock = DockStyle.Fill;
            // 透明背景：让窗口层 Mica 材质透出，形成 Win11 磨砂质感（前端 body 为半透明背景）
            _web.DefaultBackgroundColor = Color.Transparent;
            this.Controls.Add(_web);

            _bridge = new NativeBridge(_web, this);
            try
            {
                var env = await CoreWebView2Environment.CreateAsync(null, GetUserDataFolder(), null);
                await _web.EnsureCoreWebView2Async(env);

                string webDir = Path.Combine(Paths.Root, "launcher", "web");
                _web.CoreWebView2.SetVirtualHostNameToFolderMapping(
                    "app.local", webDir, CoreWebView2HostResourceAccessKind.DenyCors);
                // 官方文档要求：WebMessageReceived 必须在导航前注册，避免漏掉早期消息
                _bridge.Attach();
                // 导航 URL 带时间戳：强制 WebView2 每次启动重新加载最新前端（绕过 HTTP 缓存旧版 UI）
                _web.CoreWebView2.Navigate("https://app.local/index.html?v=" + DateTime.Now.Ticks);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "WebView2 初始化失败：\n" + ex.Message + "\n\n请确认系统已安装 WebView2 运行时。",
                    "DeepSeek Harness", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private string GetUserDataFolder()
        {
            string f = Path.Combine(Paths.Root, "launcher", "webview-data");
            try { Directory.CreateDirectory(f); } catch { }
            return f;
        }

        // =====================================================================
        //  无边框窗口：窗口拖动/缩放（前端 pointer 检测 + 后台线程 SetWindowPos 手动循环）
        //  WebView2 全屏捕获全部鼠标事件，系统 WM_NCHITTEST / SC_SIZE 原生缩放均无法介入
        //  （社区实测：brmble 也确认 WebView2 下无法走 WM_NCHITTEST），故由前端检测边缘/
        //  标题栏 mousedown → 原生后台线程实时 SetWindowPos 跟随（5ms 节流 ≈200fps，跟手无卡顿），
        //  pointerup 通知停止。单击不黏住由“未移动超时”兜底 + 原生忽略“过早 End”双重保证。
        //  WM_GETMINMAXINFO 显式设置最小跟踪尺寸（无边框窗口 WinForms MinimumSize 不生效）。
        // =====================================================================
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == NativeMethods.WM_GETMINMAXINFO)
            {
                NativeMethods.MINMAXINFO mmi = (NativeMethods.MINMAXINFO)
                    Marshal.PtrToStructure(m.LParam, typeof(NativeMethods.MINMAXINFO));
                mmi.ptMinTrackSize.X = this.MinimumSize.Width;
                mmi.ptMinTrackSize.Y = this.MinimumSize.Height;
                Marshal.StructureToPtr(mmi, m.LParam, false);
                m.Result = IntPtr.Zero;
                return;
            }
            // 拖动/缩放循环期间临时去掉 WebView2 透明合成，提升流畅度（结束后恢复）
            if (m.Msg == WM_ENTERSIZEMOVE)
            {
                if (_web != null) _web.DefaultBackgroundColor = Color.FromArgb(13, 15, 28);
            }
            else if (m.Msg == WM_EXITSIZEMOVE)
            {
                if (_web != null) _web.DefaultBackgroundColor = Color.Transparent;
            }
            base.WndProc(ref m);
        }

        // =====================================================================
        //  窗口控制（由前端调用）
        // =====================================================================
        // 拖动/缩放窗口期间：临时去掉 WebView2 透明合成，显著提升移动/缩放流畅度；
        // 结束后恢复透明（配合前端深色 body，视觉无差异但性能提升明显）
        // 通过 WM_ENTERSIZEMOVE / WM_EXITSIZEMOVE 覆盖“拖动移动 + 缩放”全部场景（OnResizeBegin/End 只覆盖缩放）
        protected override void OnResizeBegin(EventArgs e)
        {
            base.OnResizeBegin(e);
            if (_web != null) _web.DefaultBackgroundColor = Color.FromArgb(13, 15, 28);
        }
        protected override void OnResizeEnd(EventArgs e)
        {
            base.OnResizeEnd(e);
            if (_web != null) _web.DefaultBackgroundColor = Color.Transparent;
        }

        private const int WM_ENTERSIZEMOVE = 0x0231;
        private const int WM_EXITSIZEMOVE = 0x0232;

        // 拖动/缩放循环开关（前端 pointerup 通知停止；循环自带“最近移动后超时”防单击黏住）
        private volatile bool _dragActive = false;
        private DateTime _dragStartTime;

        // 手动拖动窗口：后台线程实时 SetWindowPos 跟随鼠标。
        // 停止机制：① 左键物理松开（GetAsyncKeyState，最可靠，不依赖 WebView2 事件）；
        // ② 前端 End 命令；③ 最近移动后 400ms 未再移动（松开后兜底，也防单击黏住）。
        // 16ms 节流 ≈60fps，平滑且 CPU 低、重绘不产生黑边。
        public void BeginDrag()
        {
            if (this.WindowState == FormWindowState.Maximized) return;
            if (_dragActive) return;
            NativeMethods.POINT start;
            if (!NativeMethods.GetCursorPos(out start)) return;
            NativeMethods.RECT rc;
            if (!NativeMethods.GetWindowRect(this.Handle, out rc)) return;
            _dragActive = true;
            _dragStartTime = DateTime.UtcNow;
            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    DateTime deadline = DateTime.UtcNow.AddSeconds(30);
                    DateTime startT = DateTime.UtcNow;
                    DateTime lastMove = startT;
                    while (_dragActive && DateTime.UtcNow < deadline)
                    {
                        // 预热 150ms 后开始检测左键松开（避免事件延迟在循环刚启动时误判提前退出）
                        if ((DateTime.UtcNow - startT).TotalMilliseconds > 150 &&
                            (NativeMethods.GetAsyncKeyState(NativeMethods.VK_LBUTTON) & 0x8000) == 0)
                        { break; }
                        NativeMethods.POINT cur;
                        if (!NativeMethods.GetCursorPos(out cur)) break;
                        int mm = Math.Abs(cur.X - start.X) + Math.Abs(cur.Y - start.Y);
                        if (mm > 4) lastMove = DateTime.UtcNow;
                        // 兜底：最近 400ms 内鼠标未再移动（单击/误触/已松开）→ 停止
                        if ((DateTime.UtcNow - lastMove).TotalMilliseconds > 400) { break; }
                        NativeMethods.SetWindowPos(this.Handle, IntPtr.Zero,
                            rc.Left + (cur.X - start.X), rc.Top + (cur.Y - start.Y),
                            0, 0, NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
                        System.Threading.Thread.Sleep(16); // 16ms ≈60fps，平滑、低占用、不产生重绘黑边
                    }
                }
                catch { }
                finally { _dragActive = false; }
            });
        }

        // 手动 resize 循环：后台线程实时 SetWindowPos 缩放（同拖动），无系统预选框。
        // dir 与前端一致：l/r/t/b/tl/tr/bl/br
        public void BeginResize(string dir)
        {
            if (this.WindowState == FormWindowState.Maximized) return;
            if (_dragActive) return;
            NativeMethods.RECT rc;
            if (!NativeMethods.GetWindowRect(this.Handle, out rc)) return;
            int ox = rc.Left, oy = rc.Top, ow = rc.Right - rc.Left, oh = rc.Bottom - rc.Top;
            NativeMethods.POINT start;
            if (!NativeMethods.GetCursorPos(out start)) return;
            bool e = dir.Contains("r"), w = dir.Contains("l"), s = dir.Contains("b"), n = dir.Contains("t");
            if (!e && !w && !s && !n) return;
            int minW = this.MinimumSize.Width > 0 ? this.MinimumSize.Width : 1200;
            int minH = this.MinimumSize.Height > 0 ? this.MinimumSize.Height : 760;
            _dragActive = true;
            _dragStartTime = DateTime.UtcNow;
            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    DateTime deadline = DateTime.UtcNow.AddSeconds(30);
                    DateTime startT = DateTime.UtcNow;
                    DateTime lastMove = startT;
                    while (_dragActive && DateTime.UtcNow < deadline)
                    {
                        // 预热 150ms 后开始检测左键松开
                        if ((DateTime.UtcNow - startT).TotalMilliseconds > 150 &&
                            (NativeMethods.GetAsyncKeyState(NativeMethods.VK_LBUTTON) & 0x8000) == 0)
                        { break; }
                        NativeMethods.POINT cur;
                        if (!NativeMethods.GetCursorPos(out cur)) break;
                        int mm = Math.Abs(cur.X - start.X) + Math.Abs(cur.Y - start.Y);
                        if (mm > 4) lastMove = DateTime.UtcNow;
                        // 兜底：最近 400ms 内鼠标未再移动（单击/误触/已松开）→ 停止
                        if ((DateTime.UtcNow - lastMove).TotalMilliseconds > 400) { break; }
                        int dx = cur.X - start.X, dy = cur.Y - start.Y;
                        int nw = ow, nh = oh, nx = ox, ny = oy;
                        if (e) nw = ow + dx;
                        if (w) { nw = ow - dx; nx = ox + dx; }
                        if (s) nh = oh + dy;
                        if (n) { nh = oh - dy; ny = oy + dy; }
                        if (nw < minW) { nw = minW; if (w) nx = ox + (ow - minW); }
                        if (nh < minH) { nh = minH; if (n) ny = oy + (oh - minH); }
                        NativeMethods.SetWindowPos(this.Handle, IntPtr.Zero, nx, ny, nw, nh,
                            NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
                        System.Threading.Thread.Sleep(16);
                    }
                }
                catch { }
                finally { _dragActive = false; }
            });
        }

        // 前端 pointerup 通知停止。
        // 忽略“过早 End”：WebView2 在顶部/边缘 mousedown 后偶发 1~7ms 的 pointercancel/pointerup 竞争，
        // 立即停止会让循环刚启动就中断（上缘/边缘缩放卡住）。循环的主停止机制是 GetAsyncKeyState
        //（左键物理松开，最可靠）与“最近移动后超时”，End 仅为冗余；忽略过早 End 不会卡死。
        public void StopDrag()
        {
            if (_dragActive && _dragStartTime != DateTime.MinValue &&
                (DateTime.UtcNow - _dragStartTime).TotalMilliseconds < 150)
            {
                return; // 忽略过早 End，循环继续
            }
            _dragActive = false;
        }

        public void MinimizeWindow() { this.WindowState = FormWindowState.Minimized; }

        public void MaximizeToggle()
        {
            if (this.WindowState == FormWindowState.Maximized)
                this.WindowState = FormWindowState.Normal;
            else
                this.WindowState = FormWindowState.Maximized;
        }

        public void CloseApp() { _forceClose = true; this.Close(); }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            // 窗口动效（社区成熟方案）：Win11 DWM 圆角 + 深色标题栏 + 打开淡入动画
            // Win10 及以下：DwmSetWindowAttribute 静默失败，自动回退为无效果，不影响功能
            try
            {
                IntPtr h = this.Handle;
                int dark = 1;
                NativeMethods.DwmSetWindowAttribute(h, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
                int corner = NativeMethods.DWMWCP_ROUND;
                NativeMethods.DwmSetWindowAttribute(h, NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));
                // Win11 Mica 材质：窗口背景跟随壁纸动态纹理（被 WebView2 覆盖部分由前端半透明背景透出）
                int backdrop = NativeMethods.DWMSBT_MAINWINDOW;
                NativeMethods.DwmSetWindowAttribute(h, NativeMethods.DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, sizeof(int));
                // 打开淡入动画
                NativeMethods.AnimateWindow(h, 180, NativeMethods.AW_BLEND | NativeMethods.AW_ACTIVATE);
            }
            catch { }
            // 启动后自动跑一轮环境自检：
            //   · 换了电脑（机器指纹变化）-> 自动修路径、重探模型端点
            //   · 运行时缺失 -> 提示（不擅自下载，避免开机就拉几百 MB）
            // 必须在后台线程；否则文件系统扫描会卡住 UI。
            System.Threading.Tasks.Task.Run(() =>
            {
                try { EnvironmentSetup.Run(false); } catch { }
            });
            // 窗口显示后再初始化 WebView2（延迟避免 DPI 布局干扰）
            try { InitWeb(); }
            catch { }
        }

        public void OpenHarness()
        {
            try
            {
                string url = "http://127.0.0.1:" + Dsh.Port;
                Process.Start(url);
            }
            catch { }
        }

        // =====================================================================
        //  托盘
        // =====================================================================
        private void BuildTray()
        {
            _trayMenu = new ContextMenuStrip();
            _trayMenu.BackColor = Color.FromArgb(26, 30, 46);
            _trayMenu.ForeColor = Color.White;

            ToolStripMenuItem show = new ToolStripMenuItem("显示主窗口");
            show.Click += (s, e) => { this.Show(); this.WindowState = FormWindowState.Normal; this.Activate(); };
            _trayMenu.Items.Add(show);

            ToolStripMenuItem start = new ToolStripMenuItem("启动 Harness 服务");
            start.Click += (s, e) => Dsh.Start();
            _trayMenu.Items.Add(start);

            ToolStripMenuItem stop = new ToolStripMenuItem("停止 Harness 服务");
            stop.Click += (s, e) => Dsh.Stop();
            _trayMenu.Items.Add(stop);

            _trayMenu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem exit = new ToolStripMenuItem("退出");
            exit.Click += (s, e) => { _forceClose = true; this.Close(); };
            _trayMenu.Items.Add(exit);

            _trayIcon = new NotifyIcon();
            _trayIcon.Text = "DeepSeek Harness 桌面工作台";
            _trayIcon.ContextMenuStrip = _trayMenu;
            try { _trayIcon.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
            catch { _trayIcon.Icon = SystemIcons.Application; }
            _trayIcon.Visible = true;
            _trayIcon.DoubleClick += (s, e) => { this.Show(); this.WindowState = FormWindowState.Normal; this.Activate(); };
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            try { File.AppendAllText(Path.Combine(Paths.ConfigDir, "crash.log"),
                DateTime.Now.ToString("HH:mm:ss") + " [close] force=" + _forceClose + " reason=" + e.CloseReason + "\n"); }
            catch { }
            if (!_forceClose && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                this.Hide();
                _trayIcon.ShowBalloonTip(2000, "DeepSeek Harness", "已最小化到托盘，双击图标可恢复。", ToolTipIcon.Info);
                return;
            }
            if (Dsh != null && Dsh.State != ServerState.Stopped) Dsh.Stop();
            if (Models != null) Models.StopAll();
            // 关闭 Job 句柄 -> 内核终止 Job 内全部子进程（覆盖 taskkill /T 覆盖不到的场景）
            ProcessSupervisor.Shutdown();
            if (_trayIcon != null) { _trayIcon.Visible = false; _trayIcon.Dispose(); }
            base.OnFormClosing(e);
        }
    }
}


