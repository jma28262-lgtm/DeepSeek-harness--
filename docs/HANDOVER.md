# DeepSeekHarness 启动器 —— 交接说明（给电脑端 AI）

> **读这份文档的 AI**：这是一个 Windows 桌面启动器项目的完整交接包。
> 目标是让你能**独立完成构建、配置与验证**，不需要问人。
> 按 §3 → §4 → §5 顺序执行。文中每个结论都标了证据等级，**请勿混淆**：
>
> - `[实测]` 真跑过、有输出
> - `[结构校验]` 检查过二进制/文件结构，但没运行
> - `[静态复核]` 只读过代码，没执行
> - `[未验证]` 完全没验证

---

## 1. 这是什么

[DeepSeek Harness](https://github.com/deepseek-ai/deepseek-harness)（`dsh`，DeepSeek 官方的
插件化 Agent Harness）的 **Windows 便携部署**，外加一个自研的 **C# WinForms + WebView2 启动器**。

启动器负责四件事：

1. **环境部署**：便携 Node 与 dsh 内核的检查/安装
2. **模型服务生命周期**：拉起/停止 llama.cpp 或 Ollama，进程树由 Job Object 托管
3. **凭据加密**：API Key 不明文落盘，只以环境变量注入 dsh 子进程
4. **换机自适应**：机器指纹变化时自动修复路径、重探模型端点

代码规模：C# 约 3000 行（8 个源文件）+ 前端约 2000 行（原生 HTML/CSS/JS，无框架）
+ PowerShell/Node 脚本约 1800 行。

---

## ⚠️ 上机后先做这两件事（必做）

> 这两条不做，后面大概率撞墙。按顺序执行，各自都有验收标准。

### A. 修 `tools\global` 的依赖损坏 —— 否则 dsh 启动即崩

**现象**（来自 `logs\app.log`，实测）：

```
Error: failed to import loader entry attachment-local:
  Cannot find package '@deepseek-ai/dsh-attachment'
  imported from ...\tools\global\node_modules\@deepseek-ai\dsh-attachment-local\lib\index.js
...
Node.js v24.19.0
dsh 进程已退出。
```

缺失 6 个包：`dsh-attachment`、`dsh-jobs`、`dsh-settings`、`dsh-session-persistence`、
`dsh-session-query`、`dsh-util-time`。

**根因链**（三段串起来才解释得通）：

1. `bootstrap.ps1` 装 dsh 时带了 `--legacy-peer-deps`（见该文件第 105 行）——
   这会**跳过 peer 依赖解析**，于是 peer 没被装上；
2. 项目本来设计了 `scripts\scan-missing.mjs` 在安装后扫描并补装缺失的 peer，
   但**它此前是坏的**（默认目录写死成 `D:/deepseek-harness/...` 而调用方从不传参、
   输出里是字面 `\n` 导致 PowerShell 的 `-like` 永不匹配、包名没拆成数组），
   所以这段补装逻辑从未真正执行过；
3. 结果是 `tools\global` 这棵树不完整，dsh 启动加载插件时找不到 peer → 崩溃。

**好消息**：上面第 2 条的三个 bug **本仓库已全部修好**（`scan-missing.mjs` + 
`bootstrap.ps1`/`update.ps1` 的调用处）。所以现在重装一次就能装全。

**修复步骤**：

```powershell
# 1) 备份现有的（可选，出问题可回退）
Move-Item <部署目录>\tools\global <部署目录>\tools\global.broken

# 2) 重跑 bootstrap —— 它会重新装一份完整的 dsh 到 tools\global
cd <部署目录>
powershell -NoProfile -ExecutionPolicy Bypass -File .\bootstrap.ps1
```

> ⚠️ **必须先删/改名 `tools\global`**：`bootstrap.ps1` 里 dsh 那段是
> `if (-not (Test-Path $dsh))` 守卫的 —— 只要 `dsh.cmd` 还在，它就会**跳过整个安装**，
> 你跑了也等于没跑。（Node 那段同样有守卫，所以不会重复下载 Node。）

**验收**：

- [ ] `logs\app.log` 里不再出现 `ERR_MODULE_NOT_FOUND`
- [ ] 启动服务后 `http://127.0.0.1:<端口>` 能出界面

**顺带自查**（本仓库已修好的工具可以直接用）：

```powershell
node scripts\scan-missing.mjs <部署目录>\tools\global
# 正常输出形如：OK: 已检查 N 个包，无缺失 peer 依赖
# 若列出 INSTALL_LIST=...，说明仍有缺口，把后面那串包名交给 npm install 即可
```

### B. 设置新的 API Key（旧的已从部署中移除）

**为什么要换**：旧密钥曾以**明文**同时存在于 `config\user.env` 与 `home\.credentials.yaml`，
而整个部署目录随移动硬盘流动；本次审计过程中也被读取过。
**现已把这两处的明文移除**，所以你需要重新设置一次。

**步骤**：

1. 到 https://platform.deepseek.com/ **作废旧密钥、生成新的** ——
   旧的那把已经暴露过，**必须作废**，不要只是换个存放位置；
2. 打开 `DeepSeekHarness.exe` → **环境配置** 页；
3. 先设访问口令（≥6 位）并点「设为口令模式（换机可用）」，
   这样凭据可随盘换机、每台新机器只需输一次口令；
4. 在「DeepSeek API Key」填入新密钥 → **保存**。
   密钥会用口令派生的密钥加密存放在 `config\secrets.dat`，磁盘上不出现明文；
5. **不要**再把密钥写回 `config\user.env`。若你写了，启动器首次运行会自动把它
   迁进加密库并从原文件抹掉。

**验收**：

- [ ] 环境配置页显示「已保存 sk-****」（掩码，不回显明文）
- [ ] `config\user.env` 里**没有** `DEEPSEEK_API_KEY=` 的非空值
- [ ] `home\.credentials.yaml` 的 `refs:` 里**没有**明文密钥

> 没有 API Key 只影响**云端模型**；本地模型（llama.cpp / Ollama）不受影响。
> 若你选了「本机模式（免口令）」，密钥将只能在本机本账户解开，**换电脑必须重填** ——
> 要跨机可用请用口令模式。

---

## 2. 目录结构

本文件同时存在于**源码仓库**与**交付包**两处，内容相同，但两边的目录布局不同。

### 2.1 源码仓库（canonical source —— 改代码以这里为准）

```
<repo>/
├── README.md                仓库落地页
├── LICENSE                  MIT
├── docs/                    文档（含本文件、截图）
│   ├── HANDOVER.md          ← 本文件（先读这个）
│   ├── VERIFICATION.md      验证证据与「未验证」清单
│   ├── OPTIMIZATION-REVIEW.md
│   ├── REPO-SETUP.md
│   └── screenshots/
├── launcher/
│   ├── src/*.cs             C# 源码 —— build.ps1 **只编译这个目录**
│   ├── web/                 WebView2 界面（index.html + js + css）
│   ├── legacy/              旧版单体实现，**不参与编译**（勿在此改 bug）
│   ├── build.ps1            构建脚本（.NET Framework 4.8）
│   ├── app.manifest         DPI/权限清单（**必须嵌进 exe**，见 §3.4）
│   └── monitor.ps1          调试辅助
├── scripts/                 auto-config.mjs / scan-missing.mjs / start-model-server.ps1
├── config/                  user.env.example（模板）/ theme.json
├── agent/                   mcp-re-tools.cjs
└── bootstrap.ps1 / start.ps1 / update.ps1（+ 对应 .bat）
```

### 2.2 交付包（外接盘上的 `DeepSeekHarness-Launcher-Project\`）

把上面源码仓库的**内容整体放进 `src\`**，另外附带已构建好的产物：

```
DeepSeekHarness-Launcher-Project\
├── README.md  LICENSE
├── docs\                    （与仓库的 docs\ 相同）
├── src\                     ← 仓库根目录的内容整体搬到这里
│   ├── launcher\  scripts\  config\  agent\  *.ps1  *.bat
│   └── launcher\wv2pkg\pkg        WebView2 程序集（构建依赖，勿删）
└── dist\                    ★ 已构建好的产物（可直接运行）
    ├── DeepSeekHarness.exe
    ├── DeepSeekHarness.exe.config
    └── *.dll                WebView2 运行时（Core / WinForms / Loader）
```

### 2.3 运行系统（在交付包之外，与外接盘同一层）

```
<外接盘>\deepseek-harness\      ← 实际运行的部署（exe + tools + home + config + launcher\web）
```

⚠️ **`launcher\web\` 是运行时必需品**（启动器用它做虚拟主机映射），部署目录里不能删。

---

## 3. 构建

### 3.1 前置条件

| 项 | 要求 |
|---|---|
| 操作系统 | Windows 10 1809+ 或 Windows 11，x64 |
| .NET Framework | 4.8（Win10 1809+ / Win11 系统自带） |
| PowerShell | 5.1（系统自带） |
| WebView2 Runtime | Win11 自带；Win10 若缺，装 [Evergreen Runtime](https://developer.microsoft.com/microsoft-edge/webview2/) |
| 管理员权限 | **不需要**（manifest 声明 `asInvoker`） |
| Visual Studio / .NET SDK | **不需要** |

### 3.2 构建命令

```powershell
cd src\launcher
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
```

`build.ps1` 的实际行为（`[静态复核]`）：用
`C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe`
把 `src\launcher\src\**\*.cs` 编成 `..\DeepSeekHarness.exe`，
参数为 `/target:winexe /platform:x64 /optimize+ /win32manifest:app.manifest`，
引用 `System / System.Core / System.Drawing / System.Windows.Forms /
System.Web.Extensions / System.Security` + `wv2pkg` 里的两个 WebView2 程序集。

产物位置：**`src\DeepSeekHarness.exe`**（因为 `build.ps1` 里 `$Root = 上一级`）。

### 3.3 ⚠️ 改代码前必读：这个编译器只支持 C# 5

`v4.0.30319\csc.exe` 是老的原生编译器，**不支持**下列语法，用了就编译失败：

| 禁止 | 替代写法 |
|---|---|
| 字符串插值 `$"x={y}"` | `"x=" + y` 或 `string.Format` |
| null 条件运算符 `a?.b` | `var t = a; if (t != null) t.b;` |
| `??=`、表达式体成员 `=>`、`out var`、元组、局部函数、`using static`、记录类型 | 用 C# 5 等价写法 |

现有 8 个源文件全部遵守这一约束，新代码请沿用同样的风格。

### 3.4 构建验收（三条都要过，缺一不可）

```powershell
# ① 产物存在且大小合理：约 85–95 KB
#    （若只有 60 多 KB，说明编的是旧代码）
Get-Item ..\DeepSeekHarness.exe | Select-Object Length

# ② 必须含 DPI 清单，否则高 DPI 屏界面发虚
#    背景：MainForm.cs 注释明确写「DPI 感知由 app.manifest 声明，此处不再调用 API」
[System.Text.Encoding]::ASCII.GetString(
  [IO.File]::ReadAllBytes('..\DeepSeekHarness.exe')) -match 'PerMonitorV2'

# ③ 编译过程零 error（warning 应为 0；若有 warning 请贴出来）
```

> **已知陷阱**：mono 的 `mcs` 编译器接受 `-win32manifest:` 参数、编译 `exit=0`，
> 但**产物里根本没有清单**（静默失败）。用 `mcs` 交叉验证时请务必单独检查 `PerMonitorV2`。
> 这也是为什么本次交付用 Roslyn csc（见 [VERIFICATION.md](VERIFICATION.md)）。

### 3.5 构建后部署

```powershell
Copy-Item ..\DeepSeekHarness.exe <外接盘>\deepseek-harness\ -Force
```

`DeepSeekHarness.exe.config` 一般不用动（它按 exe 基名加载，声明 `.NETFramework,Version=v4.8`，
并配有 WinForms 的 DPI 设置）。

---

## 4. 配置

### 4.1 配置文件

`<外接盘>\deepseek-harness\config\user.env`（模板见 `src\config\user.env.example`）。

**模板里刻意不写死盘符**：留空 = 自动探测。启动器的「环境配置」页在换机后也会
把失效的绝对路径重新定位并写回。

主要键：

| 键 | 说明 |
|---|---|
| `DEEPSEEK_API_KEY` | 建议**不填这里**，改在启动器「环境配置」页填（那样会加密存放） |
| `DSH_PORT` | Web UI 端口，默认 3080 |
| `DSH_AUTO_START_MODEL` | 1 = 启动 dsh 前自动拉起本地推理服务 |
| `DSH_MODEL_BACKEND` | `llama-cpp` 或 `ollama` |
| `DSH_LLAMA_DIR` | `llama-server.exe` 所在目录，留空自动探测 |
| `DSH_LLAMA_MODEL` | GGUF 模型路径 |
| `DSH_LLAMA_PORT` | 默认 11435 |
| `DSH_LLAMA_CONTEXT` / `DSH_LLAMA_GPU_LAYERS` | 上下文长度 / GPU 卸载层数（99 = 全部） |
| `DSH_OLLAMA_EXE` / `DSH_OLLAMA_MODEL` | Ollama 路径与预载模型 |
| `DSH_EXTRA_ENDPOINTS` | `名称\|地址\|/v1`，多个用 `;` 分隔 |
| `DSH_PERMISSION` | dsh 默认权限模式，**仅在该项为空且 dsh 尚无权限配置时写入一次** |

> **易踩的坑**：llama.cpp 默认端口 **11435 不在内置探测表里**（内置只探 11434/1234/8080/
> 8000/1337/5001/5000）。要让本地模型被自动注册，必须在 `DSH_EXTRA_ENDPOINTS` 里显式声明
> `llama-server|http://127.0.0.1:11435|/v1`。这一条是审查中发现的「全链路互不报错地空转」问题。

### 4.2 密钥如何存放（重要）

两种模式，**磁盘上都不出现明文**：

| 模式 | 换机可用 | 输入成本 | 实现 |
|---|---|---|---|
| **口令模式**（默认） | ✅ 凭据文件可随盘搬走 | 每台新机器输一次口令，之后该机免密 | PBKDF2-HMAC-SHA256（20 万次迭代）+ AES-256-CBC + HMAC-SHA256（encrypt-then-MAC） |
| 本机模式 | ❌ 只在本机本账户可解 | 零输入 | DPAPI（CurrentUser） |

密钥**只以环境变量注入 dsh 子进程，不写入任何配置文件**。依据是 dsh 自身的
凭据解析顺序 `env → .credentials.yaml → .env`（`dsh-credentials-local` 的 `resolve()`，
`[静态复核]`）：环境变量优先，因此无需落盘。

**为什么跨机必然要输一次口令**：若要让"换机零输入"成立，解密密钥就必须与密文同盘存放，
那与明文无异，只是看起来像加密。这是数学约束，不是实现取舍。

凭据文件：`config\secrets.dat`（加密）、`config\secrets.unlock`（本机 DPAPI 解锁缓存）。
两者都**不要提交到任何仓库**。

### 4.3 首次运行会发生什么

1. **凭据迁移**（幂等）：把历史遗留在 `config\user.env` 与 `home\.credentials.yaml` 里的
   明文密钥收进加密库，并**从原文件抹掉**（改前留 `.bak`）。需要凭据库已解锁。
2. **机器指纹记录**：写入 `config\machine.id`（`MachineGuid` + 机器名 + 用户名的 SHA-256 前 8 字节）。
3. **换机自检**（后台线程）：指纹变化则触发路径自愈 + 模型端点重探。
4. 启动服务时：显式以 `--host 127.0.0.1` 绑定回环，注入密钥环境变量。

---

## 5. 端到端验证清单

按顺序勾，任一项不过就停在那里查：

> **先确认「上机后先做这两件事」已过**（tools\global 依赖完整 + 新 API Key 已设），否则下面第一条就会卡住。

- [ ] `build.ps1` 编译零 error → §3.4 三条验收全过
- [ ] 双击 `deepseek-harness\DeepSeekHarness.exe` 能起来（**起不来 → 见 §7 回滚**）
- [ ] 界面顶部无「演示模式」提示条（有 = 没跑在 WebView2 里，说明 exe 没正确加载）
- [ ] 侧边栏出现 **环境配置** 页
- [ ] 「环境配置」页 → 点 **一键配置环境** → 报告里每步状态合理（无 failed）
- [ ] 若提示需要口令 → 输入一次 → 状态变「已解锁」
- [ ] 「当前状态」里 **子进程托管 (Job Object)** 显示「已启用」
- [ ] 仪表盘 → **启动服务** → 状态变「运行中」，且**界面全程不卡**（原实现会冻结最长 10 分钟）
- [ ] **打开 Web UI** → dsh 的界面能正常加载
- [ ] 关掉启动器 → 任务管理器里 **没有残留的 `node.exe` / `llama-server.exe` / `ollama.exe`**
- [ ] `config\user.env` 里**没有** `DEEPSEEK_API_KEY=` 的明文值
- [ ] `home\.credentials.yaml` 的 `refs:` 里**没有**明文密钥
- [ ] 断网重试：一键配置会报「自动部署失败，通常是网络问题」，而不是假装成功

---

## 6. 已知问题与待办

### 6.1 本次交付中「未验证」的部分（请优先验证这些）

| 项 | 状态 |
|---|---|
| **exe 的运行时行为** | `[未验证]` 在 Android 上无法运行 Windows 程序。PE 头/入口点/引用/公钥令牌/清单都校验过，但**渲染、WebView2 集成、DPAPI、Job Object 归属全部未运行验证** |
| `bootstrap.ps1` / `update.ps1` 的改动 | `[静态复核]` 只做了括号配平与逐行复核，**没有执行过**（本机无 PowerShell） |
| `auto-config.mjs` 的 `renameSync` 覆盖语义 | `[静态复核]` 在 Windows 上是 MoveFileEx 语义（文档知识），未实测 |
| 150% DPI 下 `#side-pop` 浮层定位 | `[未验证]` 代码里混用了 `getBoundingClientRect()` 与 `window.innerHeight`，高 DPI 下可能偏移（详见 `OPTIMIZATION-REVIEW.md`） |

### 6.2 架构债（不影响使用，但值得后续处理）

1. **两条启动路径的编排逻辑不统一**：exe 直接起 node；`start.ps1` 另有一套。
   详见 `OPTIMIZATION-REVIEW.md` §4.1。
2. **`ConfigManager.Save()` 会重写整个 `user.env`（注释全丢）**，且不是单例 ——
   多处 `new ConfigManager(...)` 持有独立快照，会用陈旧数据覆盖别人的修改。
3. **前端旧调用点仍无 `.catch`**（新代码已用 `req()` 包装；旧调用点待迁移）。
4. 迁到 .NET 8 可直接获得单元测试能力（本次的加密测试已经证明测试的价值）。

### 6.3 安全须知

1. **不要把 `config\user.env`、`home\`、`config\secrets.*` 提交到任何仓库**。
   仓库的 `.gitignore` 已覆盖。
2. Windows 上 DSH 自身不做凭据文件权限校验
   （`dsh-credentials-local` 里有 `if (process.platform === "win32") return;`），
   **不要依赖文件权限保护密钥** —— 这正是启动器要自己加密的原因。
3. 若历史上明文密钥曾经外泄或随盘流动过，请到
   [platform.deepseek.com](https://platform.deepseek.com/) 轮换。

---

## 7. 回滚

本次交付替换了启动器可执行文件。旧版本备份在：

```
蓝鱼文件夹（手机侧）: /sdcard/Download/蓝鱼/DeepSeekHarness-旧版程序备份/
  DeepSeekHarness.exe.20260917-1059.original   ← 替换前正在使用的那份
  DeepSeekHarness.exe.20260827-2316.bak
  DeepSeekHarness.exe.20260827-1506.old
  DeepSeekHarness.exe.config
```

**回滚方法**：把 `DeepSeekHarness.exe.20260917-1059.original` 改名成
`DeepSeekHarness.exe` 覆盖到 `<外接盘>\deepseek-harness\` 即可（配置与数据不受影响，
因为本次改动没有修改 `user.env` 与 `home/`）。

---

## 8. 不要做的事

1. **不要修改 `src\launcher\legacy\Program.cs`** —— 它不参与编译（`build.ps1` 只收
   `src\launcher\src\`），里面是旧版实现，逻辑与现行版本**不同**。改它是白改。
2. **不要在源码里使用 C# 6+ 语法**（见 §3.3）。
3. **不要把密钥写进任何会进仓库的文件**。
4. **不要删除 `launcher\web\`** —— 运行时要靠它做虚拟主机映射。
5. **不要用 mono 的 `mcs` 的编译结果作为交付物** —— 它无法嵌入 Win32 清单（见 §3.4 陷阱）。
