// FreshWin (c) Ze'ev Russak — Apache License 2.0 (see LICENSE + NOTICE).
// Derived from ZuTM's GuestFileServer. Loopback HTTP channel between the
// host harness and the guest runner:
//   GET  /manifest.json      — the run plan
//   GET  /files/{name}       — payload files (registered by name only — no path handling)
//   POST /report             — step/final reports (JSON body)
//   POST /upload?name=x.zip  — results archive from the guest
// The guest reaches it through QEMU slirp at http://10.0.2.2:<port>/.
// Loopback-only binding: nothing outside the host (and the VM it hosts) can talk to it.

using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FreshWin;

public sealed record GuestStepReport
{
    public string Step { get; init; } = "";

    public int ExitCode { get; init; }

    public long DurationMs { get; init; }
}

/// <summary>Final verdict posted by the guest runner before shutdown.</summary>
public sealed record GuestRunReport
{
    public string Overall { get; init; } = "";

    public IReadOnlyList<GuestStepReport> Steps { get; init; } = [];
}

public static class GuestRunJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);
}

public sealed class GuestFileServer : IAsyncDisposable
{
    /// <summary>Fixed default port — the frozen guest runner polls 10.0.2.2:8765.</summary>
    public const int DefaultPort = 8765;

    private const long MaxUploadBytes = 512 * 1024 * 1024;

    private readonly HttpListener _listener = new();
    private readonly Dictionary<string, string> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _uploadDirectory;
    private readonly string _manifestJson;
    private readonly Action<string>? _log;
    private readonly TaskCompletionSource<GuestRunReport> _finalReport = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private CancellationTokenSource? _cts;

    public GuestFileServer(int port, string manifestJson, string uploadDirectory, Action<string>? log = null)
    {
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        _manifestJson = manifestJson;
        _uploadDirectory = uploadDirectory;
        _log = log;
        Port = port;
    }

    public int Port { get; }

    /// <summary>Completes with the guest's final pass/fail verdict.</summary>
    public Task<GuestRunReport> FinalReport => _finalReport.Task;

    /// <summary>Result archive uploaded by the guest, if any.</summary>
    public string? UploadedArchive { get; private set; }

    public void RegisterFile(string name, string sourcePath) => _files[name] = sourcePath;

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _listener.Start();
        _ = Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    private async Task AcceptLoopAsync(CancellationToken cancellation)
    {
        while (!cancellation.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (Exception ex) when (ex is ObjectDisposedException or HttpListenerException)
            {
                return; // stopped
            }

            _ = Task.Run(() => HandleAsync(context), CancellationToken.None);
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        var request = context.Request;
        try
        {
            var path = request.Url?.AbsolutePath ?? "/";
            Log($"{request.RemoteEndPoint} {request.HttpMethod} {path}");
            switch (request.HttpMethod)
            {
                case "GET" when path == "/manifest.json":
                    await RespondAsync(context, "application/json", _manifestJson);
                    break;
                case "GET" when path.StartsWith("/files/", StringComparison.Ordinal):
                    await ServeFileAsync(context, path);
                    break;
                case "POST" when path == "/report":
                    await AcceptReportAsync(context);
                    break;
                case "POST" when path == "/upload":
                    await AcceptUploadAsync(context);
                    break;
                default:
                    context.Response.StatusCode = 404;
                    context.Response.Close();
                    break;
            }
        }
        catch (Exception ex)
        {
            Log($"request failed: {ex.Message}");
            try
            {
                context.Response.StatusCode = 500;
                context.Response.Close();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    private async Task ServeFileAsync(HttpListenerContext context, string path)
    {
        var name = Uri.UnescapeDataString(path["/files/".Length..]);
        if (!_files.TryGetValue(name, out var source))
        {
            context.Response.StatusCode = 404;
            context.Response.Close();
            return;
        }

        // Content-Length up front: PS 5.1's Invoke-WebRequest (what the guest
        // runner uses) mishandles chunked octet-stream responses.
        var length = new FileInfo(source).Length;
        await using var file = File.OpenRead(source);
        context.Response.StatusCode = 200;
        context.Response.ContentType = "application/octet-stream";
        context.Response.ContentLength64 = length;
        await file.CopyToAsync(context.Response.OutputStream);
        context.Response.Close();
    }

    private async Task AcceptReportAsync(HttpListenerContext context)
    {
        using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
        var json = await reader.ReadToEndAsync();
        var report = GuestRunJson.Deserialize<GuestRunReport>(json);
        if (report is not null)
        {
            Log($"report: overall={report.Overall} steps={report.Steps.Count}");
            await File.AppendAllTextAsync(
                Path.Combine(_uploadDirectory, "run-report.json"),
                json + Environment.NewLine);
            if (!string.IsNullOrEmpty(report.Overall))
            {
                _finalReport.TrySetResult(report);
            }
        }

        context.Response.StatusCode = 200;
        context.Response.Close();
    }

    private async Task AcceptUploadAsync(HttpListenerContext context)
    {
        var name = SanitizeUploadName(context.Request.QueryString["name"] ?? "results.zip");
        if (context.Request.ContentLength64 is > MaxUploadBytes)
        {
            context.Response.StatusCode = 413;
            context.Response.Close();
            return;
        }

        Directory.CreateDirectory(_uploadDirectory);
        var target = Path.Combine(_uploadDirectory, name);
        await using (var file = File.Create(target))
        {
            await context.Request.InputStream.CopyToAsync(file);
        }

        UploadedArchive = target;
        Log($"uploaded {name} ({new FileInfo(target).Length / 1024 / 1024} MiB)");
        context.Response.StatusCode = 200;
        context.Response.Close();
    }

    private static async Task RespondAsync(HttpListenerContext context, string contentType, string body)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        context.Response.StatusCode = 200;
        context.Response.ContentType = contentType;
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes);
        context.Response.Close();
    }

    /// <summary>Strips anything that could steer the upload outside the results directory.</summary>
    public static string SanitizeUploadName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "results.zip";
        }

        foreach (var c in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(c, '_');
        }

        // Collapse separators that survived the invalid-char pass.
        var file = name.Replace('/', '_').Replace('\\', '_');
        return file.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ? file : file + ".zip";
    }

    private void Log(string message) => _log?.Invoke(message);

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        _finalReport.TrySetCanceled();
        try
        {
            _listener.Stop();
            _listener.Close();
        }
        catch (ObjectDisposedException)
        {
        }
    }
}
