// FreshWin (c) Ze'ev Russak — Apache License 2.0 (see LICENSE + NOTICE).
// Derived from ZuTM's QemuRuntime. Where the QEMU installation lives.

namespace FreshWin.Qemu;

/// <summary>Where the QEMU installation that FreshWin drives lives.</summary>
public sealed class QemuRuntime
{
    public string RootPath { get; }

    private readonly string _binDirectory;

    public QemuRuntime(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        RootPath = Path.GetFullPath(rootPath);

        // Two layouts exist: installers that keep executables at the root
        // (qemu.weilnetz.de — QEMU resolves its modules relative to the exe,
        // so this layout must be preserved) and distributions that use bin\.
        _binDirectory = File.Exists(Path.Combine(RootPath, "qemu-system-x86_64.exe"))
                        || File.Exists(Path.Combine(RootPath, "qemu-system-aarch64.exe"))
            ? RootPath
            : Path.Combine(RootPath, "bin");
    }

    public string BinDirectory => _binDirectory;

    public string ShareDirectory => Path.Combine(RootPath, "share");

    /// <summary>Path of the qemu-system executable for a guest architecture (qemu-system-x86_64.exe, qemu-system-aarch64.exe, …).</summary>
    public string SystemExecutable(string architecture) =>
        Path.Combine(BinDirectory, $"qemu-system-{architecture}.exe");

    public string QemuImgPath => Path.Combine(BinDirectory, "qemu-img.exe");

    /// <summary>UEFI firmware for a guest architecture, or null when not installed. Both bundled layouts are probed.</summary>
    public string? FindUefiFirmware(string architecture)
    {
        var edk2 = Path.Combine(ShareDirectory, $"edk2-{architecture}");
        var candidates = new[]
        {
            Path.Combine(edk2, "code.fd"),
            Path.Combine(edk2, $"{architecture}_code.fd"),
            Path.Combine(ShareDirectory, $"edk2-{architecture}-code.fd"),
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    private static bool LooksLikeRuntime(string directory) =>
        Directory.Exists(Path.Combine(directory, "bin"))
        || File.Exists(Path.Combine(directory, "qemu-system-x86_64.exe"))
        || File.Exists(Path.Combine(directory, "qemu-system-aarch64.exe"))
        || File.Exists(Path.Combine(directory, "bin", "qemu-system-x86_64.exe"))
        || File.Exists(Path.Combine(directory, "bin", "qemu-system-aarch64.exe"));

    /// <summary>Default search order for a QEMU runtime on this machine.</summary>
    public static QemuRuntime? Discover(string? explicitRoot = null)
    {
        IEnumerable<string> roots =
        [
            .. (explicitRoot is null ? [] : new[] { explicitRoot }),
            // Environment override first — power users and CI pin it here.
            Environment.GetEnvironmentVariable("FRESHWIN_QEMU_ROOT") ?? "",
        ];

        foreach (var root in roots.Where(r => !string.IsNullOrWhiteSpace(r)))
        {
            if (LooksLikeRuntime(root))
            {
                return new QemuRuntime(root);
            }
        }

        // Repo layout: run scripts/fetch-qemu.ps1, then work from anywhere.
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "runtimes", "qemu");
            if (LooksLikeRuntime(candidate))
            {
                return new QemuRuntime(candidate);
            }
        }

        return null;
    }

    public static QemuRuntime Require(string? explicitRoot = null) =>
        Discover(explicitRoot)
        ?? throw new InvalidOperationException(
            "QEMU runtime not found — run scripts/fetch-qemu.ps1 or set FRESHWIN_QEMU_ROOT.");
}
