# FreshWin (c) Ze'ev Russak — Apache License 2.0 (see LICENSE + NOTICE).
# Derived from ZuTM's install-checks.ps1.
#
# Installer checks: a real msiexec round trip against a built MSI —
#   1. silent install at the default location
#   2. payload present (exe + any file you name via -PayloadFile)
#   3. Start-menu shortcut resolves at the installed exe
#   4. the installed app shows a window whose title matches -WindowTitle
#   5. uninstall removes the payload
#
# Runs on the host or inside the guest (same script both places).
#
# Usage: powershell -File install-checks.ps1 -Msi app.msi [-ShortcutName app]
#        [-WindowTitle app] [-PayloadFile Microsoft.UI.Xaml.dll] [-ReportDir dir]
# Exit 0 = pass, 1 = fail. With -ReportDir, the resolved install directory
# is written to <ReportDir>\freshwin-step-result.txt for follow-up steps
# (substitute as {stepResult} in their arguments).

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Msi,
    [string]$ShortcutName = '',
    [string]$WindowTitle = '',
    [string]$PayloadFile = 'Microsoft.UI.Xaml.dll',
    [string]$ReportDir = ''
)

$ErrorActionPreference = 'Stop'

# --- preconditions ----------------------------------------------------------
$msiPath = (Resolve-Path $Msi).Path
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]$identity
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Output "FAIL: installer checks require an elevated process (per-machine MSI)."
    Write-Error "installer checks require an elevated process (per-machine MSI)."
    exit 1
}

# Shortcut/menu folder names come from the MSI when not supplied; guess the
# app name from the MSI file name ( MyApp-x64.msi → MyApp ).
if (-not $ShortcutName) {
    $ShortcutName = [IO.Path]::GetFileNameWithoutExtension($msiPath) -replace '-(x64|arm64)$', ''
}
if (-not $WindowTitle) { $WindowTitle = $ShortcutName }

$vmFolder = Join-Path ([IO.Path]::GetTempPath()) ("freshwin-vms-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $vmFolder | Out-Null

function Fail([string]$message) {
    Write-Output "FAIL: $message"
    Write-Error $message
    Remove-Item -Recurse -Force $vmFolder -ErrorAction SilentlyContinue
    exit 1
}

Write-Host "installer checks: $msiPath"

# --- 1. install / upgrade in place -----------------------------------------
$installStarted = Get-Date
$p = Start-Process -FilePath msiexec.exe -ArgumentList @(
    '/i', "`"$msiPath`"", '/qn', '/norestart'
) -Wait -PassThru
if ($p.ExitCode -ne 0) { Fail "msiexec install failed with exit code $($p.ExitCode)" }
Write-Host 'install: ok'

# --- 2. shortcut (also the authoritative install location — many MSIs leave
#        the registry InstallLocation empty). Adjust for per-user packages. --
$shortcut = Join-Path ([Environment]::GetFolderPath('CommonPrograms')) "$ShortcutName\$ShortcutName.lnk"
if (-not (Test-Path $shortcut)) {
    $shortcut = Join-Path ([Environment]::GetFolderPath('Programs')) "$ShortcutName\$ShortcutName.lnk"
}
if (-not (Test-Path $shortcut)) { Fail "Start-menu shortcut missing at $shortcut" }
$shell = New-Object -ComObject WScript.Shell
try {
    $installDir = $shell.CreateShortcut($shortcut).TargetPath | Split-Path -Parent
} finally {
    [void][Runtime.InteropServices.Marshal]::ReleaseComObject($shell)
}
if (-not $installDir) { Fail "shortcut at $shortcut has no target" }
Write-Host "shortcut: ok -> $installDir"

if ($ReportDir -ne '') {
    New-Item -ItemType Directory -Force -Path $ReportDir | Out-Null
    Set-Content -Path (Join-Path $ReportDir 'freshwin-step-result.txt') -Value $installDir
}

# --- 3. payload -------------------------------------------------------------
$exePath = Join-Path $installDir "$ShortcutName.exe"
if (-not (Test-Path $exePath)) { Fail "$ShortcutName.exe missing from $installDir" }
if ($PayloadFile -and -not (Test-Path (Join-Path $installDir $PayloadFile))) {
    Fail "expected payload $PayloadFile not installed alongside the app"
}
Write-Host 'payload: ok'

# --- 4. launch --------------------------------------------------------------
$app = Start-Process -FilePath $exePath -WorkingDirectory $installDir -PassThru
$windowSeen = $false
$deadline = (Get-Date).AddSeconds(30)
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Milliseconds 500
    $app.Refresh()
    if ($app.HasExited) { Fail "installed app exited before showing its window (exit $($app.ExitCode))" }
    if ($app.MainWindowTitle -like "*$WindowTitle*") { $windowSeen = $true; break }
}
if (-not $windowSeen) { Fail "installed app did not show a '$WindowTitle' window within 30 s" }
Write-Host "launch: ok (window '$($app.MainWindowTitle)')"
try { $app | Stop-Process -Force -ErrorAction SilentlyContinue } catch { }
Start-Sleep -Seconds 2

# --- 5. uninstall -----------------------------------------------------------
$p = Start-Process -FilePath msiexec.exe -ArgumentList @('/x', "`"$msiPath`"", '/qn') -Wait -PassThru
if ($p.ExitCode -ne 0) { Fail "msiexec uninstall failed with exit code $($p.ExitCode)" }
if (Test-Path $exePath) { Fail "$ShortcutName.exe survived uninstall" }
Write-Host 'uninstall: ok'

Remove-Item -Recurse -Force $vmFolder -ErrorAction SilentlyContinue
Write-Host 'installer checks PASSED' -ForegroundColor Green
exit 0
