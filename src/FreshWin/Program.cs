// FreshWin (c) Ze'ev Russak — Apache License 2.0 (see LICENSE + NOTICE).
// Derived from ZuTM's TestEnv tool. Pure-computation CLI: filesystems,
// answer files, pins, sockets. Every process spawn (qemu, qemu-img, 7-Zip,
// wimlib) lives in the PowerShell layer — freshwin.psm1 — by design.

using System.Net.Sockets;
using System.Text.Json;
using FreshWin;
using FreshWin.Qemu;

namespace FreshWin;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        var command = args.FirstOrDefault(a => !a.StartsWith('-')) ?? "help";
        try
        {
            return command switch
            {
                "profile" => ProfileCommand(args),
                "guest" => GuestCommand(args),
                "stage" => await StageCommand(args),
                "fat32" => Fat32Command(args),
                "autounattend" => AutounattendCommand(args),
                "sidecar" => SidecarCommand(args),
                "serial-wait" => await SerialWaitCommand(args),
                "qmp" => await QmpCommand(args),
                "serve" => await ServeCommand(args),
                _ => Help(),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }
    }

    private static int Help()
    {
        Console.WriteLine("""
            FreshWin — disposable, just-installed Windows test VMs on QEMU/Windows.

            This assembly is the pure-computation half; the PowerShell module
            (freshwin.psm1) orchestrates phases and spawns QEMU. Commands:

              profile -Arch x64|arm64
                  Guest shape as JSON (machine, NIC, display, base image names).

              stage -Pin <tiny11-x64|tiny11-arm64|virtio-win|edk2-ovmf|
                          edk2-aarch64|wimlib|wimboot-x64|wimboot-arm64> -Dir <dir>
                  Download + hash-verify a pinned dependency (cached by marker).

              fat32 -Source <dir> -Out <img>
                  Build a bootable FAT32 disk image from a staging directory.

              autounattend -Arch x64|arm64 -Out <file> [-AdminUser freshwin]
                            [-AdminPassword freshwin] [-ComputerName FRESHWINVM]
                            [-ImageName "Windows 11 Pro"] [-ProvisionScript freshwin-provision.ps1]
                  Generate the unattended answer file.

              sidecar write -BaseImage <img> -Arch <arch> -IsoName <name> -IsoSource <src>
                             -IsoSha256 <sha> [-VirtioWinSha256 <sha>] -QemuRoot <dir> [-Accelerated]
              sidecar check -BaseImage <img> [-Arch <arch>]
                  Stamp / validate the frozen base image's ready marker.

              serial-wait -Port <n> -Marker <text> -TimeoutSec <n> [-HeartbeatMs 300]
                  Connect to the guest serial TCP port, optionally drip CRs (satisfies
                  cdboot's keypress gate during boot), wait for a marker.
                  Exit 0 = found, 3 = timeout (log tail on stdout).

              qmp -Port <n> -Command <name> [-JsonArgs '{...}']
                  Execute a QMP command, print the return JSON.

              serve -Port <n> -Manifest <file> -ResultsDir <dir> -File name=path[;name2=path2]
                     [-TimeoutSec 2400]
                  Run the guest HTTP channel; exit 0 when the final report is 'pass',
                  1 on failure, 2 on timeout. Uploads land in ResultsDir.
            """);
        return 0;
    }

    private static string? Option(string[] args, string name)
    {
        for (var i = 0; i + 1 < args.Length; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }

        return null;
    }

    private static bool HasFlag(string[] args, string name) =>
        args.Contains(name, StringComparer.OrdinalIgnoreCase);

    private static GuestArch ParseArch(string[] args)
    {
        var arch = Option(args, "-Arch") ?? "";
        return arch.ToLowerInvariant() switch
        {
            "arm64" => GuestArch.Arm64,
            "x64" or "amd64" => GuestArch.X64,
            "" => GuestProfile.DefaultArch(),
            var other => throw new InvalidOperationException($"unknown -Arch '{other}' (use x64 or arm64)"),
        };
    }

    // -- profile -----------------------------------------------------------------

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private static int ProfileCommand(string[] args)
    {
        var profile = GuestProfile.For(ParseArch(args));
        var acceleration = Qemu.Whpx.Detect(profile.SystemArchitecture);
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            arch = profile.Arch.ToString().ToLowerInvariant(),
            systemArchitecture = profile.SystemArchitecture,
            machineTarget = profile.MachineTarget,
            networkHardware = profile.NetworkHardware,
            installMediaInterface = profile.InstallMediaInterface,
            displayHardware = profile.DisplayHardware,
            needsVirtioDrivers = profile.NeedsVirtioDrivers,
            baseImageName = profile.BaseImageName,
            baseVarsName = profile.BaseVarsName,
            acceleration = acceleration.ToString().ToLowerInvariant(),
        }, JsonOptions));
        return 0;
    }

    // -- guest --------------------------------------------------------------------

    /// <summary>Extracts an embedded guest script (frozen into images by the orchestrator).</summary>
    private static int GuestCommand(string[] args)
    {
        var name = (Option(args, "-Name") ?? "").ToLowerInvariant() switch
        {
            "provision" => "provision.ps1",
            "runner" => "runner.ps1",
            var other => throw new InvalidOperationException($"unknown guest script '{other}' (provision|runner)"),
        };
        var output = Option(args, "-Out") ?? throw new InvalidOperationException("guest needs -Out");
        var stream = typeof(Program).Assembly.GetManifestResourceStream($"FreshWin.Resources.guest.{name}")
            ?? throw new InvalidOperationException($"embedded guest resource missing: {name}");
        using var reader = new StreamReader(stream);
        File.WriteAllText(Path.GetFullPath(output), reader.ReadToEnd());
        Console.WriteLine($"wrote {output}");
        return 0;
    }

    // -- stage -------------------------------------------------------------------

    private static async Task<int> StageCommand(string[] args)
    {
        var pinName = Option(args, "-Pin") ?? throw new InvalidOperationException("stage needs -Pin");
        var dir = Option(args, "-Dir") ?? throw new InvalidOperationException("stage needs -Dir");
        var path = await MediaSources.StageAsync(MediaSources.Pin(pinName), Path.GetFullPath(dir));
        Console.WriteLine(path);
        return 0;
    }

    // -- fat32 -------------------------------------------------------------------

    private static int Fat32Command(string[] args)
    {
        var source = Option(args, "-Source") ?? throw new InvalidOperationException("fat32 needs -Source and -Out");
        var output = Option(args, "-Out") ?? throw new InvalidOperationException("fat32 needs -Source and -Out");
        Fat32Image.Write(Path.GetFullPath(output), Path.GetFullPath(source), new Fat32Image.Options
        {
            Log = message => Console.WriteLine(message),
        });
        Console.WriteLine($"wrote {output} ({new FileInfo(output).Length / 1024 / 1024} MiB)");
        return 0;
    }

    // -- autounattend ------------------------------------------------------------

    private static int AutounattendCommand(string[] args)
    {
        var output = Option(args, "-Out") ?? throw new InvalidOperationException("autounattend needs -Out");
        File.WriteAllText(
            Path.GetFullPath(output),
            AutounattendXml.Generate(
                ParseArch(args),
                Option(args, "-AdminUser") ?? "freshwin",
                Option(args, "-AdminPassword") ?? "freshwin",
                Option(args, "-ComputerName") ?? "FreshWinVM",
                Option(args, "-ImageName") ?? "Windows 11 Pro",
                Option(args, "-ProvisionScript") ?? "freshwin-provision.ps1"));
        Console.WriteLine($"wrote {output}");
        return 0;
    }

    // -- sidecar -------------------------------------------------------------------

    private static int SidecarCommand(string[] args)
    {
        var verb = args.Skip(1).FirstOrDefault(a => !a.StartsWith('-'));
        var baseImage = Option(args, "-BaseImage") ?? throw new InvalidOperationException("sidecar needs -BaseImage");
        if (verb == "write")
        {
            new BaseImageSidecar
            {
                Arch = (Option(args, "-Arch") ?? throw new InvalidOperationException("sidecar write needs -Arch")).ToLowerInvariant(),
                IsoName = Option(args, "-IsoName") ?? "",
                IsoSource = Option(args, "-IsoSource") ?? "user-supplied",
                IsoSha256 = Option(args, "-IsoSha256") ?? "",
                VirtioWinSha256 = Option(args, "-VirtioWinSha256"),
                QemuRoot = Option(args, "-QemuRoot") ?? "",
                Accelerated = HasFlag(args, "-Accelerated"),
                CreatedUtc = DateTimeOffset.UtcNow,
            }.Save(baseImage);
            Console.WriteLine($"sidecar written for {Path.GetFileName(baseImage)}");
            return 0;
        }

        if (verb == "check")
        {
            var sidecar = BaseImageSidecar.Load(baseImage);
            Console.WriteLine(JsonSerializer.Serialize(sidecar, JsonOptions));
            var arch = Option(args, "-Arch");
            if (arch is not null && !string.Equals(sidecar.Arch, arch.ToLowerInvariant(), StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"base image sidecar is for arch '{sidecar.Arch}', not '{arch}'.");
            }

            return 0;
        }

        throw new InvalidOperationException("sidecar needs verb write|check");
    }

    // -- serial-wait ----------------------------------------------------------------

    private static async Task<int> SerialWaitCommand(string[] args)
    {
        var port = int.Parse(Option(args, "-Port") ?? throw new InvalidOperationException("serial-wait needs -Port"));
        var marker = Option(args, "-Marker") ?? throw new InvalidOperationException("serial-wait needs -Marker");
        var timeoutSec = int.Parse(Option(args, "-TimeoutSec") ?? "600");
        var heartbeatMs = int.Parse(Option(args, "-HeartbeatMs") ?? "0");

        SerialChannel? channel = null;
        for (var attempt = 0; attempt < 40 && channel is null; attempt++)
        {
            try
            {
                channel = await SerialChannel.ConnectAsync("127.0.0.1", port);
            }
            catch (SocketException)
            {
                await Task.Delay(250);
            }
        }

        if (channel is null)
        {
            Console.Error.WriteLine($"serial port {port} did not come up");
            return 2;
        }

        using var heartbeatCts = new CancellationTokenSource();
        Task? heartbeat = null;
        if (heartbeatMs > 0)
        {
            // Windows install media's El Torito boot image (cdboot) waits for a
            // keypress and exits with EFI_TIMEOUT when none arrives — the serial
            // console is the firmware's ConIn, so a steady drip of CRs satisfies
            // it and is harmless at every other stage.
            heartbeat = Task.Run(async () =>
            {
                while (!heartbeatCts.IsCancellationRequested)
                {
                    await channel.SendLineAsync(string.Empty, heartbeatCts.Token);
                    await Task.Delay(heartbeatMs, heartbeatCts.Token);
                }
            }, heartbeatCts.Token);
        }

        try
        {
            await channel.WaitForAsync(marker, TimeSpan.FromSeconds(timeoutSec));
            Console.WriteLine($"marker found: {marker}");
            return 0;
        }
        catch (OperationCanceledException)
        {
            var log = channel.Log;
            Console.WriteLine($"marker NOT found within {timeoutSec}s\n--- serial tail ---\n{log[^Math.Min(3000, log.Length)..]}");
            return 3;
        }
        finally
        {
            heartbeatCts.Cancel();
            await channel.DisposeAsync();
        }
    }

    // -- qmp -----------------------------------------------------------------------

    private static async Task<int> QmpCommand(string[] args)
    {
        var port = int.Parse(Option(args, "-Port") ?? throw new InvalidOperationException("qmp needs -Port"));
        var command = Option(args, "-Command") ?? throw new InvalidOperationException("qmp needs -Command");
        var jsonArgs = Option(args, "-JsonArgs");
        object? arguments = jsonArgs is null
            ? null
            : JsonSerializer.Deserialize<Dictionary<string, object?>>(jsonArgs);

        await using var qmp = await QmpClient.ConnectAsync("127.0.0.1", port);
        var result = await qmp.ExecuteAsync(command, arguments);
        Console.WriteLine(result.GetRawText());
        return 0;
    }

    // -- serve ---------------------------------------------------------------------

    private static async Task<int> ServeCommand(string[] args)
    {
        var port = int.Parse(Option(args, "-Port") ?? throw new InvalidOperationException("serve needs -Port"));
        var manifestPath = Option(args, "-Manifest") ?? throw new InvalidOperationException("serve needs -Manifest");
        var resultsDir = Option(args, "-ResultsDir") ?? throw new InvalidOperationException("serve needs -ResultsDir");
        var timeoutSec = int.Parse(Option(args, "-TimeoutSec") ?? "2400");
        // PS 5.1 Start-Process -PassThru cannot reliably deliver the exit code
        // of dotnet children — the verdict file is the authoritative channel.
        var verdictFile = Option(args, "-VerdictFile");

        var verdict = 2;
        try
        {
            await using var server = new GuestFileServer(
                port,
                await File.ReadAllTextAsync(manifestPath),
                Path.GetFullPath(resultsDir),
                message => Console.WriteLine($"(http) {message}"));
            var files = Option(args, "-File") ?? "";
            foreach (var pair in files.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var separator = pair.IndexOf('=');
                server.RegisterFile(pair[..separator], pair[(separator + 1)..]);
            }

            server.Start();
            Console.WriteLine($"guest channel: http://127.0.0.1:{port}/ (guest sees http://10.0.2.2:{port}/)");

            var completed = await Task.WhenAny(server.FinalReport, Task.Delay(TimeSpan.FromSeconds(timeoutSec)));
            if (completed != server.FinalReport)
            {
                Console.Error.WriteLine("no final report before timeout");
                return 2;
            }

            var report = await server.FinalReport;
            foreach (var step in report.Steps)
            {
                Console.WriteLine($"step {step.Step}: exit {step.ExitCode} in {TimeSpan.FromMilliseconds(step.DurationMs):g}");
            }

            var passed = report.Overall == "pass" && report.Steps.All(step => step.ExitCode == 0);
            Console.WriteLine(passed ? "guest run PASSED" : "guest run FAILED");
            verdict = passed ? 0 : 1;
            return verdict;
        }
        finally
        {
            if (verdictFile is not null)
            {
                await File.WriteAllTextAsync(verdictFile, verdict switch
                {
                    0 => "pass",
                    1 => "fail",
                    _ => "timeout",
                });
            }
        }
    }
}
