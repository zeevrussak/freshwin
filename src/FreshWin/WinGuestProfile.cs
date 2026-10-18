// FreshWin (c) Ze'ev Russak — Apache License 2.0 (see LICENSE + NOTICE).
// Derived from ZuTM's WinGuestProfile. Per-architecture guest shape: the
// single source of truth the PowerShell orchestrator reads via `profile`.

namespace FreshWin;

public enum GuestArch
{
    X64,
    Arm64,
}

public sealed record GuestProfile
{
    public required GuestArch Arch { get; init; }

    /// <summary>QEMU architecture string (x86_64 / aarch64).</summary>
    public required string SystemArchitecture { get; init; }

    /// <summary>Machine target: q35 for x64, virt for ARM64.</summary>
    public required string MachineTarget { get; init; }

    /// <summary>NIC model with a driver Windows can use from first boot.</summary>
    public required string NetworkHardware { get; init; }

    /// <summary>Install media travels as a bootable FAT32 USB disk on both arches (El Torito/UDF/ATAPI boot wedges on this QEMU/EDK2 stack).</summary>
    public required string InstallMediaInterface { get; init; }

    /// <summary>Display device: gives the autologon session a monitor so UIA has a desktop.</summary>
    public required string DisplayHardware { get; init; }

    /// <summary>ARM64 needs virtio-win drivers (NIC + GPU) staged into the image.</summary>
    public required bool NeedsVirtioDrivers { get; init; }

    public required string BaseImageName { get; init; }

    public required string BaseVarsName { get; init; }

    public static GuestProfile For(GuestArch arch) => arch switch
    {
        GuestArch.X64 => new GuestProfile
        {
            Arch = arch,
            SystemArchitecture = "x86_64",
            MachineTarget = "q35",
            // e1000e: PCIe-native for q35 and in-box on Windows 10+; plain e1000
            // is legacy PCI and awkward to place on a q35 root bus.
            NetworkHardware = "e1000e",
            InstallMediaInterface = "usb",
            // std VGA needs no guest driver — MS Basic Display Driver binds to it.
            DisplayHardware = "VGA",
            NeedsVirtioDrivers = false,
            BaseImageName = "win11-x64-base.qcow2",
            BaseVarsName = "win11-x64-vars.fd",
        },
        GuestArch.Arm64 => new GuestProfile
        {
            Arch = arch,
            SystemArchitecture = "aarch64",
            MachineTarget = "virt",
            // No in-box e1000 driver on Windows ARM64 — virtio-net staged from virtio-win.
            NetworkHardware = "virtio-net-pci",
            InstallMediaInterface = "usb",
            DisplayHardware = "virtio-gpu-pci",
            NeedsVirtioDrivers = true,
            BaseImageName = "win11-arm64-base.qcow2",
            BaseVarsName = "win11-arm64-vars.fd",
        },
        _ => throw new ArgumentOutOfRangeException(nameof(arch), arch, null),
    };

    public static GuestArch DefaultArch() =>
        System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture
            == System.Runtime.InteropServices.Architecture.Arm64
            ? GuestArch.Arm64
            : GuestArch.X64;
}
