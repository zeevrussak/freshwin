# FreshWin (c) Ze'ev Russak — Apache License 2.0 (see LICENSE + NOTICE).
#
# Thin wrapper: builds the frozen Windows base image for -Arch.
# See freshwin.psm1 Build-FreshWinBase for the full story.
#
# Usage:
#   powershell -File scripts\build-base.ps1                        # host arch
#   powershell -File scripts\build-base.ps1 -Arch arm64            # native on ARM64 hosts, TCG elsewhere
#   powershell -File scripts\build-base.ps1 -IsoPath D:\iso\win11.iso
#   powershell -File scripts\build-base.ps1 -Vnc                   # watchable debug boot

[CmdletBinding()]
param(
    [ValidateSet('x64', 'arm64')][string]$Arch = '',
    [string]$IsoPath = '',
    [string]$ImageName = 'Windows 11 Pro',
    [int]$MemoryMib = 4096, [int]$CpuCount = 4, [int]$DiskGiB = 40,
    [int]$TimeoutMin = 0,
    [switch]$Vnc
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
Import-Module (Join-Path $repoRoot 'freshwin.psm1') -Force

if (-not $env:FRESHWIN_QEMU_ROOT -and -not (Test-Path (Join-Path $repoRoot 'runtimes\qemu'))) {
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $repoRoot 'scripts\fetch-qemu.ps1')
    if ($LASTEXITCODE -ne 0) { throw 'fetch-qemu.ps1 failed' }
}

$parameters = @{ }
if ($Arch) { $parameters.Arch = $Arch }
if ($IsoPath) { $parameters.IsoPath = $IsoPath }
if ($ImageName) { $parameters.ImageName = $ImageName }
$parameters.MemoryMib = $MemoryMib
$parameters.CpuCount = $CpuCount
$parameters.DiskGiB = $DiskGiB
if ($TimeoutMin -gt 0) { $parameters.TimeoutMin = $TimeoutMin }
if ($Vnc) { $parameters.Vnc = $true }

Build-FreshWinBase @parameters
exit 0
