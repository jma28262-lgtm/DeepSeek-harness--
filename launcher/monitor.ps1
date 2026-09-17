<#
  调试辅助：启动 DeepSeekHarness.exe 并观察它在 2 分钟内是否异常退出。
  用法:  powershell -NoProfile -File .\monitor.ps1 -ExePath "<部署目录>\DeepSeekHarness.exe"
  历史版本把 exe 路径写死在脚本里，属于个人环境信息，已改为参数传入。
#>
param(
  [Parameter(Mandatory = $true)][string]$ExePath
)
if (-not (Test-Path $ExePath)) { Write-Error "找不到: $ExePath"; exit 1 }
$p = Start-Process $ExePath -PassThru
Write-Output "Started PID: $($p.Id) at $(Get-Date -Format 'HH:mm:ss')"
$deadline = (Get-Date).AddMinutes(2)
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Seconds 3
    if ($p.HasExited) {
        Write-Output "EXITED at $(Get-Date -Format 'HH:mm:ss') ExitCode=$($p.ExitCode)"
        exit 0
    }
}
if (-not $p.HasExited) {
    Write-Output "Still alive after 2 minutes. Killing."
    $p.Kill()
}
