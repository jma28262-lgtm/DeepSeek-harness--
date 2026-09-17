using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace DeepSeekHarnessLauncher
{
    /// <summary>
    /// 凭据库。两种存储模式，磁盘上都不出现明文：
    ///
    ///   口令模式（默认，跨机可用）
    ///     密钥材料由口令经 PBKDF2-HMAC-SHA256 派生，secrets.dat 用 AES-256-CBC 加密，
    ///     并以 HMAC-SHA256 做 encrypt-then-MAC 认证。整个文件可以随 U 盘搬到任何电脑，
    ///     在每台新机器上输入一次口令即可解开；解开后本机会用 DPAPI 缓存密钥材料
    ///     （secrets.unlock），所以同一台机器后续启动无需再输入。
    ///
    ///   本机模式（免口令）
    ///     直接用 DPAPI(CurrentUser) 保护，零输入，但**只能在本机本账户解开**，换机必须重填。
    ///
    /// 为什么会这样：若要让"换机零输入"成立，解密密钥就必须和密文一起放在盘上，
    /// 那等价于明文，只是看起来像加密。因此跨机必然需要一次口令输入 —— 这是约束，不是取舍。
    /// </summary>
    public static class SecretStore
    {
        public enum Mode { None, Passphrase, Dpapi }

        public enum LoadStatus
        {
            Ok,               // 已解开，可用
            Empty,            // 库不存在 —— 首次运行，正常
            NeedsPassphrase,  // 口令模式且本机没有解锁缓存 —— 需要用户输入一次口令
            BadPassphrase,    // 口令错误（MAC 校验失败）
            Corrupted,        // 文件损坏或格式不认识
            NoMode            // 尚未选择存储模式
        }

        private const int KDF_ITERATIONS = 200000;   // PBKDF2 迭代次数
        private const int KEY_LEN = 64;              // 32B 加密密钥 + 32B MAC 密钥
        private const int SALT_LEN = 16;
        private const int IV_LEN = 16;
        private const string ENTROPY_TAG = "DeepSeekHarness.SecretStore.v2";

        private static readonly object _lock = new object();
        private static Dictionary<string, string> _values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static string _dir;
        private static string _file;        // secrets.dat
        private static string _unlockFile;  // secrets.unlock（本机 DPAPI 缓存）
        private static byte[] _keyMaterial; // 64 字节；非 null 表示已解锁
        private static byte[] _salt;        // 口令模式下当前文件的盐
        private static int _iterations = KDF_ITERATIONS;
        private static Mode _mode = Mode.None;
        private static bool _loaded;

        public static string LastError { get; private set; }
        public static string FilePath { get { return _file; } }
        public static Mode CurrentMode { get { lock (_lock) { return _mode; } } }
        public static bool IsUnlocked { get { lock (_lock) { return _keyMaterial != null; } } }

        public static void Init(string configDir)
        {
            lock (_lock)
            {
                _dir = configDir;
                if (!Directory.Exists(_dir)) Directory.CreateDirectory(_dir);
                _file = Path.Combine(_dir, "secrets.dat");
                _unlockFile = Path.Combine(_dir, "secrets.unlock");
                _values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                _keyMaterial = null;
                _salt = null;
                _mode = Mode.None;
                _loaded = false;
                LastError = null;
            }
        }

        // =================================================================
        //  载入
        // =================================================================
        public static LoadStatus Load()
        {
            lock (_lock)
            {
                _loaded = true;
                LastError = null;

                if (string.IsNullOrEmpty(_file) || !File.Exists(_file))
                {
                    _mode = Mode.None;
                    return LoadStatus.Empty;
                }

                Dictionary<string, string> header;
                try { header = ReadHeader(_file); }
                catch (Exception ex) { LastError = "读取凭据库失败：" + ex.Message; return LoadStatus.Corrupted; }

                // ---- 本机模式：直接用 DPAPI ----
                if (Get(header, "mode") == "dpapi")
                {
                    _mode = Mode.Dpapi;
                    byte[] blob;
                    try { blob = Convert.FromBase64String(Get(header, "data")); }
                    catch { LastError = "本机模式凭据库的 data 字段不是合法 Base64。"; return LoadStatus.Corrupted; }
                    string plain;
                    if (!TryDpapiUnprotect(blob, out plain))
                    {
                        LastError = "本机模式凭据库无法在当前 Windows 用户/这台电脑上解密。"
                                  + "这是本机模式的固有限制（DPAPI 绑定用户账户）。"
                                  + "如需换机可用，请改用口令模式。";
                        return LoadStatus.NeedsPassphrase;
                    }
                    ParsePayload(plain);
                    return LoadStatus.Ok;
                }

                if (Get(header, "mode") != "passphrase")
                {
                    LastError = "凭据库 mode 字段无法识别：" + Get(header, "mode");
                    return LoadStatus.Corrupted;
                }

                // ---- 口令模式 ----
                _mode = Mode.Passphrase;
                try
                {
                    _salt = Convert.FromBase64String(Get(header, "salt"));
                    string it = Get(header, "iterations");
                    int parsed;
                    _iterations = (it != null && int.TryParse(it, out parsed) && parsed > 0) ? parsed : KDF_ITERATIONS;
                }
                catch { LastError = "凭据库参数解析失败。"; return LoadStatus.Corrupted; }

                // 先试本机解锁缓存（同一台机器免密）
                byte[] cached;
                if (TryDpapiUnprotect(ReadAllBytesOrNull(_unlockFile), out cached))
                {
                    string plain;
                    if (TryDecryptWith(header, cached, out plain))
                    {
                        _keyMaterial = cached;
                        ParsePayload(plain);
                        return LoadStatus.Ok;
                    }
                    // 缓存与当前 secrets.dat 不匹配（例如口令改过、文件被替换）——丢掉缓存，要求口令
                    try { File.Delete(_unlockFile); } catch { }
                }

                return LoadStatus.NeedsPassphrase;
            }
        }

        /// <summary>用口令解锁（口令模式）。成功后会写入本机解锁缓存，后续启动免输入。</summary>
        public static LoadStatus Unlock(string passphrase)
        {
            lock (_lock)
            {
                // 若尚未载入，先从文件头读出 mode / salt / iterations。
                // 不能依赖"调用方一定会先调 Load()" —— 那是脆弱契约（测试已踩到）。
                if (!_loaded) Load();
                // 标记为已载入。否则后续 Save() 里的 EnsureLoaded() 会再走一次 Load()，
                // 而 Load() 见到 secrets.dat 不存在会把 _mode 重置为 None，导致新建即失败。
                _loaded = true;
                LastError = null;
                if (string.IsNullOrEmpty(_file) || !File.Exists(_file))
                {
                    // 还没有库 —— 用这个口令新建
                    _mode = Mode.Passphrase;
                    _salt = RandomBytes(SALT_LEN);
                    _iterations = KDF_ITERATIONS;
                    _keyMaterial = Derive(passphrase, _salt, _iterations);
                    WriteUnlockCache(_keyMaterial);
                    return LoadStatus.Ok;
                }
                if (_mode != Mode.Passphrase)
                {
                    LastError = "当前不是口令模式，无需解锁。";
                    return LoadStatus.Corrupted;
                }
                if (_salt == null) { LastError = "凭据库未载入。"; return LoadStatus.Corrupted; }

                Dictionary<string, string> header = ReadHeader(_file);
                byte[] km = Derive(passphrase, _salt, _iterations);
                string plain;
                if (!TryDecryptWith(header, km, out plain))
                {
                    LastError = "口令错误（认证标签不匹配）。";
                    return LoadStatus.BadPassphrase;
                }
                _keyMaterial = km;
                WriteUnlockCache(km);
                ParsePayload(plain);
                return LoadStatus.Ok;
            }
        }

        /// <summary>丢弃本机解锁缓存（下次启动需要重新输入口令）。</summary>
        public static void ForgetUnlockCache()
        {
            lock (_lock)
            {
                try { if (File.Exists(_unlockFile)) File.Delete(_unlockFile); } catch { }
                _keyMaterial = null;
            }
        }

        // =================================================================
        //  读取 / 修改
        // =================================================================
        private static void EnsureLoaded()
        {
            if (!_loaded) Load();
        }

        public static bool Has(string name)
        {
            lock (_lock)
            {
                EnsureLoaded();
                return !string.IsNullOrEmpty(name) && _values.ContainsKey(name);
            }
        }

        public static bool TryGet(string name, out string value)
        {
            lock (_lock)
            {
                EnsureLoaded();
                if (!string.IsNullOrEmpty(name) && _values.TryGetValue(name, out value)) return true;
                value = null;
                return false;
            }
        }

        public static string MaskedHint(string name)
        {
            string v;
            if (!TryGet(name, out v) || string.IsNullOrEmpty(v)) return "";
            if (v.Length <= 10) return new string('*', v.Length);
            return v.Substring(0, 5) + new string('*', 6) + v.Substring(v.Length - 4);
        }

        public static void Set(string name, string value)
        {
            if (string.IsNullOrEmpty(name)) return;
            lock (_lock)
            {
                EnsureLoaded();
                _values[name] = value == null ? "" : value;
            }
        }

        public static void Remove(string name)
        {
            lock (_lock)
            {
                EnsureLoaded();
                _values.Remove(name);
            }
        }

        public static string[] Names()
        {
            lock (_lock)
            {
                EnsureLoaded();
                var list = new List<string>(_values.Keys);
                list.Sort(StringComparer.OrdinalIgnoreCase);
                return list.ToArray();
            }
        }

        // =================================================================
        //  保存
        // =================================================================
        public static bool Save()
        {
            lock (_lock)
            {
                EnsureLoaded();
                LastError = null;

                if (_mode == Mode.None)
                {
                    LastError = "尚未选择凭据存储模式（口令模式 / 本机模式）。";
                    return false;
                }
                if (_keyMaterial == null)
                {
                    LastError = "凭据库未解锁，无法保存。";
                    return false;
                }

                string payload = BuildPayload();
                var sb = new StringBuilder();
                sb.AppendLine("# DeepSeek Harness - encrypted credentials (managed by DeepSeekHarness.exe)");
                sb.AppendLine("# NEVER put plaintext here.");
                sb.AppendLine("version=1");

                if (_mode == Mode.Dpapi)
                {
                    byte[] blob;
                    if (!TryDpapiProtect(payload, out blob))
                    {
                        LastError = "DPAPI 加密失败：" + (LastError == null ? "未知" : LastError);
                        return false;
                    }
                    sb.AppendLine("mode=dpapi");
                    sb.AppendLine("# 本机模式：只能在本机本账户解开，换机需重新输入。");
                    sb.AppendLine("data=" + Convert.ToBase64String(blob));
                }
                else
                {
                    if (_salt == null) _salt = RandomBytes(SALT_LEN);
                    byte[] iv = RandomBytes(IV_LEN);
                    byte[] encKey, macKey;
                    SplitKeys(_keyMaterial, out encKey, out macKey);
                    byte[] cipher = AesCbcEncrypt(encKey, iv, Encoding.UTF8.GetBytes(payload));
                    byte[] mac = Hmac(macKey, Concat(iv, cipher));
                    sb.AppendLine("mode=passphrase");
                    sb.AppendLine("kdf=PBKDF2-HMAC-SHA256");
                    sb.AppendLine("iterations=" + _iterations);
                    sb.AppendLine("salt=" + Convert.ToBase64String(_salt));
                    sb.AppendLine("iv=" + Convert.ToBase64String(iv));
                    sb.AppendLine("data=" + Convert.ToBase64String(cipher));
                    sb.AppendLine("mac=" + Convert.ToBase64String(mac));
                }

                try
                {
                    WriteAtomic(_file, sb.ToString());
                    if (_mode == Mode.Passphrase) WriteUnlockCache(_keyMaterial);
                    return true;
                }
                catch (Exception ex) { LastError = "写入凭据库失败：" + ex.Message; return false; }
            }
        }

        /// <summary>切换到本机模式（免口令，但不可换机）。</summary>
        public static bool SwitchToDpapiMode()
        {
            lock (_lock)
            {
                EnsureLoaded();
                _mode = Mode.Dpapi;
                _keyMaterial = new byte[KEY_LEN];   // 占位：本机模式不使用派生密钥
                try { if (File.Exists(_unlockFile)) File.Delete(_unlockFile); } catch { }
                return Save();
            }
        }

        /// <summary>切换到口令模式并设定口令。</summary>
        public static bool SwitchToPassphraseMode(string passphrase)
        {
            lock (_lock)
            {
                EnsureLoaded();
                if (string.IsNullOrEmpty(passphrase) || passphrase.Length < 6)
                {
                    LastError = "口令太短（至少 6 个字符）。";
                    return false;
                }
                _mode = Mode.Passphrase;
                _salt = RandomBytes(SALT_LEN);
                _iterations = KDF_ITERATIONS;
                _keyMaterial = Derive(passphrase, _salt, _iterations);
                return Save();
            }
        }

        /// <summary>改口令：先用旧口令解开（或已解锁），再用新口令重写。</summary>
        public static bool ChangePassphrase(string newPassphrase)
        {
            lock (_lock)
            {
                EnsureLoaded();
                if (_keyMaterial == null) { LastError = "请先解锁。"; return false; }
                if (string.IsNullOrEmpty(newPassphrase) || newPassphrase.Length < 6)
                {
                    LastError = "新口令太短（至少 6 个字符）。";
                    return false;
                }
                _mode = Mode.Passphrase;
                _salt = RandomBytes(SALT_LEN);
                _iterations = KDF_ITERATIONS;
                _keyMaterial = Derive(newPassphrase, _salt, _iterations);
                return Save();
            }
        }

        private static string BuildPayload()
        {
            var sb = new StringBuilder();
            foreach (KeyValuePair<string, string> kv in _values) sb.AppendLine(kv.Key + "=" + kv.Value);
            return sb.ToString();
        }

        private static void ParsePayload(string payload)
        {
            var parsed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrEmpty(payload))
            {
                string[] lines = payload.Replace("\r\n", "\n").Split('\n');
                foreach (string raw in lines)
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line[0] == '#') continue;
                    int idx = line.IndexOf('=');
                    if (idx <= 0) continue;
                    parsed[line.Substring(0, idx).Trim()] = line.Substring(idx + 1);
                }
            }
            _values = parsed;
        }

        // =================================================================
        //  加解密原语
        // =================================================================
        /// <summary>PBKDF2-HMAC-SHA256。自己实现是为了不依赖框架版本差异（.NET 4.7.2+ 才有 HashAlgorithmName 重载）。</summary>
        public static byte[] Derive(string passphrase, byte[] salt, int iterations)
        {
            using (var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(passphrase == null ? "" : passphrase)))
            {
                int hLen = hmac.HashSize / 8;
                int blocks = (KEY_LEN + hLen - 1) / hLen;
                byte[] dk = new byte[blocks * hLen];
                byte[] saltBlock = new byte[salt.Length + 4];
                Buffer.BlockCopy(salt, 0, saltBlock, 0, salt.Length);
                for (int i = 1; i <= blocks; i++)
                {
                    saltBlock[salt.Length] = (byte)(i >> 24);
                    saltBlock[salt.Length + 1] = (byte)(i >> 16);
                    saltBlock[salt.Length + 2] = (byte)(i >> 8);
                    saltBlock[salt.Length + 3] = (byte)i;
                    byte[] u = hmac.ComputeHash(saltBlock);
                    byte[] t = (byte[])u.Clone();
                    for (int j = 1; j < iterations; j++)
                    {
                        u = hmac.ComputeHash(u);
                        for (int k = 0; k < hLen; k++) t[k] ^= u[k];
                    }
                    Buffer.BlockCopy(t, 0, dk, (i - 1) * hLen, hLen);
                }
                byte[] result = new byte[KEY_LEN];
                Buffer.BlockCopy(dk, 0, result, 0, KEY_LEN);
                return result;
            }
        }

        private static void SplitKeys(byte[] km, out byte[] encKey, out byte[] macKey)
        {
            encKey = new byte[32];
            macKey = new byte[32];
            Buffer.BlockCopy(km, 0, encKey, 0, 32);
            Buffer.BlockCopy(km, 32, macKey, 0, 32);
        }

        private static byte[] AesCbcEncrypt(byte[] key, byte[] iv, byte[] plain)
        {
            using (var aes = new AesCryptoServiceProvider())
            {
                aes.KeySize = 256; aes.BlockSize = 128;
                aes.Mode = CipherMode.CBC; aes.Padding = PaddingMode.PKCS7;
                aes.Key = key; aes.IV = iv;
                using (var enc = aes.CreateEncryptor())
                    return enc.TransformFinalBlock(plain, 0, plain.Length);
            }
        }

        private static byte[] AesCbcDecrypt(byte[] key, byte[] iv, byte[] cipher)
        {
            using (var aes = new AesCryptoServiceProvider())
            {
                aes.KeySize = 256; aes.BlockSize = 128;
                aes.Mode = CipherMode.CBC; aes.Padding = PaddingMode.PKCS7;
                aes.Key = key; aes.IV = iv;
                using (var dec = aes.CreateDecryptor())
                    return dec.TransformFinalBlock(cipher, 0, cipher.Length);
            }
        }

        private static byte[] Hmac(byte[] key, byte[] data)
        {
            using (var h = new HMACSHA256(key)) return h.ComputeHash(data);
        }

        /// <summary>常量时间比较，避免 MAC 校验被计时侧信道利用。</summary>
        public static bool FixedTimeEquals(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            int diff = 0;
            for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
            return diff == 0;
        }

        private static bool TryDecryptWith(Dictionary<string, string> header, byte[] km, out string plain)
        {
            plain = null;
            try
            {
                byte[] iv = Convert.FromBase64String(Get(header, "iv"));
                byte[] data = Convert.FromBase64String(Get(header, "data"));
                byte[] mac = Convert.FromBase64String(Get(header, "mac"));
                byte[] encKey, macKey;
                SplitKeys(km, out encKey, out macKey);
                byte[] expected = Hmac(macKey, Concat(iv, data));
                if (!FixedTimeEquals(expected, mac)) return false;   // 先认证后解密
                plain = Encoding.UTF8.GetString(AesCbcDecrypt(encKey, iv, data));
                return true;
            }
            catch { return false; }
        }

        private static byte[] Concat(byte[] a, byte[] b)
        {
            byte[] r = new byte[a.Length + b.Length];
            Buffer.BlockCopy(a, 0, r, 0, a.Length);
            Buffer.BlockCopy(b, 0, r, a.Length, b.Length);
            return r;
        }

        private static byte[] RandomBytes(int n)
        {
            byte[] b = new byte[n];
            using (var rng = new RNGCryptoServiceProvider()) rng.GetBytes(b);
            return b;
        }

        // =================================================================
        //  本机 DPAPI 辅助（只用于解锁缓存与本机模式）
        // =================================================================
        private static byte[] Entropy()
        {
            return Encoding.UTF8.GetBytes(ENTROPY_TAG);
        }

        private static bool TryDpapiProtect(string plain, out byte[] blob)
        {
            blob = null;
            try
            {
                blob = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), Entropy(), DataProtectionScope.CurrentUser);
                return true;
            }
            catch { return false; }
        }

        private static bool TryDpapiProtect(byte[] raw, out byte[] blob)
        {
            blob = null;
            try
            {
                blob = ProtectedData.Protect(raw, Entropy(), DataProtectionScope.CurrentUser);
                return true;
            }
            catch { return false; }
        }

        private static bool TryDpapiUnprotect(byte[] blob, out string plain)
        {
            plain = null;
            byte[] raw;
            if (!TryDpapiUnprotect(blob, out raw)) return false;
            plain = Encoding.UTF8.GetString(raw);
            return true;
        }

        private static bool TryDpapiUnprotect(byte[] blob, out byte[] raw)
        {
            raw = null;
            if (blob == null || blob.Length == 0) return false;
            try
            {
                raw = ProtectedData.Unprotect(blob, Entropy(), DataProtectionScope.CurrentUser);
                return true;
            }
            catch { return false; }
        }

        private static void WriteUnlockCache(byte[] keyMaterial)
        {
            try
            {
                byte[] blob;
                if (TryDpapiProtect(keyMaterial, out blob))
                    WriteAtomic(_unlockFile, Convert.ToBase64String(blob));
            }
            catch { }
        }

        private static byte[] ReadAllBytesOrNull(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
                string txt = File.ReadAllText(path, new UTF8Encoding(false)).Trim();
                return Convert.FromBase64String(txt);
            }
            catch { return null; }
        }

        private static void WriteAtomic(string path, string content)
        {
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, content, new UTF8Encoding(false));
            if (File.Exists(path))
            {
                try { File.Replace(tmp, path, null); }
                catch { File.Copy(tmp, path, true); File.Delete(tmp); }
            }
            else File.Move(tmp, path);
        }

        // =================================================================
        //  头部解析
        // =================================================================
        private static Dictionary<string, string> ReadHeader(string path)
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string[] lines = File.ReadAllLines(path, new UTF8Encoding(false));
            foreach (string raw in lines)
            {
                string line = (raw == null ? "" : raw).Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                int idx = line.IndexOf('=');
                if (idx <= 0) continue;
                d[line.Substring(0, idx).Trim()] = line.Substring(idx + 1).Trim();
            }
            return d;
        }

        private static string Get(Dictionary<string, string> d, string k)
        {
            string v;
            return (d != null && d.TryGetValue(k, out v)) ? v : null;
        }
    }
}

namespace DeepSeekHarnessLauncher
{
    /// <summary>
    /// 明文凭据的一次性迁移：把历史遗留在 config\user.env 与 home\.credentials.yaml 中的
    /// 明文密钥收进加密凭据库，并从原文件里抹掉。
    ///
    /// 为什么要迁移（实测）：这两处原本都是明文，而整个部署目录随 U 盘移动，
    /// 且 Windows 上 dsh 自身不做权限校验 —— 明文等于"跟着盘走"。
    ///
    /// 前置条件：凭据库必须已解锁（已设口令或已选本机模式）。
    /// 未解锁时不改动任何文件，只报告"待解锁后迁移"，绝不为了迁移而把明文留在别处。
    /// </summary>
    public static class SecretMigration
    {
        /// <summary>需要收进加密库的键。新增密钥时在这里登记。</summary>
        public static readonly string[] SecretKeys = new string[] { "DEEPSEEK_API_KEY" };

        public static bool IsSecret(string key)
        {
            if (string.IsNullOrEmpty(key)) return false;
            foreach (string k in SecretKeys)
                if (string.Equals(k, key, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>执行迁移，返回人类可读的报告行（供 UI 与日志展示）。幂等。</summary>
        public static string[] Run()
        {
            var report = new System.Collections.Generic.List<string>();
            try
            {
                if (Paths.ConfigDir == null) return report.ToArray();
                SecretStore.Init(Paths.ConfigDir);
                SecretStore.Load();

                if (!SecretStore.IsUnlocked)
                {
                    // 库里已有内容但本机解不开，或尚未选择模式 —— 都不能迁移
                    string pending = FindPlaintext();
                    if (pending != null)
                        report.Add("检测到明文密钥（" + pending + "），但凭据库尚未解锁，暂不迁移。"
                                 + "请在「环境配置」里设置口令或选择本机模式后重试。");
                    return report.ToArray();
                }

                bool changed = false;

                // ---- 1) config\user.env ----
                string userEnv = Paths.UserEnv;
                if (File.Exists(userEnv))
                {
                    string[] lines = File.ReadAllLines(userEnv, new UTF8Encoding(false));
                    var kept = new System.Collections.Generic.List<string>();
                    foreach (string raw in lines)
                    {
                        string line = raw == null ? "" : raw;
                        string trimmed = line.Trim();
                        int idx = trimmed.IndexOf('=');
                        if (idx > 0 && !trimmed.StartsWith("#"))
                        {
                            string k = trimmed.Substring(0, idx).Trim();
                            if (IsSecret(k))
                            {
                                string v = trimmed.Substring(idx + 1).Trim().Trim('"');
                                if (v.Length > 0 && !SecretStore.Has(k))
                                {
                                    SecretStore.Set(k, v);
                                    report.Add("已把 " + k + " 从 config\\user.env 移入加密凭据库");
                                }
                                else if (v.Length > 0)
                                {
                                    report.Add("已丢弃 config\\user.env 中的重复明文 " + k + "（加密库已有）");
                                }
                                changed = true;
                                continue;   // 该行不再保留 —— 这是"抹掉明文"的关键
                            }
                        }
                        kept.Add(line);
                    }
                    if (changed)
                    {
                        SecretStore.Save();
                        WriteAtomic(userEnv, string.Join(Environment.NewLine, kept.ToArray()) + Environment.NewLine);
                    }
                }

                // ---- 2) home\.credentials.yaml 的 refs 段 ----
                string cred = Path.Combine(Paths.DshHome, ".credentials.yaml");
                if (File.Exists(cred))
                {
                    string[] lines = File.ReadAllLines(cred, new UTF8Encoding(false));
                    var kept = new System.Collections.Generic.List<string>();
                    bool inRefs = false, credChanged = false;
                    foreach (string raw in lines)
                    {
                        string line = raw == null ? "" : raw;
                        string trimmed = line.Trim();
                        if (trimmed.Length == 0) { kept.Add(line); continue; }
                        if (!char.IsWhiteSpace(line[0]))
                        {
                            inRefs = trimmed.StartsWith("refs:");
                            kept.Add(line);
                            continue;
                        }
                        if (inRefs)
                        {
                            int idx = trimmed.IndexOf(':');
                            if (idx > 0)
                            {
                                string k = trimmed.Substring(0, idx).Trim().Trim('"');
                                if (IsSecret(k))
                                {
                                    // dsh 的 credentials 解析顺序是 env -> 文件 -> dotenv，
                                    // 启动时注入的环境变量优先级更高，所以删掉不影响功能，反而消除明文。
                                    if (!SecretStore.Has(k))
                                    {
                                        string v = trimmed.Substring(idx + 1).Trim().Trim('"');
                                        if (v.Length > 0) SecretStore.Set(k, v);
                                    }
                                    credChanged = true;
                                    report.Add("已从 home\\.credentials.yaml 移除明文 " + k
                                             + "（改由启动时注入环境变量）");
                                    continue;
                                }
                            }
                        }
                        kept.Add(line);
                    }
                    if (credChanged)
                    {
                        SecretStore.Save();
                        File.Copy(cred, cred + ".bak", true);
                        WriteAtomic(cred, string.Join(Environment.NewLine, kept.ToArray()) + Environment.NewLine);
                    }
                }
            }
            catch (Exception ex)
            {
                report.Add("凭据迁移失败：" + ex.Message);
            }
            return report.ToArray();
        }

        /// <summary>返回第一个仍在明文里的密钥位置描述；没有则返回 null。用于提示用户。</summary>
        public static string FindPlaintext()
        {
            try
            {
                if (File.Exists(Paths.UserEnv))
                {
                    foreach (string raw in File.ReadAllLines(Paths.UserEnv, new UTF8Encoding(false)))
                    {
                        string t = (raw == null ? "" : raw).Trim();
                        int idx = t.IndexOf('=');
                        if (idx > 0 && !t.StartsWith("#") && IsSecret(t.Substring(0, idx).Trim()))
                            return "config\\user.env";
                    }
                }
                string cred = Path.Combine(Paths.DshHome, ".credentials.yaml");
                if (File.Exists(cred))
                {
                    bool inRefs = false;
                    foreach (string raw in File.ReadAllLines(cred, new UTF8Encoding(false)))
                    {
                        string line = raw == null ? "" : raw;
                        string t = line.Trim();
                        if (t.Length == 0) continue;
                        if (!char.IsWhiteSpace(line[0])) { inRefs = t.StartsWith("refs:"); continue; }
                        if (!inRefs) continue;
                        int idx = t.IndexOf(':');
                        if (idx > 0 && IsSecret(t.Substring(0, idx).Trim().Trim('"'))) return "home\\.credentials.yaml";
                    }
                }
            }
            catch { }
            return null;
        }

        private static void WriteAtomic(string path, string content)
        {
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, content, new UTF8Encoding(false));
            if (File.Exists(path))
            {
                try { File.Replace(tmp, path, null); }
                catch { File.Copy(tmp, path, true); File.Delete(tmp); }
            }
            else File.Move(tmp, path);
        }
    }
}
