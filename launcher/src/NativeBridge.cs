using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Web.Script.Serialization;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace DeepSeekHarnessLauncher
{
    // =====================================================================
    //  WebView2 <-> C# 桥接层
    //  JS: window.chrome.webview.postMessage('{id,cmd,args}')
    //  C#: {type:'response', id, ok, data} / {type:'state'|'log'|'toast', ...}
    // =====================================================================
    public class NativeBridge
    {
        private WebView2 _web;
        private MainForm _form;
        private JavaScriptSerializer _ser = new JavaScriptSerializer();
        private Timer _stateTimer;
        private bool _attached = false;

        // 注意：Ready 会被后台线程（node/llama 进程输出回调）访问，必须安全化，
        // 否则从后台线程触碰 CoreWebView2 会抛 InvalidOperationException 导致进程崩溃。
        public bool Ready
        {
            get
            {
                try { return _attached && _web.CoreWebView2 != null; }
                catch { return false; }
            }
        }

        public NativeBridge(WebView2 web, MainForm form)
        {
            _web = web;
            _form = form;
        }

        // 在 CoreWebView2 初始化完成后调用
        public void Attach()
        {
            if (_attached || _web.CoreWebView2 == null) return;
            _attached = true;
            _web.CoreWebView2.WebMessageReceived += (s, e) =>
            {
                // 注意：WebMessageAsJson 返回的是 JSON 编码后的字符串（外层带引号），
                // 直接 Deserialize 到 Dictionary 会因"String 转 Dictionary"抛错。
                // 必须用 TryGetWebMessageAsString() 取原始消息文本。
                // 只接受来自本应用页面（app.local）的消息。
                // dsh 的 Web UI 在跨域 iframe 中运行（web/index.html 的 #hx-frame），
                // 而 WebView2 会把 chrome.webview 注入到每一个 frame —— 不校验来源，
                // 等于把 setConfig / installPlugin / openExternal 交给 iframe 里的任意内容。
                try
                {
                    string src = e.Source;
                    if (string.IsNullOrEmpty(src) ||
                        src.IndexOf("app.local", StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        LogBridge("拒绝非 app.local 来源的消息: " + src);
                        return;
                    }
                }
                catch { }
                string raw = null;
                try { raw = e.TryGetWebMessageAsString(); } catch { }
                if (string.IsNullOrEmpty(raw)) raw = e.WebMessageAsJson;
                try { HandleMessage(raw); }
                catch (Exception ex)
                {
                    try { PushToast("内部错误: " + ex.Message, "error"); } catch { }
                }
            };

            // 服务日志 -> 前端
            MainForm.Dsh.LogLine += line => PushLog(line);
            MainForm.Models.LogLine += line => PushLog(line);

            // 状态推送（2s 轮询，同时做心跳探测）
            _stateTimer = new Timer();
            _stateTimer.Interval = 2000;
            _stateTimer.Tick += (s, e) => PushState();
            _stateTimer.Start();
            PushState();
        }

        // 日志/状态/提示推送：可能从后台线程（node/llama 输出回调）调用，
        // 全部操作（含 Ready 检查）都必须在 try/catch 内，且真正触碰 CoreWebView2
        // 的动作经 BeginInvoke 切回 UI 线程执行，避免 InvalidOperationException。
        public void PushLog(string line)
        {
            try
            {
                LogManager.Write(line);
                var payload = new Dictionary<string, object> { { "type", "log" }, { "line", line } };
                Post(_ser.Serialize(payload));
            }
            catch { }
        }

        public void PushState()
        {
            try
            {
                var payload = new Dictionary<string, object> { { "type", "state" }, { "data", BuildState() } };
                Post(_ser.Serialize(payload));
            }
            catch { }
        }

        public void PushToast(string text, string kind)
        {
            try
            {
                var payload = new Dictionary<string, object> { { "type", "toast" }, { "text", text }, { "kind", kind } };
                Post(_ser.Serialize(payload));
            }
            catch { }
        }

        private void Post(string json)
        {
            // 用 PostWebMessageAsString：前端 ev.data 为字符串，与 bridge.js 的 JSON.parse(ev.data) 匹配。
            // PostWebMessageAsString 线程安全，可直接从 Task.Run 等非 UI 线程调用。
            try
            {
                var web = _web;
                var core = web != null ? web.CoreWebView2 : null;
                if (core == null) return;
                core.PostWebMessageAsString(json);
            }
            catch { }
        }

        // =====================================================================
        //  命令分发
        // =====================================================================
        private void HandleMessage(string json)
        {
            var msg = _ser.Deserialize<Dictionary<string, object>>(json);
            if (msg == null || !msg.ContainsKey("cmd")) return;
            string cmd = msg["cmd"] as string ?? "";
            object id = msg.ContainsKey("id") ? msg["id"] : null;
            var args = new Dictionary<string, object>();
            if (msg.ContainsKey("args") && msg["args"] is Dictionary<string, object>)
                args = (Dictionary<string, object>)msg["args"];

            if (cmd != "getState") LogBridge("CMD: " + cmd);

            switch (cmd)
            {
                case "getState": Respond(id, true, BuildState()); break;
                case "getEnv": Respond(id, true, BuildEnv()); break;

                // Dsh.Start() 内部会同步执行 bootstrap（最长 10 分钟）与本地模型拉起，
                // 必须在后台线程执行；否则 WebView2 所在的 UI 线程被占住，窗口完全无响应。
                case "startGateway": Task.Run(() => { MainForm.Dsh.Start(); Respond(id, true, MainForm.Dsh.State.ToString()); }); break;
                case "stopGateway": Task.Run(() => { MainForm.Dsh.Stop(); Respond(id, true, MainForm.Dsh.State.ToString()); }); break;

                case "startModel": StartModelAsync(id, args); break;
                case "stopModel": StopModelAsync(id, args); break;
                case "stopAllModels": MainForm.Models.StopAll(); Respond(id, true, true); break;

                case "scanGGUF": ScanGguf(id, args); break;
                case "pickFolder": PickFolder(id, args); break;
                case "detectLlamaDir": Respond(id, true, MainForm.Models.DetectLlamaDir()); break;
                case "detectOllama": Respond(id, true, MainForm.Models.DetectOllamaPath()); break;

                case "getConfig": Respond(id, true, BuildConfig()); break;
                // 口令解锁：换机后需要输一次。成功则顺手迁移残留明文。
                case "unlockSecrets":
                    {
                        string pw = GetStr(args, "passphrase");
                        var st = SecretStore.Unlock(pw);
                        if (st == SecretStore.LoadStatus.Ok)
                        {
                            string[] notes = SecretMigration.Run();
                            Respond(id, true, BuildSecretStatus(notes));
                        }
                        else Respond(id, false, SecretStore.LastError == null ? st.ToString() : SecretStore.LastError);
                    }
                    break;
                // 切换凭据存储模式：passphrase=换机可用；dpapi=本机免口令但不可换机
                case "setSecretMode":
                    {
                        string mode = GetStr(args, "mode");
                        bool ok;
                        if (string.Equals(mode, "dpapi", StringComparison.OrdinalIgnoreCase))
                            ok = SecretStore.SwitchToDpapiMode();
                        else
                            ok = SecretStore.SwitchToPassphraseMode(GetStr(args, "passphrase"));
                        if (!ok) { Respond(id, false, SecretStore.LastError); break; }
                        string[] notes = SecretMigration.Run();
                        Respond(id, true, BuildSecretStatus(notes));
                    }
                    break;
                // 丢弃本机解锁缓存（下次启动需重输口令）—— 也便于验证"换机"路径
                case "forgetUnlock":
                    SecretStore.ForgetUnlockCache();
                    Respond(id, true, BuildSecretStatus(null));
                    break;
                case "envSetup":
                    if (EnvironmentSetup.Running) { Respond(id, false, "配置正在进行中，请稍候。"); break; }
                    Task.Run(() => { var rep = EnvironmentSetup.Run(true); Respond(id, true, rep); });
                    break;
                case "clearSecret":
                    SecretStore.Remove("DEEPSEEK_API_KEY");
                    bool cleared = SecretStore.Save();
                    if (cleared) SecretMigration.Run();   // 顺手抹掉配置文件里可能残留的明文
                    Respond(id, cleared, SecretStore.LastError);
                    break;
                case "setConfig": SetConfig(id, args); break;

                case "getTheme": Respond(id, true, MainForm.Themes.Load()); break;
                case "saveTheme": MainForm.Themes.Save(GetStr(args, "json")); Respond(id, true, true); break;
                case "saveUserBg": SaveUserBg(id, args); break;

                case "listPlugins": ListPlugins(id); break;
                case "installPlugin": InstallPlugin(id, args); break;
                case "uninstallPlugin": UninstallPlugin(id, args); break;

                case "openExternal": OpenExternal(GetStr(args, "url")); Respond(id, true, true); break;
                case "openHarness": _form.OpenHarness(); Respond(id, true, true); break;

                case "windowDrag":
                    _form.BeginDrag();
                    Respond(id, true, true);
                    break;
                case "windowResize":
                    {
                        string dr = GetStr(args, "dir");
                        _form.BeginResize(dr);
                        Respond(id, true, true);
                        break;
                    }
                case "windowDragEnd":
                case "windowResizeEnd":
                    _form.StopDrag();
                    Respond(id, true, true);
                    break;
                case "windowMin": _form.MinimizeWindow(); Respond(id, true, true); break;
                case "windowMaxToggle": _form.MaximizeToggle(); Respond(id, true, true); break;
                case "windowClose": _form.CloseApp(); Respond(id, true, true); break;

                case "runAutoConfig": Task.Run(() => { MainForm.Dsh.RunAutoConfig(); }); Respond(id, true, true); break;

                default: Respond(id, false, "unknown cmd: " + cmd); break;
            }
        }

        // =====================================================================
        //  状态 / 环境
        // =====================================================================
        private Dictionary<string, object> BuildState()
        {
            var cfg = MainForm.Cfg;
            return new Dictionary<string, object>
            {
                { "gateway", new Dictionary<string, object> {
                    { "state", MainForm.Dsh.State.ToString().ToLowerInvariant() },
                    { "port", MainForm.Dsh.Port },
                    { "ready", MainForm.Dsh.Ready() },
                    { "pid", MainForm.Dsh.Proc != null ? MainForm.Dsh.Proc.Id : 0 }
                } },
                { "model", new Dictionary<string, object> {
                    { "llama", MainForm.Models.LlamaRunning() },
                    { "ollama", MainForm.Models.OllamaRunning() },
                    { "llamaPort", MainForm.Models.LlamaPort },
                    // llamaDir：llama.cpp 安装目录（含 llama-server.exe，用于启动底座）
                    { "llamaDir", MainForm.Models.LlamaExeDir() },
                    // modelDir：模型扫描目录（含 GGUF 文件，用于左卡扫描）
                    { "modelDir", cfg.Get("DSH_LLAMA_DIR", "") },
                    { "ollamaPath", MainForm.Models.DetectOllamaPath() },
                    { "backend", cfg.Get("DSH_MODEL_BACKEND", "llama.cpp") }
                } }
            };
        }

        private Dictionary<string, object> BuildEnv()
        {
            var cfg = MainForm.Cfg;
            float dpiScale = 1f;
            try { dpiScale = _form.DeviceDpi / 96f; } catch { }
            return new Dictionary<string, object>
            {
                { "node", Util.FileVersionOrDash(Paths.NodeExe) },
                { "nodeOk", File.Exists(Paths.NodeExe) },
                { "dsh", ReadDshVersion() },
                { "dshOk", File.Exists(Paths.DshJsBin) },
                { "os", Environment.OSVersion.VersionString },
                { "port", MainForm.Dsh.Port },
                { "portEditable", true },
                { "autoStart", cfg.GetBool("DSH_AUTO_START_MODEL") },
                { "modelDir", cfg.Get("DSH_LLAMA_DIR", "") },
                { "llamaDir", MainForm.Models.LlamaExeDir() },
                { "llamaPort", MainForm.Models.LlamaPort },
                // 系统 DPI 缩放系数（150% -> 1.5）：前端用于 CSS zoom 补偿，避免流式布局下界面过小
                { "dpi", Math.Round(dpiScale, 3) },
                // 凭据状态（不含任何密钥内容）
                // 注意：必须回完整对象 —— 环境配置页读的是 env.secret，
                // 只给扁平字段会导致该页永远显示"未设置/未配置"。
                { "secret", BuildSecretStatus(null) },
                { "secretMode", SecretStore.CurrentMode.ToString() },
                { "secretsUnlocked", SecretStore.IsUnlocked },
                { "secretError", SecretStore.LastError == null ? "" : SecretStore.LastError },
                { "startupNotes", MainForm.StartupNotes },
                { "jobObject", ProcessSupervisor.Adopted },
                { "envSetupRunning", EnvironmentSetup.Running },
                { "lastSetup", EnvironmentSetup.LastReport }
            };
        }

        private string ReadDshVersion()
        {
            try
            {
                string pkg = Path.Combine(Paths.GlobalDir, "node_modules", "@deepseek-ai", "dsh", "package.json");
                if (!File.Exists(pkg)) return "未安装";
                string txt = File.ReadAllText(pkg, System.Text.Encoding.UTF8);
                var m = System.Text.RegularExpressions.Regex.Match(txt, "\"version\"\\s*:\\s*\"([^\"]+)\"");
                return m.Success ? m.Groups[1].Value : "?";
            }
            catch { return "?"; }
        }

        // =====================================================================
        //  模型相关
        // =====================================================================
        private void StartModelAsync(object id, Dictionary<string, object> args)
        {
            string backend = GetStr(args, "backend");
            if (string.IsNullOrEmpty(backend)) backend = MainForm.Cfg.Get("DSH_MODEL_BACKEND", "llama.cpp");
            Task.Run(() =>
            {
                try
                {
                    bool ok;
                    if (backend.Equals("ollama", StringComparison.OrdinalIgnoreCase))
                    {
                        string exe = GetStr(args, "ollamaPath");
                        if (string.IsNullOrEmpty(exe)) exe = MainForm.Models.DetectOllamaPath();
                        ok = MainForm.Models.StartOllama(exe);
                        string model = GetStr(args, "ollamaModel");
                        if (ok && !string.IsNullOrEmpty(model)) MainForm.Models.LoadOllamaModel(exe, model);
                    }
                    else
                    {
                        string dir = GetStr(args, "dir");
                        string model = GetStr(args, "model");
                        string ctx = GetStr(args, "ctx");
                        string gpu = GetStr(args, "gpu");
                        string extra = GetStr(args, "extra");
                        ok = MainForm.Models.StartLlama(dir, model, ctx, gpu, extra);
                    }
                    string res = ok ? "started" : "failed";
                    _form.BeginInvoke(new Action(() => Respond(id, ok, res)));
                }
                catch (Exception ex)
                {
                    string m = ex.Message;
                    _form.BeginInvoke(new Action(() => Respond(id, false, m)));
                }
            });
        }

        private void StopModelAsync(object id, Dictionary<string, object> args)
        {
            string backend = GetStr(args, "backend");
            Task.Run(() =>
            {
                try
                {
                    if (backend.Equals("ollama", StringComparison.OrdinalIgnoreCase)) MainForm.Models.StopOllama();
                    else MainForm.Models.StopLlama();
                    _form.BeginInvoke(new Action(() => Respond(id, true, "stopped")));
                }
                catch (Exception ex)
                {
                    string m = ex.Message;
                    _form.BeginInvoke(new Action(() => Respond(id, false, m)));
                }
            });
        }

        private void ScanGguf(object id, Dictionary<string, object> args)
        {
            string dir = GetStr(args, "dir");
            // 递归遍历模型目录可能很久（慢盘/大目录），必须离开 UI 线程
            Task.Run(() =>
            {
                try
                {
                    LogBridge("scanGGUF 开始 dir=" + dir);
                    var list = MainForm.Models.FindGgufModelsDetailed(dir);
                    LogBridge("scanGGUF 完成 count=" + list.Count);
                    Respond(id, true, list);
                }
                catch (Exception ex)
                {
                    LogBridge("scanGGUF 异常: " + ex.Message);
                    Respond(id, false, ex.Message);
                }
            });
        }

        private void LogBridge(string line)
        {
            try
            {
                lock (_logLock)
                {
                    string f = Path.Combine(Paths.ConfigDir, "bridge.log");
                    System.IO.File.AppendAllText(f, DateTime.Now.ToString("HH:mm:ss.fff") + " " + line + "\r\n");
                }
            }
            catch { }
        }

        private readonly object _logLock = new object();

        private void PickFolder(object id, Dictionary<string, object> args)
        {
            try
            {
                string result = null;
                _form.Invoke(new Action(() =>
                {
                    using (var dlg = new FolderBrowserDialog())
                    {
                        dlg.Description = GetStr(args, "title");
                        if (dlg.ShowDialog(_form) == DialogResult.OK) result = dlg.SelectedPath;
                    }
                }));
                Respond(id, true, result);
            }
            catch (Exception ex) { Respond(id, false, ex.Message); }
        }

        // =====================================================================
        //  配置
        // =====================================================================
        /// <summary>凭据状态摘要。只回掩码与模式，绝不回密钥内容。</summary>
        private Dictionary<string, object> BuildSecretStatus(string[] notes)
        {
            return new Dictionary<string, object>
            {
                { "mode", SecretStore.CurrentMode.ToString() },
                { "unlocked", SecretStore.IsUnlocked },
                { "hasKey", SecretStore.Has("DEEPSEEK_API_KEY") },
                { "hint", SecretStore.MaskedHint("DEEPSEEK_API_KEY") },
                { "error", SecretStore.LastError == null ? "" : SecretStore.LastError },
                { "notes", notes == null ? new string[0] : notes }
            };
        }

        private Dictionary<string, object> BuildConfig()
        {
            var cfg = MainForm.Cfg;
            var d = new Dictionary<string, object>();
            foreach (var k in new[] {
                "DSH_PORT", "DSH_AUTO_START_MODEL", "DSH_MODEL_BACKEND",
                "DSH_LLAMA_DIR", "DSH_LLAMA_MODEL", "DSH_LLAMA_PORT", "DSH_LLAMA_CONTEXT",
                "DSH_LLAMA_GPU_LAYERS", "DSH_LLAMA_EXTRA_ARGS", "DSH_OLLAMA_EXE", "DSH_OLLAMA_MODEL",
                "DSH_EXTRA_ENDPOINTS", "DSH_LLAMA_EXE_DIR" })
            {
                d[k] = cfg.Get(k, "");
            }
            // 密钥绝不回传前端：只回"是否已配置"与掩码提示（例如 sk-xxxx****yyyy）
            string apiKey;
            bool hasKey = SecretStore.TryGet("DEEPSEEK_API_KEY", out apiKey) && !string.IsNullOrEmpty(apiKey);
            d["hasApiKey"] = hasKey;
            d["apiKeyHint"] = hasKey ? SecretStore.MaskedHint("DEEPSEEK_API_KEY") : "";
            d["secretBackend"] = "DPAPI (CurrentUser)";
            string secErr = SecretStore.LastError;
            d["secretError"] = string.IsNullOrEmpty(secErr) ? "" : secErr;
            string[] notes = MainForm.StartupNotes;
            d["startupNotes"] = (notes == null) ? new string[0] : notes;
            d["secret"] = BuildSecretStatus(null);
            d["lastSetup"] = EnvironmentSetup.LastReport;
            d["envSetupRunning"] = EnvironmentSetup.Running;
            return d;
        }

        private void SetConfig(object id, Dictionary<string, object> args)
        {
            string key = GetStr(args, "key");
            string value = GetStr(args, "value");
            // 密钥走 DPAPI 加密库，绝不写进 config\user.env（这是本项目最重要的安全约束）
            if (SecretMigration.IsSecret(key))
            {
                if (string.IsNullOrEmpty(value))
                {
                    Respond(id, false, "密钥为空，未做修改。需要清除请使用 clearSecret。");
                    return;
                }
                SecretStore.Set(key, value);
                if (!SecretStore.Save())
                {
                    Respond(id, false, "密钥保存失败：" + SecretStore.LastError);
                    return;
                }
                // 换密钥即自动配置：
                //   1. 抹掉散落在 user.env / .credentials.yaml 里的旧明文
                //   2. 服务正在运行则自动重启，让新密钥立刻生效
                string[] notes = SecretMigration.Run();
                bool restarted = false;
                if (MainForm.Dsh != null && MainForm.Dsh.State == ServerState.Running)
                {
                    Task.Run(() => { MainForm.Dsh.Stop(); MainForm.Dsh.Start(); });
                    restarted = true;
                }
                Respond(id, true, "密钥已加密保存（模式：" + SecretStore.CurrentMode + "）"
                    + (notes.Length > 0 ? "；已清理 " + notes.Length + " 处旧明文" : "")
                    + (restarted ? "；正在自动重启服务以生效。" : "；下次启动服务生效。"));
                return;
            }
            var cfg = MainForm.Cfg;
            if (key == "DSH_PORT")
            {
                int p;
                if (int.TryParse(value, out p) && p >= 1024 && p <= 65535)
                {
                    cfg.Set(key, value);
                    cfg.Save();
                    MainForm.Dsh.Port = p;
                    Respond(id, true, "端口已更新为 " + p + "，重启服务后生效。");
                }
                else Respond(id, false, "端口无效（需 1024-65535）。");
                return;
            }
            if (key == "DSH_LLAMA_PORT")
            {
                int p;
                if (int.TryParse(value, out p) && p >= 1024 && p <= 65535)
                {
                    cfg.Set(key, value);
                    cfg.Save();
                    MainForm.Models.LlamaPort = p;
                    Respond(id, true, "模型端口已更新为 " + p);
                }
                else Respond(id, false, "端口无效。");
                return;
            }
            cfg.Set(key, value);
            cfg.Save();
            Respond(id, true, true);
        }

        // =====================================================================
        //  插件
        // =====================================================================
        private void ListPlugins(object id)
        {
            Task.Run(() =>
            {
                try { Respond(id, true, MainForm.Plugins.ListInstalled()); }
                catch (Exception ex) { Respond(id, false, ex.Message); }
            });
        }

        private void InstallPlugin(object id, Dictionary<string, object> args)
        {
            string pkg = GetStr(args, "pkg");
            if (!PkgNameRe.IsMatch(pkg)) { Respond(id, false, "包名不合法（仅允许 npm 包名，拒绝空格与 - 开头的参数注入）"); return; }
            Task.Run(() =>
            {
                try
                {
                    bool ok = MainForm.Plugins.Install(pkg);
                    PushToast(ok ? "插件安装完成：" + pkg : "插件安装失败：" + pkg, ok ? "success" : "error");
                    Respond(id, ok, ok ? (object)MainForm.Plugins.ListInstalled() : "安装失败，请查看日志");
                }
                catch (Exception ex) { Respond(id, false, ex.Message); }
            });
        }

        private void UninstallPlugin(object id, Dictionary<string, object> args)
        {
            string pkg = GetStr(args, "pkg");
            if (!PkgNameRe.IsMatch(pkg)) { Respond(id, false, "包名不合法。"); return; }
            Task.Run(() =>
            {
                try
                {
                    bool ok = MainForm.Plugins.Uninstall(pkg);
                    PushToast(ok ? "插件已卸载：" + pkg : "插件卸载失败：" + pkg, ok ? "success" : "error");
                    Respond(id, ok, ok ? (object)MainForm.Plugins.ListInstalled() : "卸载失败，请查看日志");
                }
                catch (Exception ex) { Respond(id, false, ex.Message); }
            });
        }

        // =====================================================================
        //  其它
        // =====================================================================
        private void OpenExternal(string url)
        {
            // 只放行 http/https。Process.Start 默认 UseShellExecute=true，
            // 传入 file:/UNC/可执行文件路径 会变成任意程序执行。
            try
            {
                Uri u;
                if (!Uri.TryCreate(url, UriKind.Absolute, out u)) return;
                if (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps) return;
                if (u.Scheme == Uri.UriSchemeHttp && !u.IsLoopback) return;   // 明文 http 只允许回环
                System.Diagnostics.Process.Start(url);
            }
            catch { }
        }

        
        // 保存用户上传的背景图片（data URL -> web\assets\bg\user-bg.png），返回相对路径
        private void SaveUserBg(object id, Dictionary<string, object> args)
        {
            string dataUrl = GetStr(args, "dataUrl");
            try
            {
                if (string.IsNullOrEmpty(dataUrl) || dataUrl.IndexOf("base64,") < 0)
                {
                    Respond(id, false, "无效的图片数据");
                    return;
                }
                string b64 = dataUrl.Substring(dataUrl.IndexOf("base64,") + 7);
                byte[] bytes = Convert.FromBase64String(b64);
                string dir = Path.Combine(Paths.Root, "launcher", "web", "assets", "bg");
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                string file = Path.Combine(dir, "user-bg.png");
                File.WriteAllBytes(file, bytes);
                Respond(id, true, "assets/bg/user-bg.png");
            }
            catch (Exception ex) { Respond(id, false, "保存背景图片失败: " + ex.Message); }
        }

        private void Respond(object id, bool ok, object data)
        {
            var payload = new Dictionary<string, object> { { "type", "response" }, { "id", id }, { "ok", ok }, { "data", data } };
            string json = _ser.Serialize(payload);
            Post(json);
        }

        // 插件名白名单：防止把 "--registry=..." 之类的 npm 参数注入进来
        private static readonly System.Text.RegularExpressions.Regex PkgNameRe =
            new System.Text.RegularExpressions.Regex(@"^(@[a-z0-9][a-z0-9-_.]*\/)?[a-z0-9][a-z0-9-_.]*(@[A-Za-z0-9][A-Za-z0-9-_.+]*)?$");

        private static string GetStr(Dictionary<string, object> args, string key)
        {
            if (args != null && args.ContainsKey(key) && args[key] != null) return args[key].ToString();
            return "";
        }
    }
}
