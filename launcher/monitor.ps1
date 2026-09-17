$p = Start-Process "D:\deepseek-harness\DeepSeekHarness.exe" -PassThru
Write-Output "Started PID: $($p.Id) at $(Get-Date -Format 'HH:mm:ss')"
$deadline = (Get-Date).AddMinutes(2)
$lastState = "alive"
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Seconds 3
    $alive = -not $p.HasExited
    if (-not $alive) {
        Write-Output "EXITED at $(Get-Date -Format 'HH:mm:ss') ExitCode=$($p.ExitCode)"
        break
    }
}
if (-not $p.HasExited) {
    Write-Output "Still alive after 2 minutes. Killing."
    $p.Kill()
}
