// FreshWin (c) Ze'ev Russak — Apache License 2.0 (see LICENSE + NOTICE).
// Derived from ZuTM's BaseImageSidecar. win11-<arch>-base.qcow2.ready.json —
// the frozen base image's stamp of readiness. Written only after the
// provisioning reboot proved autologon + runner; the harness refuses to run
// tests against a base without one.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace FreshWin;

public sealed record BaseImageSidecar
{
    public int Schema { get; init; } = 1;

    public string Arch { get; init; } = "";

    public string IsoName { get; init; } = "";

    /// <summary>Pin URL for auto-downloaded ISOs; "user-supplied" for local ISOs.</summary>
    public string IsoSource { get; init; } = "";

    public string IsoSha256 { get; init; } = "";

    public string? VirtioWinSha256 { get; init; }

    public string QemuRoot { get; init; } = "";

    public bool Accelerated { get; init; }

    public DateTimeOffset CreatedUtc { get; init; }

    public static string PathFor(string baseImagePath) => baseImagePath + ".ready.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public static BaseImageSidecar Load(string baseImagePath)
    {
        var path = PathFor(baseImagePath);
        if (!File.Exists(path))
        {
            throw new InvalidOperationException(
                $"no ready sidecar next to {Path.GetFileName(baseImagePath)} — build the base image first (it must complete its verification boot before it can be used).");
        }

        var sidecar = JsonSerializer.Deserialize<BaseImageSidecar>(File.ReadAllText(path), JsonOptions)
            ?? throw new InvalidOperationException($"unreadable sidecar at {path}");
        if (sidecar.Schema != 1)
        {
            throw new InvalidOperationException($"sidecar schema {sidecar.Schema} at {path} is newer than this tool understands.");
        }

        return sidecar;
    }

    public void Save(string baseImagePath)
    {
        File.WriteAllText(PathFor(baseImagePath), JsonSerializer.Serialize(this, JsonOptions));
    }
}
