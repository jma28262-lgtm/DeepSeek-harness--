# ============================================================================
#  DeepSeek Harness - portable launcher (PowerShell 5.1 compatible)
#  Called by start.bat. Resolves everything relative to THIS script's folder,
#  so the whole deployment can live on a removable drive with any drive letter.
# ============================================================================
$ErrorActionPreference = 'Stop'

$Root = $PSScriptRoot
if (-not $Root) { $Root = (Get-Location).Path }

# --- 环境变量保护: 某些精简/受限环境下可能缺失, 补全以免 dsh 无法运行 ---
if (-not $env:SystemRoot) { $env:SystemRoot = "$env:WINDIR" }
if (-not $env:COMSPEC) { $env:COMSPEC = "$env:SystemRoot\System32\cmd.exe" }
if (-not $env:PATHEXT) { $env:PATHEXT = ".COM;.EXE;.BAT;.CMD" }
if (-not $env:USERPROFILE) { $env:USERPROFILE = "$env:SystemDrive\Users\$([Environment]::UserName)" }
if (-not $env:APPDATA) { $env:APPDATA = "$env:USERPROFILE\AppData\Roaming" }
if (-not $env:LOCALAPPDATA) { $env:LOCALAPPDATA = "$env:USERPROFILE\AppData\Local" }
if (-not $env:HOME) { $env:HOME = $env:USERPROFILE }
if (-not $env:TEMP) { $env:TEMP = "$env:LOCALAPPDATA\Temp" }
$ToolsDir   = Join-Path $Root 'tools'
$NodeDir    = Join-Path $ToolsDir 'node'
$NodeExe    = Join-Path $NodeDir 'node.exe'
$GlobalDir  = Join-Path $ToolsDir 'global'
$DshHome    = Join-Path $Root 'home'
$WsDir      = Join-Path $Root 'workspace'
$ConfigDir  = Join-Path $Root 'config'
$UserEnv    = Join-Path $ConfigDir 'user.env'
$Bootstrap  = Join-Path $Root 'bootstrap.ps1'
$AutoConfig = Join-Path $Root 'scripts\auto-config.mjs'
$ModelStarter = Join-Path $Root 'scripts\start-model-server.ps1'

function Write-Step($msg) { Write-Host "==> $msg" -ForegroundColor Cyan }
function Write-Err($msg)  { Write-Host "ERROR: $msg" -ForegroundColor Red }

# ---------------------------------------------------------------------------
# 0. Load user settings from config\user.env (KEY=VALUE lines, # comments)
# ---------------------------------------------------------------------------
if (Test-Path $UserEnv) {
  Get-Content $UserEnv | ForEach-Object {
    $line = $_.Trim()
    if ($line -and -not $line.StartsWith('#')) {
      $idx = $line.IndexOf('=')
      if ($idx -gt 0) {
        $k = $line.Substring(0, $idx).Trim()
        $v = $line.Substring($idx + 1).Trim().Trim('"')
        if ($k -match '^[A-Za-z_][A-Za-z0-9_]*$') {
          Set-Item -Path "Env:$k" -Value $v
        }
      }
    }
  }
}

# ---------------------------------------------------------------------------
# 1. Ensure the required directories exist
# ---------------------------------------------------------------------------
foreach ($d in @($ToolsDir, $GlobalDir, $DshHome, $WsDir, $ConfigDir)) {
  New-Item -ItemType Directory -Force -Path $d | Out-Null
}

# ---------------------------------------------------------------------------
# 2. First run? Install portable Node.js + dsh automatically
# ---------------------------------------------------------------------------
$dshCandidates = @(
  (Join-Path $GlobalDir 'dsh.cmd'),
  (Join-Path $GlobalDir 'node_modules\.bin\dsh.cmd')
)
$dsh = $dshCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1

$useSystemNode = ($env:DSH_SYSTEM_NODE -eq '1')
if (-not $useSystemNode -and -not (Test-Path $NodeExe)) {
  Write-Step 'Portable Node.js not found -> running first-time bootstrap...'
  & $Bootstrap
  if (-not (Test-Path $NodeExe)) { Write-Err 'Bootstrap did not install Node.js.'; exit 1 }
}
if (-not $dsh) {
  Write-Step 'dsh CLI not found -> running first-time bootstrap...'
  & $Bootstrap
  $dsh = $dshCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
  if (-not $dsh) { Write-Err 'Bootstrap did not install dsh.'; exit 1 }
}

# ---------------------------------------------------------------------------
# 3. Environment: PATH, DSH_HOME, portable npm cache/prefix
# ---------------------------------------------------------------------------
if ($useSystemNode) {
  $nodeCmd = Get-Command node -ErrorAction SilentlyContinue
  if (-not $nodeCmd) { Write-Err 'DSH_SYSTEM_NODE=1 but no node on PATH.'; exit 1 }
  $env:Path = "$GlobalDir;$env:Path"
} else {
  $env:Path = "$NodeDir;$GlobalDir;$env:Path"
}
$env:DSH_HOME = $DshHome
$env:npm_config_cache   = Join-Path $ToolsDir 'npm-cache'
$env:npm_config_prefix  = $GlobalDir
$env:npm_config_update_notifier = 'false'

# ---------------------------------------------------------------------------
# 3b. Auto-start local model server (llama.cpp / Ollama) if configured
# ---------------------------------------------------------------------------
if ($env:DSH_AUTO_START_MODEL -eq '1') {
  Write-Step 'Auto-starting local model server...'
  & $ModelStarter
  if ($LASTEXITCODE -ne 0) {
    Write-Host '  (model server did not start; continuing with DeepSeek API / existing endpoints)' -ForegroundColor Yellow
  }
}

# ---------------------------------------------------------------------------
# 4. Auto-configuration: detect local model servers, update settings.yaml
# ---------------------------------------------------------------------------
Write-Step 'Detecting local model endpoints and generating configuration...'
if ($useSystemNode) {
  & node $AutoConfig
} else {
  & $NodeExe $AutoConfig
}
if ($LASTEXITCODE -ne 0) {
  Write-Err "auto-config failed (code $LASTEXITCODE) - continuing with existing config."
}

# ---------------------------------------------------------------------------
# 5. Build dsh arguments
# ---------------------------------------------------------------------------
$appArgs = @('web')
if ($env:DSH_PORT) { $appArgs += @('--port', $env:DSH_PORT) }
if ($env:DSH_NO_OPEN -eq '1') { $appArgs += '--no-open' }
if ($env:DSH_EXTRA_ARGS) {
  $appArgs += @($env:DSH_EXTRA_ARGS -split '\s+' | Where-Object { $_ })
}

# ---------------------------------------------------------------------------
# 6. Launch
# ---------------------------------------------------------------------------
Write-Step "Starting DeepSeek Harness Web UI (DSH_HOME=$DshHome)..."
$displayPort = if ($env:DSH_PORT) { $env:DSH_PORT } else { '3080' }
Write-Host "URL: http://127.0.0.1:$displayPort" -ForegroundColor Yellow
Write-Host 'Press Ctrl+C in this window to stop the server.' -ForegroundColor Yellow
Write-Host ''

Push-Location $WsDir
try {
  & $dsh @appArgs
  $code = $LASTEXITCODE
  Write-Host "`ndsh exited with code $code" -ForegroundColor Cyan
  if ($code -ne 0) { exit $code }
}
finally {
  Pop-Location
}
