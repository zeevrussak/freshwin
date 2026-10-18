# FreshWin (c) Ze'ev Russak — Apache License 2.0 (see LICENSE + NOTICE).
# Derived from ZuTM's TestEnv tooling (tools/ZuTM.TestEnv + scripts/testenv).
#
# PowerShell orchestration for disposable Windows test VMs on QEMU/Windows:
# every process spawn lives here; the FreshWin C# CLI does pure computation
# (FAT32 images, answer files, pins, serial/QMP/HTTP sockets).
#
#   Build-FreshWinMedia  -Arch x64                # bootable FAT32 install disk (cached)
#   Build-FreshWinBase   -Arch x64                # unattended install → frozen base image
#   Invoke-FreshWinTest  -Msi app.msi [-StepScript ui.ps1]
#                                                  # single-use cycle on the frozen base
#
# Layout under work\: win11-iso\ (pins), firmware\, win11-<arch>-install.img,
# win11-<arch>-base.qcow2 + -vars.fd + .ready.json, per-run overlays.
# Results under artifacts\guest-e2e\<arch>\<timestamp>\.

Set-StrictMode -Version 3.0

$script:FreshWinRoot = Split-Path -Parent $PSCommandPath
$script:WorkDir = Join-Path $script:FreshWinRoot 'work'

# --- CLI resolution -------------------------------------------------------------

function Get-FreshWinCli {
    # Prefer a published single-file exe; build on demand; fall back to dotnet run.
    $publish = Join-Path $script:FreshWinRoot 'artifacts\cli\FreshWin.dll'
    $project = Join-Path $script:FreshWinRoot 'src\FreshWin\FreshWin.csproj'
    if (-not (Test-Path $publish)) {
        Write-Host 'building FreshWin CLI …' -ForegroundColor Cyan
        dotnet build $project -c Release --nologo -v quiet | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'FreshWin CLI build failed.' }
        $publish = Join-Path $script:FreshWinRoot 'src\FreshWin\bin\Release\net10.0\FreshWin.dll'
    }
    return @{ Exe = 'dotnet'; Arguments = @($publish) }
}

function Invoke-FreshWinCli {
    param([string]$Command, [string[]]$CliArguments, [switch]$Capture)
    $cli = Get-FreshWinCli
    $all = $cli.Arguments + $Command + $CliArguments
    if ($Capture) {
        return (& $cli.Exe @all)
    }
    & $cli.Exe @all
    if ($LASTEXITCODE -ne 0) { throw "freshwin $Command failed with $LASTEXITCODE" }
}

function Get-FreshWinProfile {
    param([ValidateSet('x64', 'arm64')][string]$Arch)
    $json = Invoke-FreshWinCli 'profile' @('-Arch', $Arch) -Capture | Out-String
    return $json | ConvertFrom-Json
}

# --- host helpers ----------------------------------------------------------------

function Find-FreshWinSevenZip {
    $sevenZip = @(
        "$env:ProgramFiles\7-Zip\7z.exe",
        "${env:ProgramFiles(x86)}\7-Zip\7z.exe"
    ) | Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $sevenZip) { throw '7-Zip not found (required for ISO/RPM/zip handling).' }
    return $sevenZip
}

function Get-FreshWinQemu {
    $candidates = @()
    if ($env:FRESHWIN_QEMU_ROOT) { $candidates += $env:FRESHWIN_QEMU_ROOT }
    for ($dir = $script:FreshWinRoot; $dir; $dir = Split-Path -Parent $dir) {
        $candidates += (Join-Path $dir 'runtimes\qemu')
    }
    foreach ($candidate in $candidates) {
        if ((Test-Path (Join-Path $candidate 'qemu-system-x86_64.exe')) -or
            (Test-Path (Join-Path $candidate 'qemu-system-aarch64.exe'))) {
            return $candidate
        }
    }
    throw 'QEMU runtime not found — run scripts/fetch-qemu.ps1 or set FRESHWIN_QEMU_ROOT.'
}

function Get-FreeTcpPort {
    $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
    $listener.Start()
    $port = ($listener.LocalEndpoint).Port
    $listener.Stop()
    return $port
}

function Invoke-FreshWinQemuImg {
    param([string]$Arguments)
    $qemu = Get-FreshWinQemu
    $qemuImg = Join-Path $qemu 'qemu-img.exe'
    & $qemuImg $Arguments 2>&1 | Write-Verbose
    if ($LASTEXITCODE -ne 0) { throw "qemu-img $Arguments failed with $LASTEXITCODE" }
}

function Start-FreshWinQemu {
    param([string[]]$QemuArguments, [string]$LogPath)
    $qemu = Get-FreshWinQemu
    $exe = Join-Path $qemu 'qemu-system-x86_64.exe'
    if ($QemuArguments -contains '-machine' -and $QemuArguments[($QemuArguments.IndexOf('-machine') + 1)] -eq 'virt') {
        $exe = Join-Path $qemu 'qemu-system-aarch64.exe'
    }
    $p = Start-Process -FilePath $exe -ArgumentList $QemuArguments `
        -WindowStyle Hidden -PassThru `
        -RedirectStandardError $LogPath -RedirectStandardOutput ($LogPath + '.out')
    return $p
}

function Wait-FreshWinMarker {
    param(
        [int]$QemuPid, [int]$SerialPort, [string]$Marker, [int]$TimeoutSec,
        [int]$HeartbeatMs = 300, [string]$ScreenshotPath = ''
    )
    $cli = Get-FreshWinCli
    # serial-wait exits 0 (found) / 3 (timeout) / 2 (no serial).
    Start-Process -FilePath $cli.Exe -ArgumentList ($cli.Arguments + @(
        'serial-wait', '-Port', $SerialPort, '-Marker', $Marker,
        '-TimeoutSec', $TimeoutSec, '-HeartbeatMs', $HeartbeatMs)) `
        -NoNewWindow -Wait -PassThru | ForEach-Object {
        if ($_.ExitCode -ne 0) {
            throw "timed out waiting for marker '$Marker' (serial-wait exit $($_.ExitCode)). QEMU log tail and screenshot (if any) are in the results."
        }
    }
}

# --- media / firmware staging ------------------------------------------------------

function Complete-FreshWinFirmware {
    param([ValidateSet('x64', 'arm64')][string]$Arch)
    $stage = Join-Path $script:WorkDir 'firmware'
    New-Item -ItemType Directory -Force -Path $stage | Out-Null
    $pin = if ($Arch -eq 'arm64') { 'edk2-aarch64' } else { 'edk2-ovmf' }
    $codeName = if ($Arch -eq 'arm64') { 'AAVMF_CODE.fd' } else { 'OVMF_CODE.fd' }
    $varsName = if ($Arch -eq 'arm64') { 'AAVMF_VARS.fd' } else { 'OVMF_VARS.fd' }
    $code = Join-Path $stage $codeName
    $vars = Join-Path $stage $varsName
    if ((Test-Path $code) -and (Test-Path $vars)) { return @{ Code = $code; Vars = $vars } }

    Invoke-FreshWinCli 'stage' @('-Pin', $pin, '-Dir', $stage) | Out-Null
    $rpm = Get-ChildItem $stage -Filter '*.rpm' | Select-Object -First 1
    $cpio = Join-Path $stage ($rpm.BaseName + '.cpio')

    # RPM → cpio → files. 7-Zip reports errors for the RPM's symlinks
    # (unsupported on Windows) after extracting the real files — tolerate.
    $sevenZip = Find-FreshWinSevenZip
    & $sevenZip x -y "-o$stage" $rpm.FullName | Out-Null
    if (-not (Test-Path $cpio)) {
        $cpio = (Get-ChildItem $stage -Filter '*.cpio' | Select-Object -First 1).FullName
    }
    $fwDir = Join-Path $stage 'fw'
    New-Item -ItemType Directory -Force -Path $fwDir | Out-Null
    & $sevenZip x -y "-o$fwDir" $cpio 2>&1 | Out-Null

    # usr/share/OVMF/* are 0-byte symlink aliases of usr/share/edk2/* — take
    # the largest match.
    $sourceRoot = Join-Path $fwDir 'usr\share'
    $codeSource = Get-ChildItem $sourceRoot -Recurse -Filter $codeName |
        Sort-Object Length -Descending | Select-Object -First 1
    $varsSource = Get-ChildItem $sourceRoot -Recurse -Filter $varsName |
        Sort-Object Length -Descending | Select-Object -First 1
    if (-not $codeSource -or $codeSource.Length -eq 0) { throw "$codeName not found in the firmware RPM payload." }
    Copy-Item $codeSource.FullName $code -Force
    Copy-Item $varsSource.FullName $vars -Force
    return @{ Code = $code; Vars = $vars }
}

function Build-FreshWinMedia {
    <#
        .SYNOPSIS
        Builds the bootable FAT32 USB-disk install media (cached by ISO SHA).
        The installer never boots from the CD: on QEMU/Windows + EDK2 the
        El Torito boot wedges (cdboot's unattendable "press any key" gate),
        so wimboot boots boot.wim straight off this disk instead.
    #>
    param(
        [ValidateSet('x64', 'arm64')][string]$Arch = '',
        [string]$IsoPath = '',
        [switch]$Force
    )
    if (-not $Arch) { $Arch = if ($env:PROCESSOR_ARCHITECTURE -eq 'ARM64') { 'arm64' } else { 'x64' } }
    $profile = Get-FreshWinProfile $Arch
    $isoDir = Join-Path $script:WorkDir 'win11-iso'
    New-Item -ItemType Directory -Force -Path $isoDir | Out-Null

    if ($IsoPath) {
        $iso = (Resolve-Path $IsoPath).Path
        $isoSource = 'user-supplied'
    } else {
        $pin = if ($Arch -eq 'arm64') { 'tiny11-arm64' } else { 'tiny11-x64' }
        $iso = [string](Invoke-FreshWinCli 'stage' @('-Pin', $pin, '-Dir', $isoDir) -Capture | Select-Object -Last 1)
        $isoSource = "pin:$pin"
    }

    $image = Join-Path $script:WorkDir ("win11-{0}-install.img" -f $Arch)
    $marker = "$image.ready"
    $isoHash = (Get-FileHash $iso -Algorithm SHA256).Hash.ToLowerInvariant()
    $cacheKey = "$isoHash;v4"
    if ((Test-Path $marker) -and ((Get-Content $marker -Raw).Trim() -eq $cacheKey) -and (Test-Path $image) -and -not $Force) {
        Write-Host "install media cached: $image"
        return @{ Image = $image; Iso = $iso; IsoSource = $isoSource; IsoSha256 = $isoHash }
    }

    # Pins that ride the media build.
    $wimlibZip = [string](Invoke-FreshWinCli 'stage' @('-Pin', 'wimlib', '-Dir', $isoDir) -Capture | Select-Object -Last 1)
    $wimlib = Join-Path $isoDir 'wimlib-imagex.exe'   # the archive extracts flat
    if (-not (Test-Path $wimlib)) {
        & (Find-FreshWinSevenZip) x -y "-o$isoDir" $wimlibZip | Out-Null
    }
    $wimbootPin = if ($Arch -eq 'arm64') { 'wimboot-arm64' } else { 'wimboot-x64' }
    [string](Invoke-FreshWinCli 'stage' @('-Pin', $wimbootPin, '-Dir', $isoDir) -Capture) | Out-Null
    $wimboot = Join-Path $isoDir (Get-ChildItem $isoDir -Filter 'wimboot*.efi' | Select-Object -First 1).Name
    $sevenZip = Find-FreshWinSevenZip

    # 1. Extract the ISO tree.
    $staging = "$image.staging"
    if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
    Write-Host "extracting $(Split-Path -Leaf $iso) → staging …"
    New-Item -ItemType Directory -Force -Path $staging | Out-Null
    & $sevenZip x -y "-o$staging" $iso | Out-Null

    # 2. NTDEV's convenience autounattend would fight ours; ours is baked into
    #    the media root (setup only scans removable media for answer files).
    Remove-Item (Join-Path $staging 'autounattend.xml') -Force -ErrorAction SilentlyContinue
    $unattend = Join-Path $staging 'freshwin-autounattend.xml'
    Invoke-FreshWinCli 'autounattend' @('-Arch', $Arch, '-Out', $unattend) | Out-Null
    Copy-Item $unattend (Join-Path $staging 'autounattend.xml') -Force

    # 3. wimboot's EFI scan reads ONLY the volume root: boot.wim / boot.sdi /
    #    BCD must live there (it pulls bootmgfw.efi out of the WIM itself).
    Copy-Item (Join-Path $staging 'sources\boot.wim') (Join-Path $staging 'boot.wim') -Force
    Copy-Item (Join-Path $staging 'boot\boot.sdi') (Join-Path $staging 'boot.sdi') -Force
    Copy-Item (Join-Path $staging 'boot\bcd') (Join-Path $staging 'BCD') -Force

    # 4. wimboot replaces cdboot as the bootloader (the original is kept).
    $bootDir = Join-Path $staging 'EFI\BOOT'
    New-Item -ItemType Directory -Force -Path $bootDir | Out-Null
    $bootFile = if ($Arch -eq 'arm64') { 'BOOTAA64.EFI' } else { 'BOOTX64.EFI' }
    $bootTarget = Join-Path $bootDir $bootFile
    if (Test-Path $bootTarget) { Copy-Item $bootTarget (Join-Path $bootDir 'CDBOOT.EFI') -Force }
    Copy-Item (Join-Path $isoDir $wimboot) $bootTarget -Force
    Write-Host "bootloader: wimboot replaces cdboot at EFI\BOOT\$bootFile"

    # 5. Split install.wim under FAT32's 4 GiB per-file ceiling. Solid LZMS
    #    WIMs (tiny11) cannot be split as-is — export non-solid (LZX) first.
    $installWim = Join-Path $staging 'sources\install.wim'
    if ((Test-Path $installWim) -and ((Get-Item $installWim).Length -gt 4294967295)) {
        $swm = Join-Path $staging 'sources\install.swm'
        Write-Host 'splitting install.wim for FAT32 (install.swm parts) …'
        & $wimlib split $installWim $swm 3800 2>&1 | Write-Verbose
        if ($LASTEXITCODE -ne 0) {
            Write-Host 'wim is solid-compressed — exporting non-solid …'
            $nonSolid = "$installWim.nonsolid"
            & $wimlib export $installWim 1 $nonSolid --compress=LZX 2>&1 | Write-Host
            if ($LASTEXITCODE -ne 0) { throw 'wimlib export failed.' }
            Remove-Item $installWim -Force
            Move-Item $nonSolid $installWim
            & $wimlib split $installWim $swm 3800 2>&1 | Write-Verbose
            if ($LASTEXITCODE -ne 0) { throw 'wimsplit failed after de-solid export.' }
        }
        Remove-Item $installWim -Force
    }

    # 6. FAT32 image.
    Write-Host "writing FAT32 image $image …"
    Invoke-FreshWinCli 'fat32' @('-Source', $staging, '-Out', $image)
    Remove-Item $staging -Recurse -Force
    Set-Content -Path $marker -Value $cacheKey
    return @{ Image = $image; Iso = $iso; IsoSource = $isoSource; IsoSha256 = $isoHash }
}

# --- QEMU argument construction ------------------------------------------------------

function New-FreshWinQemuArguments {
    param(
        [Parameter(Mandatory)] $Profile,       # from Get-FreshWinProfile
        [ValidateSet('install', 'run')][string]$Phase,
        [string]$DiskPath, [string]$EfiCode, [string]$EfiVars,
        [string]$InstallImage = '', [string]$UnattendStick = '',
        [string]$VirtioWinIso = '',
        [int]$MemoryMib = 4096, [int]$CpuCount = 4,
        [int]$SerialPort, [int]$QmpPort, [switch]$Vnc
    )
    $args = @(
        '-name', 'freshwin-guest',
        '-machine', $Profile.machineTarget,
        '-accel', $Profile.acceleration, '-accel', 'tcg',   # first available wins
        '-m', $MemoryMib, '-smp', $CpuCount,
        '-rtc', 'base=localtime',
        '-display', 'none',
        '-drive', "if=pflash,format=raw,readonly=on,file=$EfiCode",
        '-drive', "if=pflash,format=raw,file=$EfiVars",
        '-device', 'qemu-xhci'
    )
    if ($Profile.systemArchitecture -eq 'aarch64') {
        $args += @('-cpu', 'max')
    }
    if ($Vnc) { $args += @('-vnc', '127.0.0.1:0') }

    $bootIndex = 0
    if ($InstallImage) {
        $args += @(
            '-drive', "id=fw-media,if=none,format=raw,file=$InstallImage,readonly=on",
            '-device', "usb-storage,drive=fw-media,bootindex=$bootIndex,removable=on"
        )
        $bootIndex++
    }
    if ($VirtioWinIso) {
        $args += @(
            '-drive', "id=fw-virtio,if=none,media=cdrom,readonly=on,file=$VirtioWinIso",
            '-device', 'usb-storage,drive=fw-virtio,removable=on'
        )
    }
    if ($UnattendStick) {
        $args += @(
            '-drive', "id=fw-stick,file=fat:ro:$UnattendStick,format=raw,if=none,readonly=on",
            '-device', 'usb-storage,drive=fw-stick,removable=on'
        )
    }
    if ($DiskPath) {
        # QEMU 11 makes the nvme namespace serial mandatory; the drive id doubles as one.
        $args += @(
            '-drive', "id=fw-disk,if=none,format=qcow2,file=$DiskPath",
            '-device', "nvme,drive=fw-disk,serial=fw-disk,bootindex=$bootIndex"
        )
    }

    # A display device must exist even though nobody looks at it: the autologon
    # session needs a monitor for the desktop/UI-automation stack.
    $args += @('-device', $Profile.displayHardware)
    $args += @(
        '-netdev', 'user,id=fw-net0',               # no restrict=on: the guest must reach 10.0.2.2
        '-device', "$($Profile.networkHardware),netdev=fw-net0",
        '-serial', "tcp:127.0.0.1:$SerialPort,server=on,wait=off",
        '-qmp', "tcp:127.0.0.1:$QmpPort,server=on,wait=off"
    )
    return $args
}

function New-FreshWinUnattendStick {
    param([ValidateSet('x64', 'arm64')][string]$Arch)
    $stick = Join-Path $script:WorkDir ("win11-{0}-unattend" -f $Arch)
    if (Test-Path $stick) { Remove-Item $stick -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $stick | Out-Null
    Invoke-FreshWinCli 'autounattend' @('-Arch', $Arch, '-Out', (Join-Path $stick 'autounattend.xml')) | Out-Null
    Invoke-FreshWinCli 'guest' @('-Name', 'provision', '-Out', (Join-Path $stick 'freshwin-provision.ps1')) | Out-Null
    Invoke-FreshWinCli 'guest' @('-Name', 'runner', '-Out', (Join-Path $stick 'freshwin-runner.ps1')) | Out-Null
    Set-Content -Path (Join-Path $stick 'freshwin-server.txt') -Value 8765
    # Fallback for the (never-observed) case where the firmware skips the
    # media boot: the UEFI shell auto-runs startup.nsh.
    Set-Content -Path (Join-Path $stick 'startup.nsh') -Value @(
        'echo FreshWin: scanning for a Windows bootloader (fallback when media boot is skipped)',
        'for %d in fs0 fs1 fs2 fs3 fs4 fs5 fs6 fs7',
        '  if exist %d:\EFI\BOOT\BOOTX64.EFI then',
        '    if not exist %d:\autounattend.xml then',
        '      %d:\EFI\BOOT\BOOTX64.EFI',
        '    endif',
        '  endif',
        'endfor',
        'echo FreshWin: no fallback bootloader found'
    )
    return $stick
}

# --- base image ----------------------------------------------------------------------

function Build-FreshWinBase {
    <#
        .SYNOPSIS
        Unattended Windows install → frozen base image (win11-<arch>-base.qcow2
        + UEFI vars + ready sidecar). Run once per machine/arch.
    #>
    param(
        [ValidateSet('x64', 'arm64')][string]$Arch = '',
        [string]$IsoPath = '', [string]$ImageName = 'Windows 11 Pro',
        [int]$MemoryMib = 4096, [int]$CpuCount = 4, [int]$DiskGiB = 40,
        [int]$TimeoutMin = 0, [switch]$Vnc
    )
    if (-not $Arch) { $Arch = if ($env:PROCESSOR_ARCHITECTURE -eq 'ARM64') { 'arm64' } else { 'x64' } }
    $profile = Get-FreshWinProfile $Arch
    if ($profile.acceleration -ne 'whpx') {
        Write-Host 'note: guest arch does not match the host — TCG emulation; the unattended install can take several hours.' -ForegroundColor Yellow
    }
    $installTimeoutMin = if ($TimeoutMin -gt 0) { $TimeoutMin } elseif ($profile.acceleration -eq 'whpx') { 90 } else { 600 }

    New-Item -ItemType Directory -Force -Path $script:WorkDir | Out-Null
    $media = Build-FreshWinMedia -Arch $Arch -IsoPath:$IsoPath
    $firmware = Complete-FreshWinFirmware -Arch $Arch
    $virtioWin = ''
    if ($profile.needsVirtioDrivers) {
        $isoDir = Join-Path $script:WorkDir 'win11-iso'
        $virtioWin = [string](Invoke-FreshWinCli 'stage' @('-Pin', 'virtio-win', '-Dir', $isoDir) -Capture | Select-Object -Last 1)
    }

    $baseDisk = Join-Path $script:WorkDir $profile.baseImageName
    $baseVars = Join-Path $script:WorkDir $profile.baseVarsName
    if ((Test-Path $baseDisk) -or (Test-Path $baseVars)) {
        Write-Host "base image already exists (delete it to rebuild): $baseDisk"
        return
    }
    Remove-Item "$baseDisk.ready.json" -Force -ErrorAction SilentlyContinue

    Write-Host "creating $DiskGiB GiB disk …"
    Invoke-FreshWinQemuImg "create -f qcow2 `"$baseDisk`" ${DiskGiB}G"
    Copy-Item $firmware.Vars $baseVars -Force

    $stick = New-FreshWinUnattendStick -Arch $Arch
    $serialPort = Get-FreeTcpPort
    $qmpPort = Get-FreeTcpPort
    $qemuArgs = New-FreshWinQemuArguments -Profile $profile -Phase install `
        -DiskPath $baseDisk -EfiCode $firmware.Code -EfiVars $baseVars `
        -InstallImage $media.Image -UnattendStick $stick -VirtioWinIso $virtioWin `
        -MemoryMib $MemoryMib -CpuCount $CpuCount -SerialPort $serialPort -QmpPort $qmpPort -Vnc:$Vnc
    $qemuLog = Join-Path $script:WorkDir 'qemu.log'
    Write-Host 'booting Windows setup (unattended; 15-60 min accelerated, hours under TCG) …'
    $qemu = Start-FreshWinQemu -QemuArguments $qemuArgs -LogPath $qemuLog

    try {
        Wait-FreshWinMarker -QemuPid $qemu.Id -SerialPort $serialPort `
            -Marker 'FRESHWIN-STAGE:provision-start' -TimeoutSec ($installTimeoutMin * 60)
        Write-Host 'first logon reached — provisioning …'
        Wait-FreshWinMarker -QemuPid $qemu.Id -SerialPort $serialPort `
            -Marker 'FRESHWIN-STAGE:provision-done' -TimeoutSec (30 * 60) -HeartbeatMs 0

        # Provision reboots to activate UAC-off autologon; the Startup runner
        # marks itself on the next boot, then powers off.
        Write-Host 'provisioned — waiting for the verification boot (autologon → runner) …'
        $verifyTimeout = if ($profile.acceleration -eq 'whpx') { 20 } else { 120 }
        Wait-FreshWinMarker -QemuPid $qemu.Id -SerialPort $serialPort `
            -Marker 'FRESHWIN-RUNNER-READY' -TimeoutSec ($verifyTimeout * 60) -HeartbeatMs 0
        if (-not $qemu.WaitForExit(15 * 60 * 1000)) { Stop-Process -Id $qemu.Id -Force }
    }
    catch {
        # Best-effort diagnostics before rethrowing.
        try {
            $cli = Get-FreshWinCli
            & $cli.Exe @($cli.Arguments + @('qmp', '-Port', $qmpPort, '-Command', 'screendump',
                '-JsonArgs', ('{"filename":"' + ($script:WorkDir -replace '\\', '\\') + '\\base-timeout.ppm"}'))) | Out-Null
        } catch { }
        if (-not $qemu.HasExited) { Stop-Process -Id $qemu.Id -Force }
        Get-Content $qemuLog -Tail 40 -ErrorAction SilentlyContinue | Write-Host
        throw
    }

    Write-Host 'verification boot complete — freezing the base image …'
    Invoke-FreshWinCli 'sidecar' @('write', '-BaseImage', $baseDisk, '-Arch', $Arch,
        '-IsoName', (Split-Path -Leaf $media.Iso), '-IsoSource', $media.IsoSource,
        '-IsoSha256', $media.IsoSha256, '-QemuRoot', (Get-FreshWinQemu),
        $(if ($profile.acceleration -eq 'whpx') { '-Accelerated' }))
    if ($virtioWin) {
        $sidecarFile = "$baseDisk.ready.json"
        $sidecar = Get-Content $sidecarFile -Raw | ConvertFrom-Json
        $sidecar | Add-Member -NotePropertyName virtioWinSha256 `
            -NotePropertyValue (Get-FileHash $virtioWin -Algorithm SHA256).Hash.ToLowerInvariant() -Force
        $sidecar | ConvertTo-Json -Depth 5 | Set-Content $sidecarFile
    }
    Set-ItemProperty $baseDisk -Name IsReadOnly $true
    Set-ItemProperty $baseVars -Name IsReadOnly $true
    Write-Host "base image ready: $baseDisk" -ForegroundColor Green
}

# --- single-use test cycle --------------------------------------------------------------

function Invoke-FreshWinTest {
    <#
        .SYNOPSIS
        Boots a disposable overlay on the frozen base, injects -Msi (plus an
        optional -StepScript) over the guest HTTP channel, executes the
        steps, harvests results, destroys the overlay. Returns 0 on pass.
    #>
    param(
        [ValidateSet('x64', 'arm64')][string]$Arch = '',
        [Parameter(Mandatory)][string]$Msi,
        [string]$StepScript = '', [string]$StepArguments = '', [int]$StepTimeoutSec = 900,
        [string]$ResultsRoot = '', [switch]$KeepOverlay, [switch]$Vnc, [int]$TimeoutMin = 0
    )
    if (-not $Arch) { $Arch = if ($env:PROCESSOR_ARCHITECTURE -eq 'ARM64') { 'arm64' } else { 'x64' } }
    $profile = Get-FreshWinProfile $Arch
    $baseDisk = Join-Path $script:WorkDir $profile.baseImageName
    $baseVars = Join-Path $script:WorkDir $profile.baseVarsName
    Invoke-FreshWinCli 'sidecar' @('check', '-BaseImage', $baseDisk, '-Arch', $Arch) | Out-Null
    $msiPath = (Resolve-Path $Msi).Path
    $stepsDir = Join-Path $script:FreshWinRoot 'scripts\steps'
    $installChecks = Join-Path $stepsDir 'install-checks.ps1'
    if (-not (Test-Path $installChecks)) { throw "built-in step script missing: $installChecks" }
    if ($StepScript -and -not (Test-Path $StepScript)) { throw "step script not found: $StepScript" }

    if (-not $ResultsRoot) { $ResultsRoot = Join-Path $script:FreshWinRoot 'artifacts\guest-e2e' }
    $resultsDir = Join-Path $ResultsRoot (Join-Path $Arch (Get-Date -Format 'yyyyMMdd-HHmmss'))
    New-Item -ItemType Directory -Force -Path $resultsDir | Out-Null

    # The run plan the frozen runner executes inside the guest.
    $payload = @((Split-Path -Leaf $msiPath), 'install-checks.ps1')
    $steps = @(
        @{ name = 'install-checks'; file = 'install-checks.ps1';
           arguments = "-Msi C:\FreshWin\payload\$(Split-Path -Leaf $msiPath) -ReportDir C:\FreshWin\results";
           timeoutSeconds = 1800; continueOnFailure = $false }
    )
    if ($StepScript) {
        $payload += (Split-Path -Leaf $StepScript)
        $steps += @{
            name = 'step'; file = (Split-Path -Leaf $StepScript)
            arguments = $StepArguments; timeoutSeconds = $StepTimeoutSec; continueOnFailure = $false
        }
        Copy-Item $StepScript $resultsDir -Force   # snapshot for the archive
    }
    $manifestPath = Join-Path $resultsDir 'manifest.json'
    @{ payload = $payload; steps = $steps } | ConvertTo-Json -Depth 5 | Set-Content $manifestPath

    # Single-use overlay + copy of the frozen NVRAM (boot entries point at
    # the installed Windows; a blank vars file would drop to the UEFI shell).
    $runId = ([guid]::NewGuid().ToString('N')).Substring(0, 12)
    $overlay = Join-Path $script:WorkDir ("win11-{0}-run-{1}.qcow2" -f $Arch, $runId)
    $runVars = Join-Path $script:WorkDir ("win11-{0}-run-{1}-vars.fd" -f $Arch, $runId)
    Invoke-FreshWinQemuImg "create -f qcow2 -b `"$baseDisk`" -F qcow2 `"$overlay`""
    Copy-Item $baseVars $runVars -Force

    # Guest HTTP channel: serve manifest + payload, collect uploads + report.
    $cli = Get-FreshWinCli
    $serveLog = Join-Path $resultsDir 'serve.log'
    $filePairs = @(
        "$([IO.Path]::GetFileName($msiPath))=$msiPath",
        "install-checks.ps1=$installChecks"
    )
    if ($StepScript) {
        $filePairs += "$([IO.Path]::GetFileName($StepScript))=$((Resolve-Path $StepScript).Path)"
    }
    $serveTimeout = if ($TimeoutMin -gt 0) { $TimeoutMin * 60 } elseif ($profile.acceleration -eq 'whpx') { 40 * 60 } else { 240 * 60 }
    $verdictFile = Join-Path $resultsDir 'verdict.txt'
    Remove-Item $verdictFile -Force -ErrorAction SilentlyContinue
    $serve = Start-Process -FilePath $cli.Exe `
        -ArgumentList ($cli.Arguments + @('serve', '-Port', 8765, '-Manifest', $manifestPath,
            '-ResultsDir', $resultsDir, '-File', ($filePairs -join ';'),
            '-TimeoutSec', $serveTimeout, '-VerdictFile', $verdictFile)) `
        -WindowStyle Hidden -PassThru -RedirectStandardOutput $serveLog -RedirectStandardError ($serveLog + '.err')
    Write-Host "guest channel: http://127.0.0.1:8765/ (guest sees http://10.0.2.2:8765/)"
    Write-Host "results dir:   $resultsDir"

    $serialPort = Get-FreeTcpPort
    $qmpPort = Get-FreeTcpPort
    $firmware = Complete-FreshWinFirmware -Arch $Arch
    $qemuArgs = New-FreshWinQemuArguments -Profile $profile -Phase run `
        -DiskPath $overlay -EfiCode $firmware.Code -EfiVars $runVars `
        -MemoryMib 4096 -CpuCount 4 -SerialPort $serialPort -QmpPort $qmpPort -Vnc:$Vnc
    $qemuLog = Join-Path $resultsDir 'qemu.log'
    Write-Host 'booting single-use guest …'
    $qemu = Start-FreshWinQemu -QemuArguments $qemuArgs -LogPath $qemuLog

    $verdict = 2
    try {
        # The serve process writes verdict.txt (pass|fail|timeout) — PS 5.1's
        # Start-Process -PassThru cannot reliably deliver dotnet children's
        # exit codes, so the file is the authoritative channel. The guest
        # shuts itself down after posting the final report.
        if (-not $serve.WaitForExit($serveTimeout * 1000 + 60000)) {
            Write-Host 'guest run: no verdict before timeout' -ForegroundColor Red
            Stop-Process -Id $serve.Id -Force -ErrorAction SilentlyContinue
        }
        if (Test-Path $verdictFile) {
            $verdict = switch ((Get-Content $verdictFile -Raw).Trim()) {
                'pass' { 0 }
                'fail' { 1 }
                default { 2 }
            }
        }
        if (-not $qemu.HasExited -and -not $qemu.WaitForExit(10 * 60 * 1000)) {
            Stop-Process -Id $qemu.Id -Force
        }
    }
    finally {
        if (-not $qemu.HasExited) { Stop-Process -Id $qemu.Id -Force -ErrorAction SilentlyContinue }
        if (-not $serve.HasExited) { Stop-Process -Id $serve.Id -Force -ErrorAction SilentlyContinue }
    }

    if ($KeepOverlay) {
        Write-Host "overlay kept for postmortem: $overlay"
    } else {
        Remove-Item $overlay, $runVars -Force -ErrorAction SilentlyContinue
    }
    if ($verdict -eq 0) {
        Write-Host "guest E2E PASSED — results in $resultsDir" -ForegroundColor Green
    } else {
        Write-Host "guest E2E FAILED ($verdict) — results in $resultsDir" -ForegroundColor Red
    }
    return $verdict
}

Export-ModuleMember -Function Build-FreshWinMedia, Build-FreshWinBase, Invoke-FreshWinTest, Get-FreshWinProfile
