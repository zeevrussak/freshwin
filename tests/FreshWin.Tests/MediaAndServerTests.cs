// FreshWin (c) Ze'ev Russak — Apache License 2.0 (see LICENSE + NOTICE).
// Derived from ZuTM's GuestFileServerTests + WindowsSourcesTests +
// WinGuestProfileTests + BaseImageSidecarTests.

using Xunit;

namespace FreshWin.Tests;

public class GuestProfileTests
{
    [Fact]
    public void X64_UsesQ35WithInBoxDevices()
    {
        var profile = GuestProfile.For(GuestArch.X64);

        Assert.Equal("x86_64", profile.SystemArchitecture);
        Assert.Equal("q35", profile.MachineTarget);
        // e1000e: PCIe-native (q35 has no legacy PCI slots) and in-box on Windows.
        Assert.Equal("e1000e", profile.NetworkHardware);
        // The installer travels as a bootable FAT32 USB disk, never as a CD.
        Assert.Equal("usb", profile.InstallMediaInterface);
        // std VGA needs no guest driver — MS Basic Display Driver binds to it.
        Assert.Equal("VGA", profile.DisplayHardware);
        Assert.False(profile.NeedsVirtioDrivers);
        Assert.Equal("win11-x64-base.qcow2", profile.BaseImageName);
        Assert.Equal("win11-x64-vars.fd", profile.BaseVarsName);
    }

    [Fact]
    public void Arm64_UsesVirtWithVirtioDrivers()
    {
        var profile = GuestProfile.For(GuestArch.Arm64);

        Assert.Equal("aarch64", profile.SystemArchitecture);
        Assert.Equal("virt", profile.MachineTarget);
        // No in-box e1000 driver on Windows ARM64 — virtio-net staged from virtio-win.
        Assert.Equal("virtio-net-pci", profile.NetworkHardware);
        Assert.Equal("usb", profile.InstallMediaInterface);
        Assert.Equal("virtio-gpu-pci", profile.DisplayHardware);
        Assert.True(profile.NeedsVirtioDrivers);
        Assert.Equal("win11-arm64-base.qcow2", profile.BaseImageName);
    }
}

public class MediaSourcesTests
{
    [Theory]
    [InlineData(nameof(MediaSources.Tiny11X64))]
    [InlineData(nameof(MediaSources.Tiny11Arm64))]
    [InlineData(nameof(MediaSources.VirtioWin))]
    [InlineData(nameof(MediaSources.Edk2OvmfRpm))]
    [InlineData(nameof(MediaSources.Edk2Aarch64Rpm))]
    [InlineData(nameof(MediaSources.WimLibZip))]
    [InlineData(nameof(MediaSources.WimBootX64))]
    [InlineData(nameof(MediaSources.WimBootArm64))]
    public void Pins_AreComplete(string pinName)
    {
        var pin = pinName switch
        {
            nameof(MediaSources.Tiny11X64) => MediaSources.Tiny11X64,
            nameof(MediaSources.Tiny11Arm64) => MediaSources.Tiny11Arm64,
            nameof(MediaSources.VirtioWin) => MediaSources.VirtioWin,
            nameof(MediaSources.Edk2OvmfRpm) => MediaSources.Edk2OvmfRpm,
            nameof(MediaSources.Edk2Aarch64Rpm) => MediaSources.Edk2Aarch64Rpm,
            nameof(MediaSources.WimLibZip) => MediaSources.WimLibZip,
            nameof(MediaSources.WimBootX64) => MediaSources.WimBootX64,
            _ => MediaSources.WimBootArm64,
        };

        Assert.StartsWith("https://", pin.Url, StringComparison.Ordinal);
        Assert.True(pin.SizeBytes > 1_000, "expected a real asset size");
        Assert.Matches("^[0-9a-f]{64}$", pin.Sha256);
        Assert.True(pin.Sha1.Length == 0 || System.Text.RegularExpressions.Regex.IsMatch(pin.Sha1, "^[0-9a-f]{40}$"));
    }

    [Fact]
    public void Pin_MapsNames()
    {
        Assert.Same(MediaSources.Tiny11X64, MediaSources.Pin("tiny11-x64"));
        Assert.Same(MediaSources.WimBootArm64, MediaSources.Pin("WIMBOOT-ARM64"));
        Assert.Throws<ArgumentOutOfRangeException>(() => MediaSources.Pin("nope"));
    }
}

public class GuestFileServerTests
{
    [Theory]
    [InlineData(null, "results.zip")]
    [InlineData("", "results.zip")]
    [InlineData("results.zip", "results.zip")]
    [InlineData("guest-results.zip", "guest-results.zip")]
    [InlineData("..\\..\\windows\\system32\\evil.zip", ".._.._windows_system32_evil.zip")]
    [InlineData("../../etc/passwd", ".._.._etc_passwd.zip")]
    [InlineData("a/b\\c.zip", "a_b_c.zip")]
    [InlineData("no-extension", "no-extension.zip")]
    [InlineData("a<b>:c|d?.zip", "a_b__c_d_.zip")]
    public void SanitizeUploadName_ContainsTheUpload(string? input, string expected)
    {
        Assert.Equal(expected, GuestFileServer.SanitizeUploadName(input));
    }
}

public class GuestRunJsonTests
{
    [Fact]
    public void Report_RoundTrips_WithCamelCaseContract()
    {
        var json = """
            {"overall":"pass","steps":[{"step":"install-checks","exitCode":0,"durationMs":42000}]}
            """;

        var report = GuestRunJson.Deserialize<GuestRunReport>(json)!;

        Assert.Equal("pass", report.Overall);
        var step = Assert.Single(report.Steps);
        Assert.Equal("install-checks", step.Step);
        Assert.Equal(0, step.ExitCode);
        Assert.Equal(42000, step.DurationMs);
    }
}

public class BaseImageSidecarTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("freshwin-sidecar-tests-").FullName;

    [Fact]
    public void SaveLoad_RoundTrips()
    {
        var baseDisk = Path.Combine(_root, "win11-x64-base.qcow2");
        File.WriteAllText(baseDisk, "image");
        var sidecar = new BaseImageSidecar
        {
            Arch = "x64",
            IsoName = "tiny11-x64.iso",
            IsoSource = "pin:tiny11-x64",
            IsoSha256 = "ab",
            VirtioWinSha256 = null,
            QemuRoot = @"D:\runtimes\qemu",
            Accelerated = true,
            CreatedUtc = DateTimeOffset.UtcNow,
        };

        sidecar.Save(baseDisk);
        var loaded = BaseImageSidecar.Load(baseDisk);

        Assert.Equal(sidecar.Arch, loaded.Arch);
        Assert.Equal(sidecar.IsoName, loaded.IsoName);
        Assert.Equal(sidecar.IsoSource, loaded.IsoSource);
        Assert.Equal(sidecar.IsoSha256, loaded.IsoSha256);
        Assert.Null(loaded.VirtioWinSha256);
        Assert.Equal(sidecar.QemuRoot, loaded.QemuRoot);
        Assert.True(loaded.Accelerated);
    }

    [Fact]
    public void Load_ThrowsWithoutSidecar()
    {
        Assert.Throws<InvalidOperationException>(() => BaseImageSidecar.Load(Path.Combine(_root, "missing-base.qcow2")));
    }

    [Fact]
    public void Load_ThrowsOnNewerSchema()
    {
        var baseDisk = Path.Combine(_root, "future-base.qcow2");
        File.WriteAllText(baseDisk, "image");
        File.WriteAllText(
            BaseImageSidecar.PathFor(baseDisk),
            """{"schema":99,"arch":"x64"}""");

        Assert.Throws<InvalidOperationException>(() => BaseImageSidecar.Load(baseDisk));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
