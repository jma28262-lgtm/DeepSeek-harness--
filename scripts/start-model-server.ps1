# ============================================================================
#  DeepSeek Harness - local model server auto-starter
#  Called by start.ps1 before auto-config. Reads config from env vars
#  (already loaded from config\user.env by start.ps1).
#  - Checks if the configured port is already serving; if yes, skips.
#  - If DSH_AUTO_START_MODEL=1, launches llama.cpp or Ollama.
#  - Polls /v1/models until ready (timeout 120s).
#  Exit codes: 0 = ready (or skipped), 1 = failed to start.
# ============================================================================
$ErrorActionPreference = 'Stop'

$Root       = $PSScriptRoot | Split-Path -Parent
$ToolsDir   = Join-Path $Root 'tools'
$LogsDir    = Join-Path $Root 'logs'
if (-not (Test-Path $LogsDir)) { New-Item -ItemType Directory -Force -Path $LogsDir | Out-Null }

function Write-Step($msg) { Write-Host "==> $msg" -ForegroundColor Cyan }
function Write-Err($msg)  { Write-Host "ERROR: $msg" -ForegroundColor Red }
function Write-Warn2($msg) { Write-Host "WARN: $msg" -ForegroundColor Yellow }

# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------
function Test-PortListening($port) {
  try {
    $tcp = New-Object System.Net.Sockets.TcpClient
    $iar = $tcp.BeginConnect('127.0.0.1', $port, $null, $null)
    $wait = $iar.AsyncWaitHandle.WaitOne(800, $false)
    if ($wait -and $tcp.Connected) { $tcp.EndConnect($iar); $tcp.Close(); return $true }
    $tcp.Close()
  } catch {}
  return $false
}

function Wait-ForEndpoint($url, $timeoutSec = 120) {
  $deadline = (Get-Date).AddSeconds($timeoutSec)
  while ((Get-Date) -lt $deadline) {
    try {
      $r = Invoke-WebRequest -Uri $url -UseBasicParsing -TimeoutSec 3 -ErrorAction Stop
      if ($r.StatusCode -eq 200) { return $true }
    } catch { }
    Start-Sleep -Milliseconds 800
  }
  return $false
}

# ---------------------------------------------------------------------------
# 0. Auto-start enabled?
# ---------------------------------------------------------------------------
if ($env:DSH_AUTO_START_MODEL -ne '1') {
  Write-Host '  (model auto-start disabled; set DSH_AUTO_START_MODEL=1 in user.env to enable)'
  exit 0
}

$backend = $env:DSH_MODEL_BACKEND
if (-not $backend) { $backend = 'llama-cpp' }

# ---------------------------------------------------------------------------
# 1. llama.cpp
# ---------------------------------------------------------------------------
if ($backend -eq 'llama-cpp') {
  $llamaDir = if ($env:DSH_LLAMA_DIR) { $env:DSH_LLAMA_DIR } else { Join-Path $Root '..\llama.cpp' }
  $llamaExe = Join-Path $llamaDir 'llama-server.exe'
  $model    = $env:DSH_LLAMA_MODEL
  $port     = if ($env:DSH_LLAMA_PORT) { [int]$env:DSH_LLAMA_PORT } else { 11435 }
  $ctx      = if ($env:DSH_LLAMA_CONTEXT) { $env:DSH_LLAMA_CONTEXT } else { '4096' }
  $gpu      = if ($env:DSH_LLAMA_GPU_LAYERS) { $env:DSH_LLAMA_GPU_LAYERS } else { '99' }
  $extra    = $env:DSH_LLAMA_EXTRA_ARGS

  # Already running?
  if (Test-PortListening $port) {
    Write-Step "llama-server already listening on port $port - skipping start."
    exit 0
  }

  if (-not (Test-Path $llamaExe)) {
    Write-Err "llama-server.exe not found at $llamaExe. Set DSH_LLAMA_DIR in user.env."
    exit 1
  }
  if (-not (Test-Path $model)) {
    Write-Err "Model file not found: $model. Set DSH_LLAMA_MODEL in user.env."
    exit 1
  }

  $logFile = Join-Path $LogsDir 'llama-server.log'
  $args = @('-m', $model, '--port', "$port", '-c', $ctx, '-ngl', $gpu, '--host', '127.0.0.1')
  if ($extra) { $args += ($extra -split '\s+' | Where-Object { $_ }) }

  Write-Step "Starting llama-server (port $port, model $(Split-Path $model -Leaf))..."
  Write-Host "  $llamaExe $($args -join ' ')"

  try {
    $p = Start-Process -FilePath $llamaExe -ArgumentList $args `
      -WorkingDirectory $llamaDir -RedirectStandardOutput $logFile -RedirectStandardError "$logFile.err" `
      -WindowStyle Hidden -PassThru
  } catch {
    Write-Err "Failed to launch llama-server: $_"
    exit 1
  }

  Write-Host "  PID: $($p.Id), log: $logFile"
  Write-Step 'Waiting for llama-server to become ready...'
  if (Wait-ForEndpoint "http://127.0.0.1:$port/v1/models" 120) {
    Write-Host '  llama-server is ready.' -ForegroundColor Green
    exit 0
  } else {
    Write-Err 'llama-server did not become ready within 120s. Check logs\llama-server.log'
    if (-not $p.HasExited) { Write-Host "  (process still running, PID $($p.Id))" -ForegroundColor Yellow }
    exit 1
  }
}

# ---------------------------------------------------------------------------
# 2. Ollama
# ---------------------------------------------------------------------------
if ($backend -eq 'ollama') {
  $ollamaExe = if ($env:DSH_OLLAMA_EXE) { $env:DSH_OLLAMA_EXE } else { 'ollama.exe' }
  $port      = 11434
  $model     = $env:DSH_OLLAMA_MODEL

  if (Test-PortListening $port) {
    Write-Step "Ollama already listening on port $port - skipping start."
    if ($model) {
      Write-Step "Pre-loading model: $model"
      & $ollamaExe run $model 2>&1 | Out-Null
    }
    exit 0
  }

  if (-not (Get-Command $ollamaExe -ErrorAction SilentlyContinue) -and -not (Test-Path $ollamaExe)) {
    Write-Err "ollama.exe not found. Set DSH_OLLAMA_EXE in user.env."
    exit 1
  }

  $logFile = Join-Path $LogsDir 'ollama.log'
  Write-Step 'Starting Ollama serve...'
  $p = Start-Process -FilePath $ollamaExe -ArgumentList 'serve' `
    -RedirectStandardOutput $logFile -RedirectStandardError "$logFile.err" `
    -WindowStyle Hidden -PassThru

  Write-Step 'Waiting for Ollama to become ready...'
  if (Wait-ForEndpoint 'http://127.0.0.1:11434/api/tags' 60) {
    Write-Host '  Ollama is ready.' -ForegroundColor Green
    if ($model) {
      Write-Step "Pre-loading model: $model"
      & $ollamaExe run $model 2>&1 | Out-Null
    }
    exit 0
  } else {
    Write-Err 'Ollama did not become ready within 60s. Check logs\ollama.log'
    exit 1
  }
}

Write-Err "Unknown backend: $backend. Set DSH_MODEL_BACKEND to 'llama-cpp' or 'ollama'."
exit 1
