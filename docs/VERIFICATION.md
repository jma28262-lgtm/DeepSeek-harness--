# 验证证据与复现步骤

本文件记录：**这些改动是怎么被验证的**，以及**你可以怎样自己复现这些验证**。
证据等级定义见 `HANDOVER.md` 开头。

---

## 1. 编译验证 `[实测]`

在 Android 设备上（mono + Roslyn csc）完成的编译验证，用于确认改动不会破坏构建。

### 验证方式

复刻 `launcher/build.ps1` 的编译语义：同样的源文件范围（`src/**/*.cs`）、
同样的输出类型（`/target:winexe /platform:x64 /optimize+`）、同样的引用集、
同样的 Win32 清单。

```bash
# 因 Android 侧读不到 root 拥有的目录，先把项目 bind mount 进 chroot
su -c 'mkdir -p /data/linux/debian/proj && mount --bind <项目目录> /data/linux/debian/proj'

# 用微软 4.8-api 参考程序集（保证公钥令牌与 Windows 一致）
su -c 'LINUX_ROOT=/data/linux/debian /data/linux/enter "cd /proj/src/launcher && \
  mono /root/roslyn/tools/csc.exe -nologo -nostdlib+ -target:winexe -platform:x64 -optimize+ \
    -win32manifest:/proj/src/launcher/app.manifest -out:/tmp/out.exe \
    -r:/usr/lib/mono/4.8-api/mscorlib.dll \
    -r:/usr/lib/mono/4.8-api/System.dll \
    -r:/usr/lib/mono/4.8-api/System.Core.dll \
    -r:/usr/lib/mono/4.8-api/System.Drawing.dll \
    -r:/usr/lib/mono/4.8-api/System.Windows.Forms.dll \
    -r:/usr/lib/mono/4.8-api/System.Web.Extensions.dll \
    -r:/usr/lib/mono/4.8-api/System.Security.dll \
    -r:/proj/Microsoft.Web.WebView2.Core.dll \
    -r:/proj/Microsoft.Web.WebView2.WinForms.dll \
    \$(find src -name \"*.cs\")"'
```

### 结果

```
=== 编译 ===
=== exit=0 ===        ← 零 error 零 warning
```

### 与「真 csc 构建的旧 exe」的结构对比 `[结构校验]`

```bash
ikdasm out.exe  | grep '^\.assembly extern'
ikdasm ref.exe  | grep '^\.assembly extern'
```

| 项 | 新产物 | 旧产物（真 csc） |
|---|---|---|
| 文件类型 | PE32+ MS Windows GUI, x86-64 .NET, 2 sections | 同 |
| `.imagebase` | `0x0000000140000000` | 同 |
| `.subsystem` | `0x0002` WINDOWS_GUI | 同 |
| `.corflags` | `0x00000001` ILONLY | 同 |
| `.entrypoint` | 有（`[STAThread]`，657 字节 Main） | 同 |
| Win32 清单 | `PerMonitorV2` + `asInvoker` | 同 |
| 程序集引用 | 9 个 | 8 个 |

**唯一的引用差异是新增 `System.Security`**（PKT `5F7F11D50A3A`），
因为新代码用 `ProtectedData` 做本机模式加密。其余 8 个引用的**公钥令牌逐一一致**。

---

## 2. 加密逻辑测试 `[实测]`

纯逻辑（密码学原语 + 状态机）可以用 mono 真正跑起来，因此这部分做的是**执行验证**。

```bash
# 把 SecretStore.cs + 一个 Paths stub + 测试 Main 一起编译成 exe，然后运行
mcs -out:/tmp/sstest.exe -r:System.Security.dll \
    SecretStore.cs Stub.cs Test.cs
mono /tmp/sstest.exe
```

### 覆盖的用例与结果（26/26 通过）

```
== 1. PBKDF2-HMAC-SHA256 标准向量 ==
  PASS  P=password S=salt c=1
  PASS  P=password S=salt c=2
  PASS  P=password S=salt c=4096
  PASS  P=passwordPASSWORDpassword S=saltSALT... c=4096
== 2. 口令模式：新建 / 落盘 / 无明文 ==
  PASS  用口令新建      PASS  保存
  PASS  文件无明文密钥   PASS  文件无明文口令
  PASS  声明口令模式     PASS  有 KDF 与迭代(200000)
== 3. 模拟换机（删掉本机解锁缓存）==
  PASS  换机后需要口令   PASS  错误口令被拒
  PASS  正确口令可解锁   PASS  值往返一致
  PASS  掩码不泄露全值
== 4. 篡改检测（独立目录）==
  PASS  被篡改的密文被拒绝（MAC 校验失败）
== 5. 改口令（独立目录）==
  PASS  改口令成功  PASS 旧口令失效  PASS 新口令可用  PASS 值仍在
== 5b. 重复 Init 不得丢失解锁状态（回归测试）==
  PASS  重复 Init 后仍处于解锁状态
  PASS  重复 Init 后值仍可读
  PASS  再次 Init 仍解锁
== 6. 本机模式（免口令）==
  PASS  切换到本机模式  PASS 无明文  PASS 可解  PASS 值正确
===== 全部通过 =====
```

**测试抓出的真 bug（已修）**：

1. `Unlock()` 在「用口令新建」路径上没置 `_loaded` → 随后 `Save()` 触发
   `EnsureLoaded() → Load()`，而 `Load()` 见文件不存在就把模式重置为 `None` → **新建即失败**
2. `Unlock()` 依赖「调用方先调 `Load()`」这个脆弱契约 → 直接调用时被误判为
   「当前不是口令模式」，**改口令整条链路失效**
3. `Init()` 非幂等，被 `SecretMigration` / `EnvironmentSetup` 在运行期重复调用时
   会清空内存解锁状态 → 若本机解锁缓存写不进去，**凭据库会莫名变回锁定**

---

## 3. 配置生成逻辑验证 `[实测]`

`scripts/auto-config.mjs` 的修复是用**复现 bug 的同一个实验**验证的：
在 `home/` 的副本上真跑一次脚本，对比前后文件。

```bash
P=<项目目录>; W=/tmp/acverify
rm -rf "$W"; mkdir -p "$W"; cp -r "$P/home/." "$W/"
cp "$W/.credentials.yaml" "$W/.credentials.before"

# 起 stub 模型服务，逼出「真正写入」的分支（否则脚本可能根本不写文件）
node -e 'require("http").createServer((q,s)=>{s.setHeader("content-type","application/json");
  s.end(JSON.stringify({data:[{id:"stub-model-7b"}]}))}).listen(11435,"127.0.0.1")' &
sleep 2

cd "$P" && DSH_HOME="$W" DEEPSEEK_API_KEY=sk-DUMMY DSH_EXTRA_ENDPOINTS='llama-server|http://127.0.0.1:11435|/v1' \
  node scripts/auto-config.mjs

# 断言
grep -q "client-connection/browser-session" "$W/.credentials.yaml"   # records 必须保留
grep -q "sk-DUMMY" "$W/.credentials.yaml" "$W/settings.yaml" && echo FAIL  # 不得有明文
grep -q "auto-extra1" "$W/settings.yaml"                             # provider 不得被删
```

### 结果

| 断言 | 修复前 | 修复后 |
|---|---|---|
| `records` 块是否保留 | ❌ **内容全被清空** | ✅ 逐字保留 |
| 是否写入明文密钥 | ❌ 写入 `refs:` | ✅ 只留 `apiKeyEnv` 引用 |
| 探测失败是否删 provider | ❌ 被删 | ✅ 保留并标记 `# probe: unreachable` |

`records` 块被清空这条，在修复前已**实测复现**（这是审查中最严重的 P0：
浏览器会话授权每次启动都丢失）。

---

## 4. 脚本层验证 `[实测]`

### `scan-missing.mjs`

```bash
node scripts/scan-missing.mjs /nonexistent-dir           # 应 exit=2 并明确报错
node scripts/scan-missing.mjs <真实 global 目录>          # 应正常扫描
node scripts/scan-missing.mjs <真实 global 目录> | grep -c '^INSTALL_LIST='
```

| 断言 | 修复前 | 修复后 |
|---|---|---|
| 目录不存在 | ❌ 假装成功（打印「无缺失 peer 依赖」） | ✅ `exit=2` + 明确错误 |
| `INSTALL_LIST=` 行首 | ❌ 前缀是字面 `\n`，PowerShell `-like` 永不匹配 | ✅ 在第 0 列 |
| `exports` 映射的包 | ❌ 误判为缺失 | ✅ 先试包入口 |

实测：真实目录下「已检查 532 个包，发现 3 个缺失 peer 依赖」。

### `bootstrap.ps1` / `update.ps1`

`[静态复核]` —— **没有执行验证**（本机无 PowerShell）。仅做了括号配平
（bootstrap 39/39、update 30/30）与逐行复核。**请在 Windows 上优先跑一次 `update.bat`。**

---

## 5. 前端界面验证 `[实测]`

`launcher/web` 是纯静态页面，用一个静态服务器在 Android 浏览器里真实渲染过，
并用无障碍读屏逐项核对文本内容。

```bash
# 任意静态服务器指向 launcher/web，然后浏览器打开
# 没有 WebView2 宿主时 bridge.js 会进入「演示模式」（顶部有提示条），用内置假数据渲染
```

### 核对结果

「环境配置」页 7 项状态值全部正确渲染、7 步报告的状态点与标签正确、
按钮齐全、演示模式提示条存在。

**这一步抓出过一个真 bug**：`BuildEnv` 只回了扁平的 `secretMode`，没回完整的
`secret` 对象，而页面读的是 `env.secret` → **状态行永远显示「未设置/未配置」**，
即使凭据已配好。已修。

---

## 6. 二进制产物验证 `[结构校验]`

交付的 `dist\DeepSeekHarness.exe` 在写入外接盘后重新校验过：

| 校验项 | 结果 |
|---|---|
| 类型 | PE32+ executable for MS Windows, x86-64 Mono/.Net assembly |
| 入口点 | 存在（1 个） |
| 程序集引用 | 9 个，含 `System.Security` |
| Win32 清单 | `PerMonitorV2` 存在 |
| 与本地构建副本大小 | 89600 字节，一致 |

---

## 7. 未做的验证（**请勿假定已通过**）

| 项 | 状态 | 建议的验证方式 |
|---|---|---|
| **exe 能否在 Windows 上运行** | `[未验证]` | 双击运行；起不来就按 `HANDOVER.md` §7 回滚 |
| WinForms 界面渲染 | `[未验证]` | 看是否无「演示模式」提示条 |
| WebView2 集成 | `[未验证]` | 看 dsh 的 Web UI 能否在内嵌 iframe 里加载 |
| DPAPI 实际行为 | `[未验证]` | 设一次本机模式密钥，重启看能否自动解开 |
| Job Object 是否真的回收子进程 | `[未验证]` | 启动服务 → 关窗口 → 任务管理器查 `node.exe` 残留 |
| `bootstrap.ps1` / `update.ps1` | `[静态复核]` | 跑一次 `update.bat` |
| `renameSync` 在 Windows 的覆盖语义 | `[静态复核]` | 跑一次一键配置，看 `settings.yaml` 是否正常写入 |
| 150% DPI 下浮层定位 | `[未验证]` | 在 150% 缩放机器上打开左下角状态卡浮层 |
