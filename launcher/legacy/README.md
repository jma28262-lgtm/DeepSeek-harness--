# legacy —— 不要修改这里的代码

`Program.cs` 是**被 `launcher/src/` 取代的旧版单体实现**，它**不参与编译**。

证据：`launcher/build.ps1` 只收集 `launcher/src/**/*.cs`：

```powershell
$SrcDir = Join-Path $PSScriptRoot "src"
$Sources = Get-ChildItem -Path $SrcDir -Recurse -Filter "*.cs" | ...
```

而 `Program.cs` 位于 `launcher/` 下、不在 `src/` 里。时间戳也印证了替代关系：
`DeepSeekHarness.exe.old`（41984B）与 `Program.cs` 的最后修改时间完全相同，
之后才有 `src/` 拆分与更新的 exe。

它里面是旧版实现：自己的 `MainForm`、自己的 `WriteUserEnv`、自己的 `taskkill /PID /T /F`，
含与 `src/` **不同**的逻辑（例如起 powershell 时 PATH 拼接未加引号，安装路径含空格会被截断）。

**把它留在这里的唯一目的是保留历史，绝不作为修改入口。** 审计过程中已有人把它误当成
"真正的入口"并在其中定位 bug —— 这正是把死文件与活代码放在一起的代价。
