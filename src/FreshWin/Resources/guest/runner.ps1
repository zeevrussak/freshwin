# FreshWin (c) Ze'ev Russak — Apache License 2.0 (see LICENSE + NOTICE).
# Derived from ZuTM's guest runner script.
#
# Frozen into the base image; runs at every interactive logon (Startup
# shortcut). Polls the host harness over the QEMU slirp gateway
# (http://10.0.2.2:<port>): when a manifest is served, downloads the payload
# (files under test + step scripts), executes the steps, uploads results,
# posts the final verdict, and shuts the guest down. With no harness (the
# freeze-time verification boot, or a manual boot) it powers off after a
# short wait.
#
# PowerShell 5.1, in-box assemblies only.

$ErrorActionPreference = 'Continue'
$ProgressPreference = 'SilentlyContinue'
New-Item -ItemType Directory -Force -Path C:\FreshWin | Out-Null
Start-Transcript -Path C:\FreshWin\runner.log -Append

function Write-Serial([string]$text) {
    try {
        $port = New-Object System.IO.Ports.SerialPort('COM1', 115200,
            [System.IO.Ports.Parity]::None, 8, [System.IO.Ports.StopBits]::One)
        $port.Open()
        $port.Write($text + "`r`n")
        $port.Close()
    } catch { }
}

function Post-Report([hashtable]$body) {
    try {
        Invoke-RestMethod -Uri "$gateway/report" -Method Post `
            -Body (ConvertTo-Json $body -Depth 5 -Compress) -TimeoutSec 15 | Out-Null
    } catch { }
}

Write-Serial 'FRESHWIN-RUNNER-READY'
Write-Host 'FreshWin guest runner ready'

$port = 8765
if (Test-Path C:\FreshWin\server.txt) {
    $configured = (Get-Content C:\FreshWin\server.txt -First 1).Trim()
    if ($configured -match '^\d+$') { $port = [int]$configured }
}
$gateway = "http://10.0.2.2:$port"

# Wait up to 2 minutes for a harness; without one this is the freeze-time
# verification boot — prove autologon worked, then power off.
$manifest = $null
$deadline = (Get-Date).AddSeconds(120)
while (-not $manifest -and (Get-Date) -lt $deadline) {
    try { $manifest = Invoke-RestMethod "$gateway/manifest.json" -TimeoutSec 5 } catch { Start-Sleep -Seconds 5 }
}
if (-not $manifest) {
    Write-Serial 'FRESHWIN-RUNNER:NO-HARNESS'
    Write-Host 'no harness on the gateway — shutting down'
    Stop-Transcript
    shutdown /s /t 10
    exit 0
}
Write-Host "manifest served: $(@($manifest.Payload).Count) files, $(@($manifest.Steps).Count) steps"

$payloadDir = 'C:\FreshWin\payload'
$resultsDir = 'C:\FreshWin\results'
New-Item -ItemType Directory -Force -Path $payloadDir, $resultsDir | Out-Null

foreach ($name in @($manifest.Payload)) {
    Write-Host "fetching $name"
    Invoke-WebRequest -Uri "$gateway/files/$name" -OutFile (Join-Path $payloadDir $name) -TimeoutSec 3600
    if (-not (Test-Path (Join-Path $payloadDir $name))) {
        Write-Serial 'FRESHWIN-RUNNER:PAYLOAD-FAILED'
        Post-Report @{ overall = 'fail'; steps = @() }
        Stop-Transcript
        shutdown /s /t 10
        exit 1
    }
}

$stepReports = @()
$failed = $false
foreach ($step in $manifest.Steps) {
    $file = Join-Path $payloadDir $step.File
    $arguments = [string]$step.Arguments
    # {stepResult} substitutes the previous step's freshwin-step-result.txt
    # (e.g. install-checks drops the resolved install directory there).
    if ($script:stepContext) { $arguments = $arguments.Replace('{stepResult}', $script:stepContext) }

    Write-Serial ("FRESHWIN-STEP:" + $step.Name + ":start")
    Write-Host "step $($step.Name): powershell -File $file $arguments"
    $watch = [System.Diagnostics.Stopwatch]::StartNew()
    $process = Start-Process -FilePath 'powershell.exe' `
        -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $file, $arguments) `
        -WorkingDirectory $payloadDir -WindowStyle Hidden -PassThru
    $timeoutMs = [int]$step.TimeoutSeconds * 1000
    if ($process.WaitForExit($timeoutMs)) {
        $exitCode = $process.ExitCode
    } else {
        taskkill /PID $process.Id /T /F 2>&1 | Out-Null
        $process.Refresh()
        $exitCode = 124
    }
    $watch.Stop()

    # A step may drop freshwin-step-result.txt for later steps to consume.
    $marker = Join-Path $resultsDir 'freshwin-step-result.txt'
    if (Test-Path $marker) {
        $script:stepContext = (Get-Content $marker -First 1).Trim()
    }

    $report = @{ step = $step.Name; exitCode = $exitCode; durationMs = $watch.ElapsedMilliseconds }
    $stepReports += $report
    Post-Report $report
    Write-Serial ("FRESHWIN-STEP:" + $step.Name + ":" + $exitCode)
    if ($exitCode -ne 0 -and -not $step.ContinueOnFailure) {
        $failed = $true
        break
    }
}

# Collect evidence: scripts' own outputs, transcripts, automatic MSI logs,
# any app crash logs under %LOCALAPPDATA%, then upload as one archive.
$collect = @(
    'C:\FreshWin\provision.log',
    'C:\FreshWin\runner.log'
)
Get-ChildItem $env:TEMP -Filter 'MSI*.log' -ErrorAction SilentlyContinue |
    ForEach-Object { $collect += $_.FullName }
Get-ChildItem "$env:LOCALAPPDATA\*\CrashDumps" -ErrorAction SilentlyContinue |
    ForEach-Object { $collect += $_.FullName }
Get-ChildItem $resultsDir -File -ErrorAction SilentlyContinue |
    ForEach-Object { $collect += $_.FullName }
$collect | Where-Object { Test-Path $_ } | Sort-Object -Unique |
    ForEach-Object {
        Copy-Item $_ (Join-Path $resultsDir ([IO.Path]::GetFileName($_))) -Force -ErrorAction SilentlyContinue
    }

$zip = 'C:\FreshWin\results.zip'
try {
    Compress-Archive -Path (Join-Path $resultsDir '*') -DestinationPath $zip -Force
    Invoke-WebRequest -Uri "$gateway/upload?name=results.zip" -Method Post -InFile $zip -TimeoutSec 900
    Write-Host 'results uploaded'
} catch {
    Write-Host "upload failed: $($_.Exception.Message)"
}

$overall = 'pass'
if ($failed) { $overall = 'fail' }
Post-Report @{ overall = $overall; steps = $stepReports }

Write-Serial 'FRESHWIN-RUNNER:SHUTDOWN'
Write-Host "run $overall — shutting down"
Stop-Transcript
shutdown /s /t 10
