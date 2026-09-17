# DeepSeek Harness 桌面工作台（便携部署 + 自研启动器）

以 [DeepSeek Harness](https://github.com/deepseek-ai/deepseek-harness)（`dsh`，插件化 Agent Harness）为底座，
配一个自研的 **Windows 桌面启动器**：负责环境部署、模型服务生命周期、凭据加密、插件管理。
**目标是「换机直接可用」**——把换电脑后需要人工重做的事，尽量压缩到"最多输一次口令"。

---

## 快速开始

1. 运行 `DeepSeekHarness.exe`（双击即可，无需管理员权限）
2. 首次在新机器上：进入 **环境配置** 页 → 点 **一键配置环境**
3. 如果凭据库提示需要口令 → 输入一次（之后本机免密）
4. 回到仪表盘点 **启动服务**，然后 **打开 Web UI**

命令行备用方式：`start.bat`（功能与 exe 不完全等价，见下方「启动路径」）。

---

## 主要能力

### 环境配置页（换机自动配置）

| 能力 | 说明 |
|---|---|
| **换机检测** | 机器指纹（`MachineGuid` + 机器名 + 用户名 的 SHA-256）。指纹变化即判定换了电脑，启动时自动跑一轮完整自检 |
| **路径自愈** | 配置里写死的绝对路径失效时，在其它盘按"相对尾部"重新定位并改写（模型按同名 `.gguf` 找、目录按 `Ollama` 找、可执行文件按 `llama-server.exe`/`ollama.exe` 找）。改前备份 `user.env.bak` |
| **一键配置** | 运行时检查/自动部署 → 路径自愈 → 端口检查 → 凭据状态 → 调用 `scripts/auto-config.mjs` 探测并生成模型 provider |
| **结果可见** | 每一步的状态（正常／已自动修复／需注意／失败）与具体说明都列在页面上，不吞错误 |

> 为什么需要"路径自愈"：历史配置里出现过 `DSH_LLAMA_DIR=G:\Ollama` 与
> `DSH_LLAMA_MODEL=D:\Ollama\Models\...` **分属两个盘**的情况。换机后盘符一变，
> 配置全部失效，而表现只是"启动成功但没有本地模型"——最难排查的一类故障。

### 凭据加密（磁盘上不出现明文）

两种模式，都在磁盘上不存明文：

| 模式 | 换机可用 | 输入成本 | 实现 |
|---|---|---|---|
| **口令模式**（默认，推荐） | ✅ 凭据文件可随盘搬到任何电脑 | 每台新机器一次口令，之后该机免密 | PBKDF2-HMAC-SHA256（20 万次迭代）+ AES-256-CBC + HMAC-SHA256（encrypt-then-MAC） |
| 本机模式 | ❌ 只能在本机本账户解开 | 零输入 | DPAPI（CurrentUser） |

**为什么跨机必然要输一次口令**：若要让"换机零输入"成立，解密密钥就必须和密文同盘存放，
那与明文无异，只是看起来像加密。这是约束，不是实现取舍。

密钥的使用方式：**只以环境变量注入 dsh 子进程，不写入任何配置文件**。
依据是 `dsh-credentials-local` 的解析顺序 `env → .credentials.yaml → .env`（环境变量优先，
且此时 `describe()` 返回 `writable:false`）。

首次运行会自动把历史遗留在 `config/user.env` 与 `home/.credentials.yaml` 里的明文
收进加密库并**从原文件抹掉**（幂等，改前留 `.bak`）。

### 全自动 + 可自定义

- **自动**：本地模型端点探测、provider 生成、路径修复、运行时部署
- **自定义**：DSH 自带的 Web UI 已有 Models 页面管理 provider —— 启动器**不重复实现**，
  只负责自动播种；细粒度调整请用 DSH 自己的界面

### 进程托管

启动器把**自身**放入 Windows Job Object（`JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`），
之后派生的 `node` / `powershell` / `llama-server` / `ollama` 全部自动继承 Job。
退出时由内核一并终止 —— 解决"关掉启动器后模型服务仍在占显存"，
这是 `taskkill /T /F` 覆盖不到的（模型服务是已退出的 PowerShell 的子进程，不在 dsh 的进程树里）。

### 插件管理

经 `dsh plugin --profile web add/remove <包名>` 管理（转发给 profile 目录内的 pnpm），
与 DSH 官方方式一致。

---

## 启动路径

| 入口 | 行为 |
|---|---|
| **`DeepSeekHarness.exe`（推荐）** | 直接调用便携 Node + `dsh/lib/bin.js`；编排逻辑在 C# 内（环境自检、模型拉起、凭据注入） |
| `start.bat` / `start.ps1` | 命令行备用；会跑 `scripts/auto-config.mjs` |

两条路径的编排逻辑本应一致，这是已知的架构债（见审查文档 §4.1）。

---

## 目录结构

```
├── DeepSeekHarness.exe       启动器（由 launcher/ 编译，见 build.ps1）
├── launcher/
│   ├── src/                  C# 源码（build.ps1 只编译这里）
│   ├── web/                  WebView2 界面（原生 HTML/CSS/JS）
│   ├── legacy/               旧版单体实现，不参与编译，仅存档
│   └── build.ps1             编译脚本（.NET Framework 4.8 csc）
├── scripts/
│   ├── auto-config.mjs       模型端点探测 + 配置生成
│   └── scan-missing.mjs      peer 依赖扫描
├── config/
│   ├── user.env.example      配置模板
│   └── user.env              实际配置（**已 gitignore**）
├── home/                     DSH_HOME：配置/凭据/会话（**已 gitignore**）
├── tools/                    便携 Node、npm 全局目录（**已 gitignore**）
└── OPTIMIZATION-REVIEW.md    代码审查与优化方案（含实测复现的缺陷）
```

---

## 编译

```
cd launcher
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
```

需要 .NET Framework 4.8 的 `csc.exe`（`C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe`）
与 `launcher\wv2pkg\pkg` 下的 WebView2 程序集。

**注意**：`build.ps1` 使用的这个 `csc.exe` 只支持 **C# 5** 语法 ——
不要使用字符串插值、`?.`、表达式体成员、`out var` 等新语法，否则编译不过。

---

## 安全须知

1. **不要提交 `config/user.env` 与 `home/`**。`.gitignore` 已覆盖，但请勿手工 `git add -f`。
2. 若历史明文密钥曾经外泄（例如随 U 盘流动），请到
   [platform.deepseek.com](https://platform.deepseek.com/) 轮换。
3. Windows 上 DSH 自身不做凭据文件权限校验
   （`dsh-credentials-local` 里有 `if (process.platform === "win32") return;`），
   因此**不要依赖文件权限**保护密钥 —— 这就是本启动器加密凭据的原因。

---

## 前端预览（无需 Windows）

`launcher/web` 是纯静态页面。用一个静态服务器打开即可预览界面：

```bash
npx serve launcher/web      # 或任意静态服务器
```

在没有 WebView2 宿主时，`bridge.js` 会进入**演示模式**（顶部有明确提示条），
用内置假数据渲染界面 —— 任何操作都不会真正生效，便于改界面时快速看效果。
