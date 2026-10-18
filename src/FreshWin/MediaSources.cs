// FreshWin (c) Ze'ev Russak — Apache License 2.0 (see LICENSE + NOTICE).
// Derived from ZuTM's WindowsSources. Pinned download sources, verified at
// stage time. tiny11's archive-published SHA-1s were checked against the
// archive.org metadata and the SHA-256s computed from trusted downloads
// (2026-10-15); the virtio-win ISO publishes no checksum at all, so its
// SHA-256 was computed from the downloaded bytes. See NOTICE for licensing.

using System.Security.Cryptography;

namespace FreshWin;

public sealed record MediaPin
{
    public required string Url { get; init; }

    public required string FileName { get; init; }

    public required long SizeBytes { get; init; }

    /// <summary>SHA-256 of the pinned bytes (hex, empty when unknown).</summary>
    public string Sha256 { get; init; } = "";

    /// <summary>SHA-1 as published by archive.org (hex, empty when unknown).</summary>
    public string Sha1 { get; init; } = "";
}

public static class MediaSources
{
    public const string Tiny11ItemId = "tiny11_25H2";
    public const string VirtioWinRelease = "virtio-win-0.1.302-1";

    public static readonly MediaPin Tiny11X64 = new()
    {
        Url = "https://archive.org/download/tiny11_25H2/tiny11_25H2_Nov25.iso",
        FileName = "tiny11-x64.iso",
        SizeBytes = 5_730_246_656,
        Sha256 = "7b24815845684add7250808b3b0027ba4e94cf52c62e9ef40d5b965dd304d6ca",
        Sha1 = "51a2cb8e12432fa9fc88103f5a84040936c7088b",
    };

    public static readonly MediaPin Tiny11Arm64 = new()
    {
        Url = "https://archive.org/download/tiny11_25H2/tiny11_25H2_Oct25_arm64.iso",
        FileName = "tiny11-arm64.iso",
        SizeBytes = 5_554_755_584,
        Sha256 = "5c5d45799e1664d81802d1d5a85b9e50c1dcf278e1d2e7e10e3328fc8dd4615c",
        Sha1 = "8cc7db18c182917e6feb69ae442354bf43fca02c",
    };

    public static readonly MediaPin VirtioWin = new()
    {
        Url = $"https://fedorapeople.org/groups/virt/virtio-win/direct-downloads/archive-virtio/{VirtioWinRelease}/virtio-win-0.1.302.iso",
        FileName = "virtio-win-0.1.302.iso",
        SizeBytes = 877_373_440,
        Sha256 = "303f7ae40dad495d6ae474fdc571df58958a4dbc5c37a522d80f9a203867949d",
    };

    /// <summary>Modern x64 UEFI firmware — the QEMU-bundled EDK2 is too old for 24H2+ Windows media.</summary>
    public static readonly MediaPin Edk2OvmfRpm = new()
    {
        Url = "https://kojipkgs.fedoraproject.org/packages/edk2/20260812/8.fc45/noarch/edk2-ovmf-20260812-8.fc45.noarch.rpm",
        FileName = "edk2-ovmf-20260812-8.fc45.noarch.rpm",
        SizeBytes = 19_124_270,
        Sha256 = "e43479f76e6da33318ea9a64ef7f1bcc5f8c095a52f75e312f6ea25361e364e0",
    };

    public static readonly MediaPin Edk2Aarch64Rpm = new()
    {
        Url = "https://kojipkgs.fedoraproject.org/packages/edk2/20260812/8.fc45/noarch/edk2-aarch64-20260812-8.fc45.noarch.rpm",
        FileName = "edk2-aarch64-20260812-8.fc45.noarch.rpm",
        SizeBytes = 11_316_122,
        Sha256 = "a614d88da3bcee4f7f96a02f33bf9ffe7d49519a55e12ee8a5b9c850abb01423",
    };

    /// <summary>wimlib — wimsplit splits install.wim under FAT32's 4 GiB per-file ceiling.</summary>
    public static readonly MediaPin WimLibZip = new()
    {
        Url = "https://wimlib.net/downloads/wimlib-1.14.5-windows-x86_64-bin.zip",
        FileName = "wimlib.zip",
        SizeBytes = 778_747,
        Sha256 = "2f446d6fa3866582175f1a22a7be198eeee0aec7aba5b4e04ad25c99eae2d265",
    };

    /// <summary>iPXE wimboot — boots boot.wim straight from the FAT media, bypassing cdboot's unattendable "press any key" gate.</summary>
    public static readonly MediaPin WimBootX64 = new()
    {
        Url = "https://github.com/ipxe/wimboot/releases/download/v2.9.0/wimboot",
        FileName = "wimboot.efi",
        SizeBytes = 76_064,
        Sha256 = "5f067ccdc4d084d5bf77b6c853bd0f8402dfc2b4cd1b103d358993ae97fae8e3",
    };

    public static readonly MediaPin WimBootArm64 = new()
    {
        Url = "https://github.com/ipxe/wimboot/releases/download/v2.9.0/wimboot.arm64",
        FileName = "wimboot.arm64.efi",
        SizeBytes = 51_488,
        Sha256 = "b1440c6386981fb447b2f341294ec7a59546d496dff0f075b0f698c853f1c949",
    };

    public static MediaPin Pin(string name) => name.ToLowerInvariant() switch
    {
        "tiny11-x64" => Tiny11X64,
        "tiny11-arm64" => Tiny11Arm64,
        "virtio-win" => VirtioWin,
        "edk2-ovmf" => Edk2OvmfRpm,
        "edk2-aarch64" => Edk2Aarch64Rpm,
        "wimlib" => WimLibZip,
        "wimboot-x64" => WimBootX64,
        "wimboot-arm64" => WimBootArm64,
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "unknown pin"),
    };

    /// <summary>Verifies a staged file against a pin. Empty hashes are skipped.</summary>
    public static async Task VerifyAsync(string path, MediaPin pin)
    {
        var size = new FileInfo(path).Length;
        if (size != pin.SizeBytes)
        {
            throw new InvalidOperationException(
                $"{Path.GetFileName(path)} has {size} bytes, pinned size is {pin.SizeBytes} — the upstream file changed or the download is truncated.");
        }

        if (pin.Sha256.Length > 0)
        {
            var actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(File.OpenRead(path)));
            if (!string.Equals(actual, pin.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"SHA-256 mismatch for {Path.GetFileName(path)}: got {actual}.");
            }
        }

        if (pin.Sha1.Length > 0)
        {
            // Integrity cross-check against archive.org's published file hash —
            // not a security boundary (SHA-256 above is), so SHA-1 is fine here.
#pragma warning disable CA5350
            var actual = Convert.ToHexStringLower(await SHA1.HashDataAsync(File.OpenRead(path)));
#pragma warning restore CA5350
            if (!string.Equals(actual, pin.Sha1, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"SHA-1 mismatch for {Path.GetFileName(path)}: got {actual}.");
            }
        }
    }

    /// <summary>Downloads a pin into <paramref name="targetDirectory"/> unless a verified copy is already staged.</summary>
    public static async Task<string> StageAsync(MediaPin pin, string targetDirectory)
    {
        Directory.CreateDirectory(targetDirectory);
        var target = Path.Combine(targetDirectory, pin.FileName);
        var marker = target + ".verified";

        if (File.Exists(marker))
        {
            return target;
        }

        if (File.Exists(target))
        {
            // A staged copy without its marker (interrupted run, manual
            // download): verify in place instead of pulling gigabytes again.
            Console.WriteLine($"verifying staged {pin.FileName} …");
            try
            {
                await VerifyAsync(target, pin);
                await File.WriteAllTextAsync(marker, pin.Url + Environment.NewLine);
                return target;
            }
            catch (InvalidOperationException)
            {
                Console.WriteLine($"staged {pin.FileName} does not match the pin — re-downloading.");
                File.Delete(target);
            }
        }

        using var http = new HttpClient();
        Console.WriteLine($"downloading {pin.Url} …");
        var temp = target + ".part";
        using (var response = await http.GetAsync(pin.Url, HttpCompletionOption.ResponseHeadersRead))
        {
            response.EnsureSuccessStatusCode();
            await using var file = File.Create(temp);
            await response.Content.CopyToAsync(file);
        }

        File.Move(temp, target, overwrite: true);
        Console.WriteLine($"verifying {pin.FileName} …");
        await VerifyAsync(target, pin);
        await File.WriteAllTextAsync(marker, pin.Url + Environment.NewLine);
        return target;
    }
}
