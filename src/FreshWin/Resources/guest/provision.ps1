# FreshWin (c) Ze'ev Russak — Apache License 2.0 (see LICENSE + NOTICE).
# Derived from ZuTM's guest provisioning script.
#
# Runs once at first logon (launched from the virtual-FAT unattend stick by
# the autounattend FirstLogonCommands entry). Configures the test image:
# persistent autologon, UAC off, the frozen runner in Startup, power settings
# that keep the interactive session alive, and (ARM64) virtio NIC/GPU drivers
# from the virtio-win CD. Reboots at the end so the UAC change takes effect;
# after that reboot the Startup runner marks itself ready and powers off —
# that powered-off state is what gets frozen.
#
# PowerShell 5.1, in-box assemblies only.

$ErrorActionPreference = 'Continue'
$ProgressPreference = 'SilentlyContinue'
$stick = Split-Path -Parent $MyInvocation.MyCommand.Path
New-Item -ItemType Directory -Force -Path C:\FreshWin | Out-Null
Start-Transcript -Path C:\FreshWin\provision.log -Force

function Write-Serial([string]$text) {
    try {
        $port = New-Object System.IO.Ports.SerialPort('COM1', 115200,
            [System.IO.Ports.Parity]::None, 8, [System.IO.Ports.StopBits]::One)
        $port.Open()
        $port.Write($text + "`r`n")
        $port.Close()
    } catch {
        # Serial is a convenience channel on ARM64 (PL011) — never fatal.
    }
}

Write-Serial 'FRESHWIN-STAGE:provision-start'
Write-Host 'provisioning FreshWin test image'

# 1) Persistent autologon + UAC off (test image: per-machine MSI installs from
#    scripts need a full administrator token).
$winlogon = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon'
Set-ItemProperty $winlogon AutoAdminLogon '1'
Set-ItemProperty $winlogon DefaultUserName 'freshwin'
Set-ItemProperty $winlogon DefaultDomainName '.'
Set-ItemProperty $winlogon DefaultPassword 'freshwin'
Set-ItemProperty $winlogon ForceAutoLogon '1'

$policies = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System'
Set-ItemProperty $policies EnableLUA 0
Set-ItemProperty $policies ConsentPromptBehaviorAdmin 0
Set-ItemProperty $policies PromptOnSecureDesktop 0

# 2) Install the frozen runner (+ its gateway port config) and start it at
#    every logon via the all-users Startup folder.
Copy-Item (Join-Path $stick 'freshwin-runner.ps1') C:\FreshWin\runner.ps1 -Force
if (Test-Path (Join-Path $stick 'freshwin-server.txt')) {
    Copy-Item (Join-Path $stick 'freshwin-server.txt') C:\FreshWin\server.txt -Force
}

$shell = New-Object -ComObject WScript.Shell
$startup = [Environment]::GetFolderPath('CommonStartup')
$shortcut = $shell.CreateShortcut((Join-Path $startup 'FreshWinRunner.lnk'))
$shortcut.TargetPath = 'powershell.exe'
$shortcut.Arguments = '-NoProfile -ExecutionPolicy Bypass -File C:\FreshWin\runner.ps1'
$shortcut.WorkingDirectory = 'C:\FreshWin'
$shortcut.WindowStyle = 7
$shortcut.Save()
[void][Runtime.InteropServices.Marshal]::ReleaseComObject($shell)

# 3) Keep the interactive session alive: no sleep, no display blanking, no
#    hibernation — UI automation needs a live desktop.
powercfg /change standby-timeout-ac 0
powercfg /change monitor-timeout-ac 0
powercfg /change hibernate-timeout-ac 0
powercfg /h off

# 4) ARM64: the guest NIC (virtio-net) and GPU (virtio-gpu) have no in-box
#    driver — stage them from the attached virtio-win CD and rescan.
if ($env:PROCESSOR_ARCHITECTURE -eq 'ARM64') {
    Write-Serial 'FRESHWIN-STAGE:provision-virtio'
    Get-Volume |
        Where-Object { $_.DriveLetter -and $_.FileSystemLabel -like '*virtio*' } |
        ForEach-Object {
            Write-Host "staging virtio drivers from $($_.DriveLetter):"
            pnputil /add-driver "$($_.DriveLetter):\*.inf" /subdirs /install 2>&1 | Out-Null
        }
    pnputil /scan-devices | Out-Null
}

Write-Serial 'FRESHWIN-STAGE:provision-done'
Write-Host 'provisioning complete — rebooting for the verification boot'
Stop-Transcript
shutdown /r /t 5
