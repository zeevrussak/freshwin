# FreshWin (c) Ze'ev Russak — Apache License 2.0 (see LICENSE + NOTICE).
#
# Thin wrapper: single-use guest test cycle on the frozen base image.
# See freshwin.psm1 Invoke-FreshWinTest for the full story.
#
# Usage:
#   powershell -File scripts\run-tests.ps1 -Msi MyApp-x64.msi
#   powershell -File scripts\run-tests.ps1 -Msi MyApp.msi -StepScript ui-drive.ps1 -StepArguments '-AppExe "{stepResult}\MyApp.exe"'
#
# Results land in artifacts\guest-e2e\<arch>\<timestamp>\ (transcripts, MSI
# logs, crash dumps, results.zip, serve/qemu logs). Exit 0 = pass.

[CmdletBinding()]
param(
    [ValidateSet('x64', 'arm64')][string]$Arch = '',
    [Parameter(Mandatory = $true)][string]$Msi,
    [string]$StepScript = '', [string]$StepArguments = '', [int]$StepTimeoutSec = 900,
    [string]$ResultsRoot = '',
    [int]$TimeoutMin = 0,
    [switch]$KeepOverlay, [switch]$Vnc
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
Import-Module (Join-Path $repoRoot 'freshwin.psm1') -Force

$parameters = @{ Msi = $Msi }
if ($Arch) { $parameters.Arch = $Arch }
if ($StepScript) { $parameters.StepScript = $StepScript }
if ($StepArguments) { $parameters.StepArguments = $StepArguments }
$parameters.StepTimeoutSec = $StepTimeoutSec
if ($ResultsRoot) { $parameters.ResultsRoot = $ResultsRoot }
if ($TimeoutMin -gt 0) { $parameters.TimeoutMin = $TimeoutMin }
if ($KeepOverlay) { $parameters.KeepOverlay = $true }
if ($Vnc) { $parameters.Vnc = $true }

exit (Invoke-FreshWinTest @parameters)
