# DeepSeekHarness 代码审计与优化方案

审计对象：外接盘 `deepseek-harness/`（803MB，已拷源码到本地）
审计范围：C# 宿主 6 个文件 3631 行 + 前端 4 个文件 1875 行 + 脚本/MCP 9 个文件 1645 行
方法：通读源码 + 交叉验证 + **两条 P0 在副本上实际复现**（未改动任何原文件）

---

## 0. 结论摘要

**这是一个完成度不低的项目**：便携路径解析、环境变量兜底、模型端点探测的兼容性处理、
`update.ps1` 的 npm 调用写法，都达到了"懂行的人写的"水准。它的问题不在能力，而在**一致性**：

| 症状 | 根因 |
|---|---|
| 同一份配置有 3 个写入者互相覆盖 | 没有单一真源 |
| 凭据每次启动被抹掉 | 用行级正则手术改 YAML，而 YAML 是嵌套结构 |
| 点"启动服务"可能卡死 10 分钟 | 编排逻辑跑在 UI 线程 |
| 关掉程序后模型服务还占着显存 | 没有进程树托管 |
| 1138 行的入口文件其实没参与编译 | 新旧实现并存，死文件未清理 |
| 两条启动路径行为不一致 | exe 绕开 start.ps1 自己起 node |

**最紧急的一条：`auto-config.mjs` 每次运行都会清空 `home/.credentials.yaml` 的 `records` 块。
我已复现。** 只要用 `start.bat` 启动，浏览器会话授权每启动一次丢一次。

---

## 1. 项目实况

### 1.1 两条互相分歧的启动路径

```
路径 A（exe，推荐入口）
  DeepSeekHarness.exe  (WinForms + WebView2, 63KB)
    └─ src/MainForm.cs → Services.cs DshService.Start()
         └─ 直接: tools\node\node.exe <global>\@deepseek-ai\dsh\lib\bin.js web --port N --no-open
         不读 user.env 里的模型配置、不跑 auto-config、不跑 start-model-server.ps1

路径 B（命令行备用）
  start.bat → start.ps1
    ├─ 读 user.env
    ├─ [可选] scripts\start-model-server.ps1   （拉 llama-server / ollama）
    ├─ scripts\auto-config.mjs                 （探测端点 + 改写 settings.yaml / .credentials.yaml）
    └─ tools\global\dsh.cmd web --port N --no-open
```

两条路径的能力集合**不重合**：A 有托盘/WebView2/插件管理但不跑 auto-config 与模型自动拉起；
B 有模型编排但不做端口探测（`Services.cs:275` 的 `DetectAvailablePort` A 才有）。
用户从哪个入口启动，得到的行为不同。

### 1.2 `launcher/Program.cs`（1138 行）是死代码

`launcher/build.ps1:22` 只收集 `launcher/src/**/*.cs`：

```powershell
$SrcDir = Join-Path $PSScriptRoot "src"
$Sources = Get-ChildItem -Path $SrcDir -Recurse -Filter "*.cs" | ...
```

而 `launcher/Program.cs` 在 `launcher/` 下**不在 src 里** → 不参与编译。
时间戳也印证了它被 `src/` 取代：`DeepSeekHarness.exe.old`（41984B, 08-27 15:06）
与 `Program.cs` 的最后修改时间完全相同，之后才有 `src/` 拆分与 `.bak`（62464B, 08-27 23:16）、
当前 exe（62976B, 09-17 10:59，晚于 `Services.cs` 的 09-17 10:57）。

它里面是旧版单文件实现：自己的 `MainForm`、自己的 `WriteUserEnv`、
自己的 `taskkill /PID /T /F`。**它含与 src/ 不同的逻辑**（例如 `Program.cs:1089` 起 powershell 的
PATH 拼接未加引号），任何人照着它改 bug 都是白改。审计过程中已有一位同事把它当成"真正入口"。

> 建议：移到 `launcher/legacy/` 或删除，并在 `build.ps1` 顶部注明"只编译 src/"。

---

## 2. 已亲手复现的缺陷（最高置信度）

方法：把 `home/` 拷贝到临时目录，令 `DSH_HOME` 指向副本，真跑一次 `scripts/auto-config.mjs`，
对比前后文件。**原文件未改动**，测试目录（含密钥副本）已删除。

### 2.1 【P0】`records` 块被整块清空 —— 凭据持续丢失

运行前：

```yaml
version: 1
refs:
  AUTO_EXTRA1_API_KEY: "local"
  DEEPSEEK_API_KEY: sk-****
records:
  {
    client-connection/browser-session:
      {
        kind: grant,
        payload: { version: 1, secret: ****
      }
  }
```

运行一次 `auto-config.mjs` 后：

```yaml
version: 1
refs:
  AUTO_EXTRA1_API_KEY: "local"
  DEEPSEEK_API_KEY: "sk-****"
records:          ← 内容全部消失
```

**影响**：浏览器会话授权（`/api` 的 browser-trust fence 依赖的 grant）每次启动丢失，
用户需反复重新授权；任何其他凭据记录同样丢失。
**触发条件**：走 `start.bat` 启动（`start.ps1:112-120` 每次启动都跑 auto-config）。

**根因**（`scripts/auto-config.mjs:326-346`）：用逐行正则解析 YAML，且把 `records` 定义为
"紧跟其后的行都属于 records"：

```js
if (/^records:\s*/.test(t)) { inRefs = false; recordsLine = t; continue; }  // 只保留 "records:" 这一行
if (inRefs && t) { ... }                                                   // records 之后的行落空 → 丢弃
```

`records` 的实际结构是 `<scope>/<id>` 嵌套映射，含 `{`、`/`、`-`、`kind:`、`payload:` 等，
匹配不上 `:332` 那个 `[A-Za-z_][A-Za-z0-9_]*` 的键正则，于是全部落入丢弃分支，
`:345` 只回写一行 `records:`。

**对比**：DSH 自己是 `withFileLock` + `writeFileAtomic` 写这个文件（`dsh-credentials-local`、
`dsh-settings-file` 均导入 `@deepseek-ai/dsh-atomic-write`）；这里是裸 `fs.writeFileSync`
（`:348`/`:384`），先截断再写、无临时文件、无 rename、无锁。

**修法**：不要重建整份文档。DSH 的依赖树里已有 `yaml`，改成
`YAML.parse(text)` → 只改自己管理的 `refs` → `YAML.stringify()`；写入走
`writeFileSync(tmp)` + `renameSync(tmp, target)`；至少也要把 `records:` 之后直到下一个
顶层键的所有行原样保留。

**潜伏风险**：`NativeBridge.cs:188` 有个 `runAutoConfig` 命令，前端目前不调用（死命令）。
谁哪天把它接到"启动服务"上，exe 路径也会开始每次启动抹凭据。

### 2.2 【P0】本地模型 provider 被删除（端点暂时不可达时）

同一次运行，`settings.yaml` 里用户手工配好的 provider 消失：

```diff
 llm-pi-ai:
   providers:
-    auto-extra1:
-      displayName: "llama-server"
-      apiKeyEnv: AUTO_EXTRA1_API_KEY
-      api: openai-completions
-      baseURL: "http://127.0.0.1:11435/v1"
-      models:
-        - id: "D:\\Ollama\\Models\\gguf\\Dolphin3.0-Llama3.1-8B-abliterated.Q4_K_M.gguf"
+    deepseek:
+      apiKeyEnv: DEEPSEEK_API_KEY
```

**影响**：`start-model-server.ps1` 最长只等 120s（`:103`），且失败时 `start.ps1:104-107`
只打一条黄色警告就继续 → 紧接着跑的 auto-config 探测不到 11435 → **把 provider 从配置里删掉**。
模型加载慢的机器上（8B Q4 在机械盘上很常见），每次冷启动都会"启动成功但没有本地模型"。

**修法**：`auto-*` provider 的清理应该只在"本次探测成功且确实缺它"时进行；
探测失败（超时/连接拒绝）与"端点已不存在"必须区分处理 ——
前者应保留，后者才清理。更好的做法是把自动生成的 provider 写进独立文件（如
`settings.auto.yaml`）并让 DSH 叠加，而不是就地改写用户文件。

### 2.3 已排除的伪问题（查证后不成立）

**`DshService.Start()` 没有传 `--host`**（`Services.cs:341`）：

```csharp
psi.Arguments = "\"" + Paths.DshJsBin + "\" web --port " + Port + " --no-open";
```

我起初怀疑这是局域网暴露（配置里 `DSH_PERMISSION=danger-full-access`，暴露即为 RCE）。
查 dsh 实现后**不成立**：

- `dsh-web-app/cordis.patch.yml:139` → `host: !!js ctx.webStartup.host ?? '127.0.0.1'` —— 默认就是回环
- `dsh-web-app/lib/startup.js:40` 显式拒绝 `--host 0.0.0.0`：
  *"intentionally not supported yet for safety: it would expose remote code execution to the network"*

所以省略 `--host` 是安全的。不过显式写上 `--host 127.0.0.1` 仍是零成本的意图表达，建议补上。

---

## 3. 代码级缺陷清单

标注：**[复现]** 已实际验证 / **[实证]** 有日志或文件证据 / **[通读]** 源码通读确认 / **[推断]** 代码路径分析

### P0 级

| # | 位置 | 问题 | 证据 |
|---|---|---|---|
| 1 | `auto-config.mjs:326-346` | `records` 块被清空 | **[复现]** |
| 2 | `auto-config.mjs:220-244` | 探测失败即删除 provider | **[复现]** |
| 3 | `logs/app.log` | **dsh 启动即崩溃**：`ERR_MODULE_NOT_FOUND`，缺 6 个包 | **[实证]** |
| 4 | `Services.cs:959` | 插件安装用 `npm install -g`，装进 `tools/global`，而 DSH 的 peer 依赖在 profile 里 → 装完 dsh 起不来 | **[实证]** |
| 5 | `scan-missing.mjs:7` | 硬编码 `D:/deepseek-harness/tools/global`，调用方从不传参 | **[通读]** |
| 6 | `scan-missing.mjs:74` | 输出字面 `\n`，PowerShell 的 `-like 'INSTALL_LIST=*'` 永不匹配 | **[通读]** |
| 7 | `bootstrap.ps1:124` | 整串当单参数喂 npm（未 `-split`） | **[通读]** |
| 8 | `Services.cs:447` 等 7 处 | 双流 `ReadToEnd` 死锁面（见 §3.3） | **[通读]** |
| 9 | `NativeBridge.cs:142-143` | `startGateway` 在 UI 线程同步执行，最长阻塞 10 分钟 | **[通读]** |
| 10 | `Services.cs:631-651` | `ollama run` 是交互式 REPL，`ReadToEnd()` 永不返回 → 该命令永不响应 | **[通读]** |
| 11 | `user.env.example:41/43/55` | 硬编码 `D:\` 路径，而 bootstrap 把它原样拷成实际配置 | **[通读]** |

#### 3.1 关于第 3、4 条 —— 这是当前**实际发生的故障**

`logs/app.log:206` 起：

```
Error: failed to import loader entry attachment-local (@deepseek-ai/dsh-attachment-local):
  Cannot find package '@deepseek-ai/dsh-attachment'
  imported from G:\deepseek-harness\tools\global\node_modules\@deepseek-ai\dsh-attachment-local\lib\index.js
```

缺失的包（去重后 6 个）：`dsh-attachment`、`dsh-jobs`、`dsh-session-persistence`、
`dsh-session-query`、`dsh-settings`、`dsh-util-time`，全部由 `tools/global` 下的同族包引用。
日志末尾 `Node.js v24.19.0` + `dsh 进程已退出。`，即**启动即崩**。

根因链：
1. `PluginService.Install`（`Services.cs:951-979`）执行 `npm install -g <pkg>`
   （`--legacy-peer-deps`，装进 `tools/global`）
2. 而 README 第 85-89 行承诺的是 `dsh plugin --profile web add <pkg>`（装进 `home/profiles/web`）
3. `tools/global` 里那份 DSH 是"扁平安装"，缺 peer 依赖
4. 装进来的 `dsh-mcp-client` 在启动时被加载 → import 失败 → **整个 dsh 起不来**

**注意 `fix_update.js`**：这个 18 行的脚本专门去 `G:\deepseek-harness\update.ps1` 里
删掉 `--legacy-peer-deps` —— 说明作者已经踩过这个坑并修了 `update.ps1`，
但 **`Services.cs:959` 的安装路径漏了**。同一份仓库里两种 npm 调用策略并存。

**修法**：安装/卸载插件改走 `dsh plugin --profile web add/remove`，与 README 承诺一致；
并在安装前后校验 `dsh --version` 仍可正常启动，失败则回滚。

#### 3.2 关于第 9 条 —— UI 冻结

`NativeBridge.HandleMessage` 由 `CoreWebView2.WebMessageReceived` 触发，运行在 UI 线程。
其中：

```csharp
case "startGateway": MainForm.Dsh.Start(); Respond(...); break;   // NativeBridge.cs:142
```

而 `DshService.Start()` 会同步调用：

- `RunBootstrap()` → `p.WaitForExit(600000)`（`Services.cs:416`）= **最长 10 分钟**
- `RunModelStarter()` → `p.StandardOutput.ReadToEnd()` + `WaitForExit(90000)`（`:447-448`）
  —— 因为是 `ReadToEnd`，实际**无上限**

首次运行（缺 node/dsh）点一下"启动服务"，窗口就是这个状态 10 分钟，WebView2 无法重绘。

对照：作者对 `startModel`/`stopModel`/`listPlugins`/`installPlugin` 都用了 `Task.Run`
（`NativeBridge.cs:265/301/426/436`），唯独 `startGateway`/`stopGateway` 没有 —— 不一致。

同类的还有：`ScanGguf`（`:317`）在 UI 线程递归遍历模型目录；
`BuildState()` 每 2 秒调用 `LlamaExeDir()` → `new ConfigManager(UserEnv)` → `File.ReadAllLines`
（每 2 秒重读并重解析配置文件）+ `DetectOllamaPath()` 扫 PATH。

#### 3.3 双流 `ReadToEnd` 死锁面（7 处）

`ProcessStartInfo` 里设了 `RedirectStandardError = true` 却不读取 stderr，
或对两个流顺序 `ReadToEnd()`，子进程写满 4KB stderr 管道即永久阻塞：

| 行号 | 方法 | 情况 |
|---|---|---|
| `Services.cs:178` | `Util.RunExe` | 双流顺序读 —— **该函数从未被调用（死代码）** |
| `Services.cs:447` | `RunModelStarter` | stderr 重定向但从不读，`ReadToEnd` 无超时 |
| `Services.cs:514-515` | `RunAutoConfig` | 双流顺序 `ReadToEnd` —— **必死锁** |
| `Services.cs:644` | `LoadOllamaModel` | stderr 不读 + `ollama run` 交互式 |
| `Services.cs:970-971` | `PluginService.Install` | 双流顺序读，npm 的 stderr 极吵 —— **必死锁** |
| `Services.cs:1013` | `PluginService.Uninstall` | stderr 不读 |

**修法**：统一用 `OutputDataReceived` / `ErrorDataReceived` + `BeginOutputReadLine` +
`BeginErrorReadLine`（仓库里 `RunBootstrap:412-415` 已经是对的写法，照抄即可），
或者 `Task.Run(() => p.StandardError.ReadToEnd())` 并行读取。

### P1 级

| # | 位置 | 问题 |
|---|---|---|
| 12 | 全局 | **没有 Job Object**：`DeepSeekHarness.exe` 被任务管理器杀掉后，`node`、`llama-server`、`ollama` 全部成为孤儿，占着 3080/11435/11434 和显存。`grep -i jobobject` 全仓零命中 |
| 13 | `Services.cs:186/166` | `KillTree`（名为 KillTree 实际只杀单进程）与 `RunExe` 是**从未被调用的死代码** |
| 14 | `Services.cs:196-207` | `KillTreeForce` 起 `taskkill.exe` 后**不等待**，`Stop()` 立即返回 → 紧接着 `Start()` 会与尚未退出的旧进程抢端口 |
| 15 | `Services.cs:611-629, 795-808` | 停不了自己启的进程时，**杀掉系统上所有**名为 `ollama` / `llama-server` 的进程（会误杀用户手动启的实例），且不杀子进程（ollama 的 runner 会残留） |
| 16 | `Services.cs:83-107` | `ConfigManager.Save()` **重写整个 user.env**：注释全丢。实测盘上的 `config/user.env` 只剩 `Save()` 写入的两行表头，而 `user.env.example` 有 20+ 行说明 —— 这就是证据 |
| 17 | 7 处 `new ConfigManager(...)` | 每个实例持有独立快照，`Save()` 会**互相覆盖**（丢更新）。同时 `MainForm.Cfg` 又是单例，两套用法并存 |
| 18 | `NativeBridge.cs:47-60` | 桥接层**不校验 `e.Source`**。dsh 的 Web UI 在跨域 iframe 里运行（`index.html:178` + `app.js:421`），iframe 内的脚本同样能访问 `window.chrome.webview` |
| 19 | `NativeBridge.cs:466-470` | `openExternal` 对页面传来的任意字符串调用 `Process.Start`，未限制 scheme（`file:`/UNC/可执行文件路径） |
| 20 | `Services.cs:959/1002` | 插件名来自输入框，**未校验**直接拼进 npm 参数（`--registry=…` 之类的参数注入） |
| 21 | `NativeBridge.cs:474-493` | `saveUserBg` 写入任意 base64 字节且不校验是否真是图片；配合 `app.local` 同源映射形成存储型 XSS 面 |
| 22 | `NativeBridge.cs:385-419` | `setConfig` 接受任意 key/value，而 `ConfigManager.Save()` 不转义 → value 含 `\n` 可注入任意配置行 |
| 23 | 前端 `bridge.js:28` | 全局 60s 超时，但 `installPlugin` 宿主预算是 180s、`pickFolder` 取决于用户何时点确定 → **超时后宿主仍在干活，迟到响应被丢弃**（`bridge.js:43-44`），UI 显示"失败"而实际成功 |
| 24 | 前端 `app.js:667-713` | 设置页 `getConfig` 失败被吞，`#s-save` 仍可点 → 用 HTML 默认值**覆盖真实 API Key 与参数** |
| 25 | 前端 `bridge.js:37` | 顶层无判空访问 `window.chrome.webview` → 非 WebView2 环境下整个前端静默死掉（`app.js:7` 拿到 undefined，IIFE 中止） |

### P2 级（摘要）

- 无障碍：导航项是无 `href` 的 `<a>`、开关是 `<span>` 无 `role="switch"`，键盘完全不可用；`#toasts` 无 `aria-live`
- i18n：中文硬编码散落在 `index.html` 与 `app.js`，C# 也直接发中文串（`NativeBridge.cs:398/441`）
- `bridge.js` 无 cache-busting（`app.js`/`style.css` 有）→ 升级后可能"新 app.js + 旧 bridge.js"协议错配
- `Bootstrap` 下载 Node zip **不校验哈希**（`bootstrap.ps1:78-84`）
- `start.ps1:44-48` 允许 user.env 注入任意环境变量（含 `NODE_OPTIONS`）→ 改 U 盘上一个文件即可执行代码
- `update.ps1:57-58` 不带 `--location` 改宿主机全局 npm 配置（`ignore-scripts false`）
- `Program.cs` / `Services.cs` 单文件 1138 / 1022 行
- **全仓零测试**。§2 的两个 bug 都是"配置合并"逻辑，任何一个单测都能拦住

---

## 4. 架构级建议（杠杆最大的几条）

### 4.1 收敛成单一启动编排

现在 A/B 两条路径行为不同。建议：**exe 只做 UI + 进程管理，编排逻辑只留一份**。
两个方向选一个：

- **推荐**：把 `start.ps1` 的编排（模型拉起、auto-config、环境注入）搬进 C#，
  删掉 ps1 链。好处是能共享日志、能显示进度、能用 Job Object 托管。C# 已有全部能力。
- 或者：exe 只调 `start.ps1`，删掉 `DshService.Start()` 里的重复逻辑。

无论哪条，都要保证"用户从哪个入口启动，行为一致"。

### 4.2 进程生命周期交给 Windows Job Object

```csharp
var job = CreateJobObject(IntPtr.Zero, null);
var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
SetInformationJobObject(job, JobObjectExtendedLimitInformation, ref info, size);
AssignProcessToJobObject(job, proc.Handle);   // node / llama-server / ollama 都挂上
```

一行代码解决"孤儿进程占显存"和"taskkill 不等待"两个问题 —— 句柄关闭时整棵树自动清理。

### 4.3 配置只有一个写入者

- C# 侧：`ConfigManager` 改成单例 + `FileSystemWatcher` 感知外部修改，不再到处 `new`
- `Save()` 改为**保留注释的就地更新**（只替换发生变化的键行），或把用户配置与生成配置
  分成 `user.env`（人手写，程序不重写）与 `generated.env`（程序管，随覆盖）
- 所有写入走 temp + `rename` + 一个跨进程锁；与 DSH 自己的 `withFileLock`/`writeFileAtomic` 保持同一约定

### 4.4 UI 线程绝不做阻塞与 IO

规则：所有 `HandleMessage` 分支一律 `Task.Run` + `BeginInvoke` 回 UI 线程 `Respond`
（`StartModelAsync:288` 已经是正确范式）。`BuildState` 里的文件/PATH 探测改为
启动时算一次 + 事件驱动更新，不要每 2 秒重读。

### 4.5 桥接层加三处校验

```csharp
// 1. 只接受来自自己页面的消息
if (!e.Source.StartsWith("https://app.local/", StringComparison.OrdinalIgnoreCase)) return;
// 2. openExternal 只放行 http/https
if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || (u.Scheme != "http" && u.Scheme != "https")) return;
// 3. 插件名白名单
if (!Regex.IsMatch(pkg, @"^(@[a-z0-9-_.]+/)?[a-z0-9-_.]+(@[^\s]+)?$")) { Respond(id,false,"非法包名"); return; }
```

### 4.6 中期：迁移到 .NET 8

当前是 .NET Framework 4.8 + `csc.exe` 手写编译脚本。迁到 .NET 8 可直接获得：
单文件发布、`System.Text.Json`（替掉 `JavaScriptSerializer`）、真正的 `async`/`HttpClient`、
以及 `dotnet test` 跑单元测试的能力。WebView2 在 .NET 8 上完全支持。
工作量中等，但对"可持续维护"是决定性的。可作为第二阶段。

---

## 5. 修复路线图

### 第一批（本周，都是小改动、高收益）

1. **`auto-config.mjs` 的 credentials 合并改用 YAML parse/stringify，保住 `records`**
   —— 正在持续丢数据，最紧急
2. **`auto-config.mjs` 不要在探测失败时删除 provider**（区分"端点不存在"与"暂时不可达"）
3. **修 `logs/app.log` 里的崩溃**：`tools/global` 补回 6 个缺失包（`npm install` 到正确位置），
   或干脆重装一份干净的 dsh；并把插件安装改走 `dsh plugin --profile web add`
4. `scan-missing.mjs` 去 `D:/` 默认值 + 调用方补传参 + 目录不存在时报错退出 + 去掉字面 `\n`
5. `bootstrap.ps1:124` 改 `-split '\s+'`
6. `DshService.Start()` 的 `startGateway`/`stopGateway` 改 `Task.Run`（消除 10 分钟 UI 冻结）
7. `LoadOllamaModel` 改用 `/api/generate` 带 `keep_alive` 预热，替掉 `ollama run`
8. 7 处双流 `ReadToEnd` 改异步读取
9. `user.env.example` 去掉 `D:\` 硬编码
10. 把 `launcher/Program.cs` 移进 `legacy/`

### 第二批（下一阶段）

11. Job Object 托管子进程
12. `ConfigManager` 单例化 + 原子写 + 保留注释
13. 桥接层三处校验（来源 / scheme / 包名）
14. 统一两条启动路径的编排
15. 前端 `req()` 包装 + 按命令设置超时 + `pickFolder` 不超时
16. `logs/app.log` 的日志管线（现在每行都 `AppendAllText` + `FileInfo` + 可能的 1MB `Copy`）

### 第三批（结构性）

17. 迁移 .NET 8
18. 补单元测试（目标：配置合并、端口探测、YAML 手术各一组）
19. 无障碍与 i18n
20. 拆 `Program.cs`(死) / `Services.cs`(1022 行)

---

## 6. 需要你确认的三件事

1. **立即轮换 DeepSeek API Key**。它明文存在两个地方（`config/user.env:10` 与
   `home/.credentials.yaml`），而 `home/` 随 U 盘移动。审计过程中这把 key 已被读取，
   建议去 platform.deepseek.com 作废重建。Windows 上 DSH 自己不做权限校验
   （`dsh-credentials-local` 里有 `if (process.platform === "win32") return;`），所以靠不住。
2. **确认你平时用哪个入口启动**：双击 `DeepSeekHarness.exe` 还是 `start.bat`？
   这决定了 §2 的凭据丢失是否每天都在发生（走 exe 不会触发，走 bat 每启动一次丢一次）。
3. **确认那些 `.bak` / `.old` / `fix_update.js` / `monitor.ps1` 是否还需要**：
   它们记录了调试历史，但也让"哪个文件是权威实现"变得不清晰。

---

## 附：本项目做得好的地方

为避免这份报告显得只有批评，以下是我核对后认为**明显正确**的设计：

1. 路径解析全部用 `$PSScriptRoot` / `%~dp0`，安装目录没有一处写死
2. `start.ps1:11-19` 主动补全 `SystemRoot`/`COMSPEC`/`PATHEXT`/`TEMP` —— 精简 Windows 上这是救命的
3. `auto-config.mjs:64-76` 的探测用了 AbortController + `finally clearTimeout`，不留悬挂 socket
4. 同时兼容 OpenAI `data[]` 与 Ollama `models[]`，并过滤 embedding 模型、按共享端口去重
5. `yamlStr()` 同时转义反斜杠与引号 —— 很多同类脚本只转义引号，Windows 路径上必炸
6. `auto-config.mjs:240-252` "看不懂就备份再重建"的姿态是对的（问题只是备份没覆盖 records 那条路径）
7. `update.ps1:41-53` 的 `Invoke-Npm` 用重定向到临时文件再取 `ExitCode`，
   绕开了 PS 5.1 "原生命令写 stderr + `$ErrorActionPreference='Stop'`" 的经典坑 —— 全仓最正确的 PS 写法
8. `start-model-server.ps1:8` 定义了退出码契约，`start.ps1:104-107` 遵守它并降级继续 ——
   "模型起不来不该阻止 dsh 启动"这个判断是对的
9. 不把密钥写进日志（`auto-config.mjs:298/393`）
10. `启动.bat` 写了 `chcp 65001` —— 作者知道编码问题，只是没推广到其他脚本

---

*本报告基于静态审计与两处最小复现实验；未修改任何项目文件。*
