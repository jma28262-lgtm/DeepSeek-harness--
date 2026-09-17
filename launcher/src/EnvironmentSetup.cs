using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace DeepSeekHarnessLauncher
{
    /// <summary>一步配置的结果，直接序列化给前端展示。</summary>
    public class SetupStep
    {
        public string Name;      // 步骤名
        public string Status;    // ok | fixed | warn | failed | skipped
        public string Detail;    // 人类可读说明
    }

    public class SetupReport
    {
        public string MachineId;
        public bool MachineChanged;
        public bool NeedsPassphrase;
        public List<SetupStep> Steps = new List<SetupStep>();
        public bool AllGood;
    }

    /// <summary>
    /// 全自动环境配置。目标：**换机后直接可用**，把需要人做的事压到最少。
    ///
    /// 自动能做的（本类负责）：
    ///   1. 运行时检查：便携 Node / dsh 内核缺失则自动部署
    ///   2. 换机检测：机器指纹变化即视为换了电脑，触发一轮完整自检
    ///   3. 路径自愈：user.env 里写死的绝对路径（含跨盘符的模型路径）失效时，
    ///      在其它盘上按"相对尾部"重新定位并改写配置 —— 这是换机后最常坏的一环
    ///   4. 模型端点探测与 provider 生成：调用项目自带的 scripts/auto-config.mjs
    ///      （不重复实现 DSH 与既有脚本的能力，只负责驱动）
    ///   5. 端口可用性检查
    ///
    /// 必须由人做的一步：口令模式下的口令输入。理由见 SecretStore 的类注释 ——
    /// 想让"换机零输入"成立，解密密钥就必须和密文同盘，那等价于明文。
    /// </summary>
    public static class EnvironmentSetup
    {
        private const string MACHINE_FILE = "machine.id";

        /// <summary>最近一次配置报告。前端通过 getEnv 读取，用于展示「上次自动配置做了什么」。</summary>
        public static SetupReport LastReport;
        public static bool Running;

        // =================================================================
        //  机器指纹
        // =================================================================
        public static string CurrentMachineId()
        {
            string guid = "";
            try
            {
                using (var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography"))
                {
                    if (k != null) guid = (k.GetValue("MachineGuid") as string) ?? "";
                }
            }
            catch { }
            // MachineName + UserName 作为兜底/补充：MachineGuid 在克隆的虚拟机上可能重复
            string basis = guid + "|" + Environment.MachineName + "|" + Environment.UserName;
            using (var sha = new System.Security.Cryptography.SHA256Managed())
            {
                byte[] h = sha.ComputeHash(Encoding.UTF8.GetBytes(basis));
                var sb = new StringBuilder();
                for (int i = 0; i < 8; i++) sb.Append(h[i].ToString("x2"));
                return sb.ToString();
            }
        }

        // =================================================================
        //  路径自愈
        // =================================================================
        /// <summary>需要在换机后重新定位的路径型配置键，以及与它们对应的"查找特征"。</summary>
        private static readonly string[][] PathKeys = new string[][]
        {
            // 键名,          查找方式,      查找参数
            new string[] { "DSH_LLAMA_MODEL", "file",  "*.gguf" },              // 模型文件：按文件名在其它盘找同名
            new string[] { "DSH_LLAMA_DIR",   "dir",   "Ollama" },              // 模型目录
            new string[] { "DSH_LLAMA_EXE_DIR", "file", "llama-server.exe" },   // llama.cpp 安装目录
            new string[] { "DSH_OLLAMA_EXE",  "file",  "ollama.exe" }
        };

        /// <summary>
        /// 对失效的绝对路径做自愈：在其它可用盘上按"相对尾部"重新定位。
        /// 只改确实不存在、且能找到唯一替代的项，改前备份 user.env。
        /// </summary>
        private static void HealPaths(ConfigManager cfg, SetupReport report, List<string> changedKeys)
        {
            foreach (string[] spec in PathKeys)
            {
                string key = spec[0], mode = spec[1], arg = spec[2];
                string cur = cfg.Get(key, "");
                if (string.IsNullOrEmpty(cur)) continue;
                if (File.Exists(cur) || Directory.Exists(cur)) continue;   // 仍然有效，不动

                string found = mode == "dir" ? FindDirectoryOnOtherDrives(cur, arg) : FindFileOnOtherDrives(cur, arg);
                if (!string.IsNullOrEmpty(found))
                {
                    cfg.Set(key, found);
                    changedKeys.Add(key);
                    report.Steps.Add(MakeStep("路径自愈：" + key, "fixed", "原路径失效（" + cur + "）→ 已重定位到 " + found));
                }
                else
                {
                    report.Steps.Add(MakeStep("路径自愈：" + key, "warn", "原路径失效且未能在其它盘找到替代：" + cur));
                }
            }
        }

        private static string FindFileOnOtherDrives(string missing, string pattern)
        {
            string name = Path.GetFileName(missing);
            foreach (DriveInfo d in SafeDrives())
            {
                try
                {
                    string root = d.RootDirectory.FullName;
                    if (string.Equals(Path.GetPathRoot(missing), root, StringComparison.OrdinalIgnoreCase)) continue;
                    string direct = Path.Combine(root, TrimRoot(missing));
                    if (File.Exists(direct)) return direct;
                    if (!string.IsNullOrEmpty(name))
                    {
                        string hit = SearchShallow(root, name, false);
                        if (hit != null) return hit;
                    }
                    if (!string.IsNullOrEmpty(pattern))
                    {
                        string hit2 = SearchShallow(root, pattern, false);
                        if (hit2 != null) return hit2;
                    }
                }
                catch { }
            }
            return null;
        }

        private static string FindDirectoryOnOtherDrives(string missing, string hint)
        {
            string tail = TrimRoot(missing);
            foreach (DriveInfo d in SafeDrives())
            {
                try
                {
                    string root = d.RootDirectory.FullName;
                    if (string.Equals(Path.GetPathRoot(missing), root, StringComparison.OrdinalIgnoreCase)) continue;
                    string direct = Path.Combine(root, tail);
                    if (Directory.Exists(direct)) return direct;
                    if (!string.IsNullOrEmpty(hint))
                    {
                        string cand = Path.Combine(root, hint);
                        if (Directory.Exists(cand)) return cand;
                        string hit = SearchShallow(root, hint, true);
                        if (hit != null) return hit;
                    }
                }
                catch { }
            }
            return null;
        }

        private static string TrimRoot(string path)
        {
            try
            {
                string root = Path.GetPathRoot(path);
                if (!string.IsNullOrEmpty(root) && path.Length > root.Length) return path.Substring(root.Length);
            }
            catch { }
            return "";
        }

        /// <summary>浅层搜索（最多 3 层），避免在全盘上盲目递归造成长时间卡顿。</summary>
        private static string SearchShallow(string root, string pattern, bool directory)
        {
            try
            {
                var dirs = new Queue<KeyValuePair<string, int>>();
                dirs.Enqueue(new KeyValuePair<string, int>(root, 0));
                int visited = 0;
                while (dirs.Count > 0 && visited < 2000)
                {
                    KeyValuePair<string, int> cur = dirs.Dequeue();
                    visited++;
                    if (cur.Value >= 3) continue;
                    string[] subs;
                    try { subs = Directory.GetDirectories(cur.Key); }
                    catch { continue; }
                    foreach (string sub in subs)
                    {
                        // 跳过明显无关/巨大的系统目录
                        string n = Path.GetFileName(sub);
                        if (n.Equals("Windows", StringComparison.OrdinalIgnoreCase)) continue;
                        if (n.Equals("$RECYCLE.BIN", StringComparison.OrdinalIgnoreCase)) continue;
                        if (n.Equals("System Volume Information", StringComparison.OrdinalIgnoreCase)) continue;
                        if (directory)
                        {
                            if (Matches(n, pattern)) return sub;
                        }
                        else
                        {
                            try
                            {
                                foreach (string f in Directory.GetFiles(sub, pattern))
                                    return f;
                            }
                            catch { }
                        }
                        dirs.Enqueue(new KeyValuePair<string, int>(sub, cur.Value + 1));
                    }
                }
            }
            catch { }
            return null;
        }

        private static bool Matches(string name, string pattern)
        {
            if (string.IsNullOrEmpty(pattern)) return false;
            if (pattern.StartsWith("*."))
                return name.EndsWith(pattern.Substring(1), StringComparison.OrdinalIgnoreCase);
            return name.Equals(pattern, StringComparison.OrdinalIgnoreCase);
        }

        private static List<DriveInfo> SafeDrives()
        {
            var list = new List<DriveInfo>();
            try
            {
                foreach (DriveInfo d in DriveInfo.GetDrives())
                {
                    try { if (d.IsReady) list.Add(d); }
                    catch { }
                }
            }
            catch { }
            return list;
        }

        // =================================================================
        //  主流程
        // =================================================================
        /// <summary>
        /// 跑一轮完整配置。**必须在后台线程调用**（内部有文件系统扫描与子进程）。
        /// </summary>
        public static SetupReport Run(bool oneClick)
        {
            var report = new SetupReport();
            Running = true;
            try { return RunCore(report, oneClick); }
            finally { LastReport = report; Running = false; }
        }

        private static SetupReport RunCore(SetupReport report, bool oneClick)
        {
            var cfg = new ConfigManager(Paths.UserEnv);
            var changedKeys = new List<string>();

            report.MachineId = CurrentMachineId();
            report.MachineChanged = DetectAndRecordMachineChange(report.MachineId);
            if (report.MachineChanged)
                report.Steps.Add(MakeStep("换机检测", "fixed", "检测到这是另一台电脑（机器指纹变化），已自动触发完整自检与配置修复。"));

            // ---- 1) 运行时 ----
            bool nodeOk = File.Exists(Paths.NodeExe);
            bool dshOk = File.Exists(Paths.DshJsBin);
            if (nodeOk && dshOk)
            {
                report.Steps.Add(MakeStep("运行时", "ok", "便携 Node 与 dsh 内核均已就绪。"));
            }
            else if (!oneClick)
            {
                report.Steps.Add(MakeStep("运行时", "warn",
                    (nodeOk ? "" : "缺少 Node；") + (dshOk ? "" : "缺少 dsh 内核；")
                    + "启动服务时会自动部署。也可点「一键配置」立即处理。"));
            }
            else
            {
                report.Steps.Add(MakeStep("运行时", "fixed", "检测到运行时缺失，正在自动部署（bootstrap）……"));
                bool boot = RunBootstrap();
                report.Steps[report.Steps.Count - 1].Status = boot ? "fixed" : "failed";
                report.Steps[report.Steps.Count - 1].Detail = boot
                    ? "自动部署完成。"
                    : "自动部署失败，通常是网络问题。请检查网络后重试，或手工运行 bootstrap.ps1。";
            }

            // ---- 2) 路径自愈（换机的核心修复项）----
            int before = changedKeys.Count;
            HealPaths(cfg, report, changedKeys);
            if (changedKeys.Count == before)
                report.Steps.Add(MakeStep("路径检查", "ok", "所有已配置的路径在当前机器上均有效。"));

            // ---- 3) 端口 ----
            try
            {
                int savedPort;
                if (int.TryParse(cfg.Get("DSH_PORT", ""), out savedPort) && savedPort >= 1024)
                {
                    if (Util.IsPortListening(savedPort))
                    {
                        int free = DshService.DetectAvailablePort(savedPort);
                        report.Steps.Add(MakeStep("端口", "warn",
                            "配置端口 " + savedPort + " 已被占用，可用端口：" + free + "（可在「设置」里改，或保持现状让服务启动时自动挑选）"));
                    }
                    else report.Steps.Add(MakeStep("端口", "ok", "端口 " + savedPort + " 可用。"));
                }
                else report.Steps.Add(MakeStep("端口", "ok", "未固定端口，启动时自动选择。"));
            }
            catch { }

            // ---- 4) 凭据 ----
            try
            {
                if (Paths.ConfigDir != null)
                {
                    SecretStore.Init(Paths.ConfigDir);
                    SecretStore.Load();
                }
                string apiKey;
                bool has = SecretStore.TryGet("DEEPSEEK_API_KEY", out apiKey) && !string.IsNullOrEmpty(apiKey);
                if (SecretStore.IsUnlocked && has)
                {
                    report.Steps.Add(MakeStep("凭据", "ok", "API Key 已加密保存（" + SecretStore.MaskedHint("DEEPSEEK_API_KEY")
                        + "），存储模式：" + SecretStore.CurrentMode + "。"));
                    // 已解锁 -> 顺手把可能残留的明文收进加密库
                    string[] notes = SecretMigration.Run();
                    foreach (string n in notes) report.Steps.Add(MakeStep("凭据迁移", "fixed", n));
                }
                else if (!SecretStore.IsUnlocked)
                {
                    string pending = SecretMigration.FindPlaintext();
                    report.NeedsPassphrase = true;
                    report.Steps.Add(MakeStep("凭据", "warn",
                        (pending != null ? "检测到明文密钥（" + pending + "）。" : "")
                        + "需要输入一次访问口令才能解锁/迁移加密凭据库。"
                        + "换机后必须重输一次，这是「不明文存储」的必然代价。"));
                }
                else
                {
                    report.Steps.Add(MakeStep("凭据", "warn", "尚未设置 DeepSeek API Key，云端模型不可用（本地模型不受影响）。"));
                }
            }
            catch (Exception ex) { report.Steps.Add(MakeStep("凭据", "failed", ex.Message)); }

            // ---- 5) 本地模型端点探测 + provider 生成 ----
            if (oneClick || report.MachineChanged)
            {
                string outp = RunAutoConfigCaptured();
                if (outp == null)
                    report.Steps.Add(MakeStep("本地模型配置", "failed", "未能运行 scripts/auto-config.mjs。"));
                else
                {
                    string summary = SummarizeAutoConfig(outp);
                    report.Steps.Add(MakeStep("本地模型配置", summary.Length > 0 ? "fixed" : "ok",
                        summary.Length > 0 ? summary : "未探测到本地模型服务；已保持现有配置不变。"));
                }
            }
            else
            {
                report.Steps.Add(MakeStep("本地模型配置", "skipped", "仅在「一键配置」或换机后自动运行（避免每次启动改写配置）。"));
            }

            // ---- 落盘 ----
            if (changedKeys.Count > before)
            {
                try
                {
                    if (File.Exists(Paths.UserEnv)) File.Copy(Paths.UserEnv, Paths.UserEnv + ".bak", true);
                    cfg.Save();
                    report.Steps.Add(MakeStep("配置写入", "fixed", "已更新 " + changedKeys.Count + " 个路径项并写回 config\\user.env（旧文件已备份为 user.env.bak）。"));
                }
                catch (Exception ex) { report.Steps.Add(MakeStep("配置写入", "failed", ex.Message)); }
            }

            report.AllGood = true;
            foreach (SetupStep s in report.Steps)
                if (s.Status == "failed") report.AllGood = false;
            return report;
        }

        private static SetupStep MakeStep(string name, string status, string detail)
        {
            var s = new SetupStep();
            s.Name = name; s.Status = status; s.Detail = detail;
            return s;
        }

        private static bool DetectAndRecordMachineChange(string currentId)
        {
            try
            {
                string f = Path.Combine(Paths.ConfigDir, MACHINE_FILE);
                if (!File.Exists(f))
                {
                    File.WriteAllText(f, currentId, new UTF8Encoding(false));
                    return false;   // 首次记录，不算换机
                }
                string prev = File.ReadAllText(f, new UTF8Encoding(false)).Trim();
                if (!string.Equals(prev, currentId, StringComparison.OrdinalIgnoreCase))
                {
                    File.WriteAllText(f, currentId, new UTF8Encoding(false));
                    return true;
                }
            }
            catch { }
            return false;
        }

        private static bool RunBootstrap()
        {
            try
            {
                if (!File.Exists(Paths.BootstrapPs1)) return false;
                var psi = new System.Diagnostics.ProcessStartInfo("powershell.exe",
                    "-NoProfile -ExecutionPolicy Bypass -File \"" + Paths.BootstrapPs1 + "\"");
                psi.WorkingDirectory = Paths.Root;
                psi.EnvironmentVariables["PATH"] = Paths.NodeDir + ";" + Paths.GlobalDir + ";"
                    + Environment.GetEnvironmentVariable("PATH");
                string o, e;
                return Util.RunCaptured(psi, 900000, out o, out e);
            }
            catch { return false; }
        }

        /// <summary>驱动项目自带的 auto-config.mjs（不重复实现它的探测逻辑）。</summary>
        private static string RunAutoConfigCaptured()
        {
            try
            {
                if (!File.Exists(Paths.AutoConfigJs) || !File.Exists(Paths.NodeExe)) return null;
                var psi = new System.Diagnostics.ProcessStartInfo(Paths.NodeExe,
                    "\"" + Paths.AutoConfigJs + "\"");
                psi.WorkingDirectory = Paths.Root;
                psi.EnvironmentVariables["DSH_HOME"] = Paths.DshHome;
                psi.EnvironmentVariables["PATH"] = Paths.NodeDir + ";" + Paths.GlobalDir + ";"
                    + Environment.GetEnvironmentVariable("PATH");
                var cfg = new ConfigManager(Paths.UserEnv);
                foreach (string key in new string[] { "DSH_EXTRA_ENDPOINTS", "DSH_LLAMA_PORT",
                    "DSH_LLAMA_DIR", "DSH_LLAMA_MODEL", "DSH_OLLAMA_EXE", "DSH_OLLAMA_MODEL" })
                {
                    string v = cfg.Get(key, "");
                    if (v.Length > 0) psi.EnvironmentVariables[key] = v;
                }
                // 密钥只以环境变量形式交给脚本；脚本已改为不再落盘
                string apiKey;
                if (SecretStore.TryGet("DEEPSEEK_API_KEY", out apiKey) && apiKey.Length > 0)
                    psi.EnvironmentVariables["DEEPSEEK_API_KEY"] = apiKey;

                string o, e;
                Util.RunCaptured(psi, 180000, out o, out e);
                return o;
            }
            catch { return null; }
        }

        /// <summary>把 auto-config 的输出压缩成一行摘要给 UI。</summary>
        private static string SummarizeAutoConfig(string outp)
        {
            if (string.IsNullOrEmpty(outp)) return "";
            var parts = new List<string>();
            string[] lines = outp.Replace("\r\n", "\n").Split('\n');
            foreach (string raw in lines)
            {
                string l = raw.Trim();
                if (l.Length == 0) continue;
                if (l.StartsWith("- ")) { parts.Add(l.Substring(2)); continue; }
                if (l.StartsWith("Default model:")) parts.Add(l);
                if (l.IndexOf("... OK", StringComparison.OrdinalIgnoreCase) >= 0) parts.Add(l);
            }
            if (parts.Count == 0) return "";
            return "探测到 " + parts.Count + " 项：" + string.Join("；", parts.ToArray());
        }
    }
}
