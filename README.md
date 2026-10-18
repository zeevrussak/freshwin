# FreshWin — disposable, just-installed Windows test VMs on QEMU/Windows

FreshWin builds a **frozen** Windows base image from any Windows 11 ISO
(stock or tiny11) and then, for every test run, boots a **disposable
overlay** of it — so your app's installer and UI are always validated on a
pristine, just-installed Windows, never on a polluted dev box. Works for any
Windows app: MSI round trips, first-launch checks, UI automation, upgrade
tests.

Derived from [ZuTM](https://github.com/zeevrussak/ZuTM) — see NOTICE.
Apache License 2.0.

```
scripts\fetch-qemu.ps1                    # once: QEMU runtime for the host arch
scripts\build-base.ps1                    # once per arch: unattended install → frozen base (~30-60 min on WHPX)
scripts\run-tests.ps1 -Msi MyApp-x64.msi `# per run: single-use cycle (5-15 min)`
    -StepScript ui-drive.ps1 -StepArguments '-AppExe "{stepResult}\MyApp.exe"'
```

Every run starts from the frozen state; the overlay (and everything the
guest did) is destroyed afterwards. Results — transcripts, automatic MSI
logs, crash dumps, the runner log — land in
`artifacts\guest-e2e\<arch>\<timestamp>\`.

## What a test cycle does

1. `qemu-img` overlay on the frozen base + a copy of the frozen UEFI vars
   (boot entries live in NVRAM — a blank vars file would drop to the UEFI
   shell).
2. A loopback HTTP server serves the run manifest, your MSI and your step
   scripts; the guest reaches it at `http://10.0.2.2:8765/` through QEMU
   slirp.
3. The frozen guest runner (Startup-folder autologon script) fetches the
   payload, executes the steps, collects evidence, uploads it as
   `results.zip`, posts the verdict, shuts the guest down.
4. The harness tears the overlay down and exits 0/1.

Built-in step: `install-checks.ps1` (msiexec install → shortcut → payload →
launch-window → uninstall). Add your own steps with `-StepScript` — any
PowerShell script; `{stepResult}` substitutes the previous step's reported
result (e.g. the install directory from install-checks).

## The boot chain (why this isn't just `-cdrom`)

Windows ISO boot on QEMU/Windows + EDK2 has landmines FreshWin sidesteps by
design; each cost a day of forensics:

- **The cdboot gate.** `cdboot.efi` (the El Torito boot image) waits for a
  keypress and resets the machine in a loop when none arrives. No injectable
  input wins — QMP `send-key` and serial CRs reach the firmware's console,
  but cdboot races the firmware's own PS/2 driver for the raw keyboard. Same
  on 2020-era and 2026-era EDK2, both accelerators, every CD transport.
- **The fix: boot from a FAT32 USB disk, not the CD.** FreshWin writes its
  own FAT32 image (MBR + BPB + LFN, files as single extents — no admin
  rights, no mkfs) containing the ISO's tree with `install.wim` split into
  `install.swm` parts under FAT32's 4 GiB per-file ceiling, and
  [wimboot](https://ipxe.org/wimboot) as the bootloader. wimboot boots
  `boot.wim` straight from the volume with no gate — but its EFI scan only
  reads the volume **root**, so `boot.wim`/`boot.sdi`/`BCD` are copied
  there (it pulls `bootmgfw.efi` out of the WIM itself). This is the Rufus
  layout, industrialized.
- **Firmware age.** The QEMU-bundled EDK2 predates 24H2+ media; FreshWin
  stages a current Fedora OVMF/AAVMF build (pinned + hash-verified).
- **tiny11 quirks.** NTDEV's ISOs bake their own autounattend (removed —
  ours rides the media) and use solid-LZMS `install.wim` (exported non-solid
  before splitting; `optimize --no-solid` does *not* de-solid).

Per-arch guest shapes: x64 = q35 + e1000e + VGA (all in-box drivers);
ARM64 = `virt` + virtio-net/GPU (drivers staged from virtio-win during
provisioning). System disk is NVMe on both (QEMU 11 requires a namespace
serial — supplied). WHPX on same-arch hosts; TCG elsewhere (an install
takes hours under TCG; the ARM64 guest runs natively on ARM64 Windows
hosts).

## Layout

| Path | Purpose |
|---|---|
| `freshwin.psm1` | Orchestration: media build, base build, test cycles. All process spawning lives here. |
| `src/FreshWin/` | The pure-computation CLI: FAT32 writer, autounattend generator, pinned sources, serial-wait/QMP/serve (sockets only — zero process spawning, fully unit-tested). |
| `scripts/` | fetch-qemu, build-base/run-tests wrappers, `steps/install-checks.ps1`. |
| `work/` (gitignored) | Pins, firmware, install media, frozen bases, per-run overlays. |
| `artifacts/guest-e2e/` | Per-run results. |

Commands of the CLI (used by the module, usable directly):
`profile`, `stage`, `fat32`, `autounattend`, `sidecar`, `serial-wait`,
`qmp`, `serve`, `guest` — run `dotnet src/FreshWin/bin/Release/net10.0/FreshWin.dll` for help.

## Prerequisites & licensing

- .NET 10 SDK, 7-Zip, PowerShell 5.1+ (in-box), QEMU (fetched by the
  script). WHPX acceleration needs the "Windows Hypervisor Platform"
  optional feature; ARM64-on-ARM64 needs Windows 11 24H2+.
- A valid Windows license remains **your** responsibility. FreshWin never
  redistributes Windows images; ISOs are user-supplied, or auto-downloaded
  from pinned, hash-verified sources (tiny11 is NTDEV's unofficial build —
  pass `-IsoPath` with a stock ISO to avoid it entirely; the generated
  autounattend carries the TPM/SecureBoot/RAM/CPU/storage bypasses either
  way).
- Third-party components (QEMU GPLv2, wimboot GPLv2+, wimlib GPL/LGPL,
  EDK2 BSD, virtio-win GPLv2) are fetched at run time, never embedded —
  see NOTICE.

## Status & known open issue

The full pipeline (media → unattended install → provisioning → freeze) is
complete and unit-tested (37/37); the boot chain reaches the Windows boot
manager on both WHPX and TCG. One open issue from the ZuTM bring-up
remains: on some hosts the boot stalls after the spinner, before setup
starts (zero guest disk writes, zero file I/O — winload dying silently).
Debugging levers, in order of leverage: boot with `-Vnc` and watch; try
`-MemoryMib 8192`; try a stock ISO instead of tiny11; kernel-debug the
ramdisk boot from an elevated prompt (`bcdedit` requires elevation).

## Credits

Derived from [ZuTM](https://github.com/zeevrussak/ZuTM), the Windows port
of UTM, where this harness was built to test ZuTM's own installer.
wimboot by Michael Brown / [iPXE](https://ipxe.org/wimboot) is the
load-bearing piece of the boot chain.
