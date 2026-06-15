# build-and-publish.ps1 — build Shonei for Windows + Mac and push both to itch.io.
#
# Run from anywhere:  .\Tools\build-and-publish.ps1
#
# One source of truth for the version is PlayerSettings bundleVersion (set it in
# Edit > Project Settings > Player). This reads it and passes it to butler as
# --userversion; the in-game VersionLabel reads the same value via Application.version.
#
# Prereqs:
#   - The Unity editor matching ProjectSettings/ProjectVersion.txt installed via
#     Unity Hub (path auto-detected from that version; override with -UnityExe).
#   - The editor must be CLOSED — batchmode can't open a project Unity already holds.
#   - butler installed + logged in (`butler login`). Ships with the itch app.
#
# Flags:
#   -WindowsOnly   skip the Mac build (faster iteration; Mac cross-build is finicky)
#   -SkipPush      build only, don't upload

param(
    [string] $UnityExe,
    [switch] $WindowsOnly,
    [switch] $SkipPush
)

$ErrorActionPreference = "Stop"

# ── Config ──────────────────────────────────────────────────────────
$ItchTarget = "anitagarden/shonei"
$WinChannel = "win-64"
$MacChannel = "mac"

$ProjectPath = Split-Path $PSScriptRoot -Parent              # Tools/ -> project root
$BuildsDir   = Join-Path (Split-Path $ProjectPath -Parent) "builds"
$WinPushDir  = Join-Path $BuildsDir "win"
$MacPushDir  = Join-Path $BuildsDir "mac"

# ── Resolve Unity + version ─────────────────────────────────────────
# Pin the editor to whatever ProjectVersion.txt says so the build always uses the
# version the project was authored against. Using a mismatched editor (e.g. the old
# 2021 install against a Unity-6 project) makes the package manager fail to resolve
# the manifest's Unity-6 package versions.
if (-not $UnityExe) {
    $verFile = Join-Path $ProjectPath "ProjectSettings\ProjectVersion.txt"
    $editorVer = (Select-String -Path $verFile -Pattern '^m_EditorVersion:\s*(.+)$').Matches[0].Groups[1].Value.Trim()
    if (-not $editorVer) { throw "Could not read m_EditorVersion from $verFile." }
    $UnityExe = "C:\Program Files\Unity\Hub\Editor\$editorVer\Editor\Unity.exe"
}
if (-not (Test-Path $UnityExe)) { throw "Unity not found at $UnityExe -- install that version via Unity Hub or pass -UnityExe <path>." }

$settings    = Join-Path $ProjectPath "ProjectSettings\ProjectSettings.asset"
$versionLine = Select-String -Path $settings -Pattern '^\s*bundleVersion:\s*(.+)$'
if (-not $versionLine) { throw "Could not read bundleVersion from $settings." }
$Version = $versionLine.Matches[0].Groups[1].Value.Trim()
Write-Host "Building Shonei v$Version" -ForegroundColor Cyan

# Resolve butler: prefer one on PATH, else the newest version the itch app manages
# under broth/ (that folder is version-pinned, so don't hardcode it). Only needed
# when we're actually pushing.
$Butler = $null
if (-not $SkipPush) {
    $onPath = Get-Command butler -ErrorAction SilentlyContinue
    if ($onPath) {
        $Butler = $onPath.Source
    } else {
        $broth = Join-Path $env:APPDATA "itch\broth\butler\versions"
        $Butler = Get-ChildItem -Path $broth -Filter butler.exe -Recurse -ErrorAction SilentlyContinue |
            Sort-Object { [version]$_.Directory.Name } -ErrorAction SilentlyContinue |
            Select-Object -Last 1 -ExpandProperty FullName
    }
    if (-not $Butler) { throw "butler not found on PATH or under $env:APPDATA\itch. Run 'butler login' once, or pass -SkipPush to build only." }
}

# ── Build (Unity batchmode) ─────────────────────────────────────────
$method = if ($WindowsOnly) { "BuildScript.BuildWindows" } else { "BuildScript.BuildAll" }
New-Item -ItemType Directory -Force -Path $BuildsDir | Out-Null

$log = Join-Path $BuildsDir "build.log"
Write-Host "Running Unity batchmode ($method) -- this takes a few minutes..." -ForegroundColor Cyan
Write-Host "  (full log streams to $log)" -ForegroundColor DarkGray

# Unity.exe is a GUI-subsystem app, so PowerShell's `&` would NOT wait for it
# (it returns immediately and leaves $LASTEXITCODE empty). Start-Process -Wait
# blocks until Unity exits and -PassThru gives us its real exit code.
$unityArgs = @(
    "-quit", "-batchmode", "-nographics",
    "-projectPath", $ProjectPath,
    "-executeMethod", $method,
    "-buildsDir", $BuildsDir,
    "-logFile", $log
)
$proc = Start-Process -FilePath $UnityExe -ArgumentList $unityArgs -Wait -PassThru
if ($proc.ExitCode -ne 0) {
    Write-Host "--- last 40 log lines ---" -ForegroundColor Yellow
    if (Test-Path $log) { Get-Content $log -Tail 40 }
    throw "Unity build failed (exit $($proc.ExitCode)). Full log: $log"
}

# ── Strip non-shippable debug folders ───────────────────────────────
# Unity drops *_DoNotShip / *_BackUpThisFolder_* next to the output. Never ship
# them; remove before pushing so they don't bloat the upload.
Get-ChildItem -Path $BuildsDir -Recurse -Directory -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -like "*_DoNotShip" -or $_.Name -like "*_BackUpThisFolder_ButDontShipItWithYourGame" } |
    ForEach-Object { Write-Host "Removing $($_.FullName)"; Remove-Item -Recurse -Force $_.FullName }

if ($SkipPush) { Write-Host "Build done (push skipped)." -ForegroundColor Green; return }

# ── Push to itch ────────────────────────────────────────────────────
Write-Host "Pushing $WinChannel..." -ForegroundColor Cyan
& $Butler push $WinPushDir "${ItchTarget}:${WinChannel}" --userversion $Version
if ($LASTEXITCODE -ne 0) { throw "butler push ($WinChannel) failed." }

if (-not $WindowsOnly) {
    Write-Host "Pushing $MacChannel..." -ForegroundColor Cyan
    & $Butler push $MacPushDir "${ItchTarget}:${MacChannel}" --userversion $Version
    if ($LASTEXITCODE -ne 0) { throw "butler push ($MacChannel) failed." }
}

Write-Host "Done -- Shonei v$Version pushed to itch." -ForegroundColor Green
