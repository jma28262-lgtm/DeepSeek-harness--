# ============================================================================
#  DeepSeek Harness - first-time bootstrap (PowerShell 5.1 compatible)
#  Downloads a portable Node.js (LTS) into tools\, then installs the dsh CLI
#  into tools\global via npm. Everything stays on this drive - no admin
#  rights, no registry changes, no dependency on the system's Node/Git.
#  Normally you never run this directly: start.bat calls it when needed.
# ============================================================================
$ErrorActionPreference = 'Stop'

$Root = $PSScriptRoot
if (-not $Root) { $Root = (Get-Location).Path }

# --- 环境变量保护: 某些精简/受限环境下可能缺失 ---
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
$DlDir      = Join-Path $ToolsDir 'download'
$CacheDir   = Join-Path $ToolsDir 'npm-cache'
$DshHome    = Join-Path $Root 'home'
$WsDir      = Join-Path $Root 'workspace'
$ConfigDir  = Join-Path $Root 'config'

foreach ($d in @($ToolsDir, $NodeDir, $GlobalDir, $DlDir, $CacheDir, $DshHome, $WsDir, $ConfigDir)) {
  New-Item -ItemType Directory -Force -Path $d | Out-Null
}

$env:Path = "$NodeDir;$GlobalDir;$env:Path"
$env:npm_config_cache   = $CacheDir
$env:npm_config_prefix  = $GlobalDir
$env:npm_config_update_notifier = 'false'

function Write-Step($msg) { Write-Host "==> $msg" -ForegroundColor Cyan }

# ---------------------------------------------------------------------------
# 1. Portable Node.js (LTS) - only if not already present
# ---------------------------------------------------------------------------
if (-not (Test-Path $NodeExe)) {
  Write-Step 'Downloading portable Node.js LTS...'
  $ProgressPreference = 'SilentlyContinue'

  $nodeUrl = $null
  try {
    $index = Invoke-RestMethod -Uri 'https://nodejs.org/dist/index.json' -TimeoutSec 30
    $lts = $index | Where-Object { $_.lts -ne $false } | Select-Object -First 1
    if ($lts) {
      $ver = [string]$lts.version
      $nodeUrl = "https://nodejs.org/dist/$ver/node-$ver-win-x64.zip"
    }
  }
  catch {
    Write-Host '  (index lookup failed, using known-good fallback version)' -ForegroundColor Yellow
  }
  if (-not $nodeUrl) {
    # fallback: pinned LTS releases
    foreach ($cand in @('v22.20.0', 'v22.19.0', 'v20.19.0')) {
      $u = "https://nodejs.org/dist/$cand/node-$cand-win-x64.zip"
      try {
        $probe = Invoke-WebRequest -Uri $u -Method Head -UseBasicParsing -TimeoutSec 20
        if ($probe.StatusCode -eq 200) { $nodeUrl = $u; break }
      }
      catch { }
    }
  }
  if (-not $nodeUrl) { throw 'Could not determine a Node.js download URL. Check the network and retry.' }

  Write-Host "  $nodeUrl"
  $zip = Join-Path $DlDir 'node.zip'
  Invoke-WebRequest -Uri $nodeUrl -OutFile $zip -UseBasicParsing -TimeoutSec 600
  if (-not (Test-Path $zip)) { throw 'Node.js download failed.' }

  Write-Step 'Extracting Node.js...'
  $tmp = Join-Path $DlDir 'node-x'
  if (Test-Path $tmp) { Remove-Item $tmp -Recurse -Force }
  Expand-Archive -Path $zip -DestinationPath $tmp
  $inner = Get-ChildItem $tmp -Directory | Select-Object -First 1
  if (-not $inner) { throw 'Extracted Node.js archive has no root folder.' }
  if (Test-Path $NodeDir) { Remove-Item $NodeDir -Recurse -Force }
  Move-Item $inner.FullName $NodeDir
  Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
  Remove-Item $zip -Force -ErrorAction SilentlyContinue
}
Write-Host "  Node: $(& $NodeExe --version)"

# ---------------------------------------------------------------------------
# 2. dsh CLI (npm, portable global prefix) - only if not already present
# ---------------------------------------------------------------------------
$dsh = Join-Path $GlobalDir 'dsh.cmd'
if (-not (Test-Path $dsh)) { $dsh = Join-Path $GlobalDir 'node_modules\.bin\dsh.cmd' }

if (-not (Test-Path $dsh)) {
  Write-Step 'Installing @deepseek-ai/dsh (npm, portable prefix)...'
  # 项目模式安装 (避免全局 -g 被安全软件拦截); --legacy-peer-deps 避免 peer 解析死锁
  $npmCli = Join-Path $NodeDir 'node_modules\npm\bin\npm-cli.js'
  Push-Location $GlobalDir
  & $NodeExe $npmCli install '@deepseek-ai/dsh' --legacy-peer-deps --no-fund --no-audit
  $installCode = $LASTEXITCODE
  Pop-Location
  if ($installCode -ne 0) { throw "npm install @deepseek-ai/dsh failed (code $installCode)." }
  if (-not (Test-Path $dsh)) { $dsh = Join-Path $GlobalDir 'node_modules\.bin\dsh.cmd' }
  if (-not (Test-Path $dsh)) { throw 'dsh.cmd not found after install.' }
}
Write-Host "  dsh: $(& $dsh --version)"

# --- 补装 peer 依赖 (cordis 插件体系依赖 peerDependencies) ---
$scan = Join-Path $Root 'scripts\scan-missing.mjs'
if (Test-Path $scan) {
  Write-Step 'Installing missing peer dependencies...'
  # 必须把 global 目录显式传给脚本：scan-missing.mjs 不再内置默认目录
  # （旧默认值写死 D 盘，换盘后会静默"扫到 0 个包"却报告成功）
  $scanOut = & $NodeExe $scan $GlobalDir 2>$null | Out-String
  if ($LASTEXITCODE -eq 2) {
    Write-Host "  peer 依赖扫描失败：找不到 $GlobalDir\node_modules（已跳过）" -ForegroundColor Yellow
  } else {
    # 用 -match 而非 -like：脚本输出的行首就是 INSTALL_LIST=
    #（旧版字符串里是字面 \n，导致 -like 'INSTALL_LIST=*' 永远匹配不上，整段从未执行）
    $installLine = ($scanOut -split "`n" | Where-Object { $_ -match '^\s*INSTALL_LIST=' })
    if ($installLine) {
      $pkgs = (($installLine | Select-Object -First 1) -replace '^\s*INSTALL_LIST=','').Trim()
      if ($pkgs) {
        # 必须拆成数组：PowerShell 不会对含空格的字符串自动分词，
        # 直接传 $pkgs 会让 npm 收到一个带空格的名字而安装失败
        $pkgArray = $pkgs -split '\s+'
        Write-Host "  补装: $($pkgArray -join ', ')" -ForegroundColor Cyan
        Push-Location $GlobalDir
        & $NodeExe (Join-Path $NodeDir 'node_modules\npm\bin\npm-cli.js') install $pkgArray --legacy-peer-deps --no-fund --no-audit
        Pop-Location
      }
    }
  }
}

# ---------------------------------------------------------------------------
# 3. pnpm (optional - only needed for `dsh plugin` out-of-tree packages)
# ---------------------------------------------------------------------------
$pnpm = Join-Path $GlobalDir 'pnpm.cmd'
if (-not (Test-Path $pnpm)) {
  Write-Step 'Installing pnpm (optional, for dsh plugin management)...'
  & (Join-Path $NodeDir 'npm.cmd') install -g 'pnpm' --prefix $GlobalDir --cache $CacheDir
}

# ---------------------------------------------------------------------------
# 4. Create config\user.env from the template if missing
# ---------------------------------------------------------------------------
$sample = Join-Path $ConfigDir 'user.env.example'
$target = Join-Path $ConfigDir 'user.env'
if ((Test-Path $sample) -and -not (Test-Path $target)) {
  Copy-Item $sample $target
  Write-Host "  Created $target - edit it to add your DeepSeek API key." -ForegroundColor Yellow
}

# ---------------------------------------------------------------------------
# 5. Marker + summary
# ---------------------------------------------------------------------------
$stamp = Get-Date -Format 'yyyy-MM-dd HH:mm'
$nodeV = & $NodeExe --version
$dshV  = & $dsh --version
Set-Content -Path (Join-Path $ToolsDir '.bootstrap-done') `
  -Value "bootstrap ok  $stamp  node=$nodeV  dsh=$dshV" -Encoding UTF8

Write-Host ''
Write-Host '============================================================' -ForegroundColor Green
Write-Host ' Bootstrap complete.' -ForegroundColor Green
Write-Host '   - Node.js : tools\node' -ForegroundColor Green
Write-Host '   - dsh CLI : tools\global' -ForegroundColor Green
Write-Host '   - config  : config\user.env (add DEEPSEEK_API_KEY here)' -ForegroundColor Green
Write-Host ' Run start.bat to launch the Web UI.' -ForegroundColor Green
Write-Host '============================================================' -ForegroundColor Green
