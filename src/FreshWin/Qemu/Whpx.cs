// FreshWin (c) Ze'ev Russak — Apache License 2.0 (see LICENSE + NOTICE).
// Derived from ZuTM's AcceleratorDetector. WHPX availability probing:
// same-architecture guests only; everything else runs under TCG.

using System.Runtime.InteropServices;

namespace FreshWin.Qemu;

public enum Acceleration
{
    /// <summary>Windows Hypervisor Platform — hardware acceleration when guest and host architectures match.</summary>
    Whpx,

    /// <summary>TCG JIT emulation — universal fallback (required for cross-architecture guests).</summary>
    Tcg,
}

public static class Whpx
{
    /// <summary>Host CPU architecture as QEMU names it.</summary>
    public static string HostArchitecture => RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.X64 => "x86_64",
        Architecture.Arm64 => "aarch64",
        Architecture.X86 => "i386",
        Architecture.Arm => "arm",
        _ => "unknown",
    };

    /// <summary>
    /// Probes the Windows Hypervisor Platform API surface. The export is
    /// "WHvGetCapability" — capital H — and GetProcAddress is case-sensitive;
    /// the wrong case reads as "feature not enabled" on every WHP-enabled
    /// machine, silently forcing TCG everywhere.
    /// </summary>
    public static bool IsAvailable()
    {
        if (!OperatingSystem.IsWindows() || !NativeLibrary.TryLoad("WinHvPlatform.dll", out var handle))
        {
            return false;
        }

        try
        {
            if (!NativeLibrary.TryGetExport(handle, "WHvGetCapability", out var address))
            {
                return false; // stub DLL without the WHv exports — feature not enabled
            }

            var whvGetCapability = Marshal.GetDelegateForFunctionPointer<WhvGetCapabilityDelegate>(address);

            // WHvCapabilityCodeHypervisorPresent = 0; result is a ULONG boolean.
            return whvGetCapability(0, out var present, sizeof(ulong), out _) == 0 && present != 0;
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or BadImageFormatException or MarshalDirectiveException)
        {
            return false;
        }
        finally
        {
            NativeLibrary.Free(handle);
        }
    }

    /// <summary>WHPX can only virtualize guests of the host's own architecture class.</summary>
    public static bool CanAccelerate(string guestArchitecture) =>
        (HostArchitecture, guestArchitecture) switch
        {
            ("x86_64", "x86_64") => true,
            ("x86_64", "i386") => true, // 32-bit x86 guests run under an x64 WHPX partition
            ("aarch64", "aarch64") => true,
            _ => false,
        };

    public static Acceleration Detect(string guestArchitecture) =>
        IsAvailable() && CanAccelerate(guestArchitecture) ? Acceleration.Whpx : Acceleration.Tcg;

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int WhvGetCapabilityDelegate(
        int capabilityCode, out ulong capabilityValue, int capabilityValueSize, out int writtenSize);
}
