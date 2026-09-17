# 仓库设置建议（About / Topics / 界面布局）

本文件是**给你在 GitHub 网页上操作时直接复制粘贴用的**，不参与构建。
（Deploy key 只能推 git，改不了仓库设置，所以只能给你文案。）

---

## 1. 仓库描述（About）

GitHub 的 About 栏建议这样填（上限 350 字符）。

**推荐版（约 120 字，信息密度高）**：

```
DeepSeek Harness 的 Windows 便携启动器：一键环境配置、换机路径自愈、凭据加密（口令模式跨机可用）、Job Object 进程托管，自动探测并拉起 llama.cpp / Ollama 本地模型服务。
```

**备选版（突出技术栈）**：

```
DeepSeek Harness (dsh) 的 Windows 桌面启动器 · C# WinForms + WebView2 · 无运行时依赖 · 一键部署 · 加密凭据 · 换机自适应 · 本地模型服务托管
```

**Website** 一栏留空，或填上游项目：`https://github.com/deepseek-ai/deepseek-harness`

---

## 2. Topics（标签）

GitHub 的 Topics 上限 20 个，全小写、用连字符。建议按此顺序填（前几个最重要）：

```
deepseek-harness
dsh
launcher
windows
csharp
winforms
webview2
dotnet-framework
portable-app
local-llm
llama-cpp
ollama
ai-agent
agent-harness
mcp
credential-encryption
dpapi
process-supervision
```

---

## 3. 仓库界面布局

README 已按"落地页"结构写好，仓库首页从上到下的阅读顺序是：

| 位置 | 内容 | 作用 |
|---|---|---|
| 1 | 标题 + 一句话定位 | 3 秒内说清是什么 |
| 2 | 徽章行（平台 / 框架 / 语言 / UI / 依赖数） | 一眼看到技术栈，"无运行时依赖"是卖点 |
| 3 | **它解决什么问题**（表格：痛点 → 做法） | 用问题而不是功能列表开头，读者才有代入感 |
| 4 | **界面**（两张截图） | 视觉说服力；已注明是演示模式渲染 |
| 5 | 快速开始（4 步） | 让读者知道最短路径 |
| 6 | 核心能力（分小节） | 详细展开：环境配置页 / 凭据加密 / 进程托管 / 与 dsh 的分工 |
| 7 | 构建（含两条 ⚠️ 提醒） | 给想改代码的人 |
| 8 | 项目结构 | 给想读代码的人 |
| 9 | 安全须知 | 建立信任，也是真实约束 |
| 10 | 相关链接 | 导流到上游 |

文件树本身也参与"界面"：`docs/HANDOVER.md`、`docs/VERIFICATION.md`、`docs/OPTIMIZATION-REVIEW.md`
放在根目录，让人一眼看到这个项目**有交接文档、有验证记录、有自我审查** ——
这比多写三个功能更能说明工程成熟度。

---

## 4. 建议一起做的仓库设置

| 设置 | 建议 | 理由 |
|---|---|---|
| **Default branch** | `main` | 当前 `main` 已包含本项目全部内容 |
| **License** | 建议加一个（如 MIT）；**目前仓库没有 LICENSE** | 没有许可证默认是"保留所有权利"，别人不能合法使用 |
| **Social preview** | 上传 `docs/screenshots/setup-page.png` | 分享链接时的缩略图，默认是灰底文件名，很难看 |
| **Releases** | 打一个 `v1.0.0` tag，附件挂 `DeepSeekHarness.exe` | 二进制不进 git 是好事，但要发版就得靠 Release 附件 |
| **`.gitattributes`** | 可选：`*.png binary`、`*.bat text eol=crlf`、`*.ps1 text eol=crlf` | 本项目历史文件行尾混用（CRLF/LF 并存），加了能防止后续 diff 噪声 |
| **About → Releases / Packages** | 保持默认 | — |

---

## 5. 关于分支

本项目首次上传时推到了 `feature/secure-credentials-and-auto-setup`，
随后**已合并进 `main`**，所以 `main` 就是完整版本。

如果之后要按功能开发，建议：

- `main` 保持可交付状态
- 功能走 `feat/xxx` 分支 + PR
- README 与交接文档的改动跟代码**同一次提交**（避免文档与实现漂移）

---

## 6. 中文项目的一个小建议

仓库主要面向中文用户，建议在 About 描述里**保留中文**（如上），
但在 README 开头加一行英文摘要，便于非中文用户判断用途：

```markdown
> A Windows portable launcher for DeepSeek Harness (dsh) — one-click setup,
> machine-migration self-healing, encrypted credentials, and local model
> (llama.cpp / Ollama) lifecycle management.
```
