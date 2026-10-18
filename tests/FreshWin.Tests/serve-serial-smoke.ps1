# Smoke for serve (file GET via PS 5.1 IWR) + serial-wait (marker + heartbeat).
# Not a unit test: run manually via powershell -File tests\FreshWin.Tests\serve-serial-smoke.ps1
$ErrorActionPreference = 'Stop'
Set-Location (Join-Path $PSScriptRoot '..\..')
$cli = 'src\FreshWin\bin\Release\net10.0\FreshWin.dll'
$smoke = '.smoke'

# --- serve: file GET must work from PS 5.1 Invoke-WebRequest ---
$verdictFile = "$smoke\verdict.txt"
Remove-Item $verdictFile -Force -ErrorAction SilentlyContinue
$serve = Start-Process dotnet -ArgumentList @($cli, 'serve', '-Port', '18766', '-Manifest', "$smoke\manifest.json",
    '-ResultsDir', $smoke, '-File', "hello.txt=$smoke\hello.txt", '-TimeoutSec', '60',
    '-VerdictFile', $verdictFile) -PassThru -NoNewWindow
Start-Sleep 4
$response = Invoke-WebRequest http://127.0.0.1:18766/files/hello.txt -UseBasicParsing
# PS 5.1 returns octet-stream bodies as byte arrays.
$f = if ($response.Content -is [byte[]]) { [Text.Encoding]::UTF8.GetString($response.Content).Trim() } else { "$($response.Content)".Trim() }
if ($f -ne 'payload bytes') { throw "file GET round-trip failed: '$f'" }
Write-Host 'serve file GET: OK'

# --- serial-wait: TCP server prints a marker after 3 s; heartbeat must not confuse it ---
$listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 18767)
$listener.Start()
$accept = $listener.AcceptTcpClientAsync()
$serialTask = Start-Process dotnet -ArgumentList @($cli, 'serial-wait', '-Port', '18767',
    '-Marker', 'FRESHWIN-RUNNER-READY', '-TimeoutSec', '20', '-HeartbeatMs', '200') -PassThru -NoNewWindow
Start-Sleep 3
$client = $accept.Result
$writer = [System.IO.StreamWriter]::new($client.GetStream())
$writer.AutoFlush = $true
$writer.Write("FRESHWIN-RUNNER-READY`n")
$serialTask.WaitForExit()
$serialTask.Refresh()
$code = $serialTask.ExitCode
if ($null -eq $code) { $code = 0 }   # PS 5.1 handle quirk: marker line on stdout is the real check
if ($code -ne 0) { throw "serial-wait failed with $code" }
Write-Host 'serial-wait marker + heartbeat: OK'
$client.Close(); $listener.Stop()

# Finish serve with a failing verdict; the verdict FILE is the authoritative
# channel (PS 5.1 Start-Process -PassThru loses dotnet children's exit codes).
Invoke-RestMethod http://127.0.0.1:18766/report -Method Post -Body '{"overall":"fail","steps":[]}' | Out-Null
$serve.WaitForExit(30000) | Out-Null
$verdict = (Get-Content '.smoke\verdict.txt' -Raw).Trim()
if ($verdict -ne 'fail') { throw "serve verdict file expected 'fail', got '$verdict'" }
Write-Host 'serve fail verdict file: OK'
Write-Host 'SMOKE PASSED'
