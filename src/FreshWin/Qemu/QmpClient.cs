// FreshWin (c) Ze'ev Russak — Apache License 2.0 (see LICENSE + NOTICE).
// Derived from ZuTM's QmpClient. Minimal QMP-over-TCP client: greeting +
// qmp_capabilities handshake, id-correlated command execution. Enough for
// status queries, screendump and powerdown — FreshWin does not need events.

using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace FreshWin.Qemu;

public sealed class QmpClient : IAsyncDisposable
{
    private readonly TcpClient _client = new();
    private readonly NetworkStream _stream;
    private int _nextId;

    private QmpClient(NetworkStream stream) => _stream = stream;

    public static async Task<QmpClient> ConnectAsync(string host, int port, CancellationToken cancellation = default)
    {
        var client = new TcpClient();
        await client.ConnectAsync(host, port, cancellation);
        var qmp = new QmpClient(client.GetStream());
        await qmp.ReadLineAsync(cancellation); // greeting
        await qmp.ExecuteAsync("qmp_capabilities", cancellation: cancellation);
        return qmp;
    }

    /// <summary>Executes a QMP command; throws with the error message on a QMP error.</summary>
    public async Task<JsonElement> ExecuteAsync(
        string command,
        object? arguments = null,
        CancellationToken cancellation = default)
    {
        var id = Interlocked.Increment(ref _nextId);
        var payload = arguments is null
            ? $"{{\"execute\":\"{command}\",\"id\":{id}}}"
            : JsonSerializer.Serialize(arguments, JsonOptions) ;

        // Splice the id in (arguments payloads are plain JSON objects).
        if (arguments is not null)
        {
            payload = payload.TrimEnd('}') + $",\"id\":{id}}}";
        }

        var bytes = Encoding.UTF8.GetBytes(payload);
        await _stream.WriteAsync(bytes, cancellation);
        await _stream.FlushAsync(cancellation);

        while (true)
        {
            var line = await ReadLineAsync(cancellation);
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.TryGetProperty("id", out var idElement) && idElement.GetInt32() == id)
            {
                if (root.TryGetProperty("error", out var error))
                {
                    var description = error.TryGetProperty("desc", out var desc) ? desc.GetString() : error.ToString();
                    throw new InvalidOperationException($"QMP {command} failed: {description}");
                }

                return root.GetProperty("return").Clone();
            }

            // Events or asynchronous messages — ignore.
        }
    }

    private async Task<string> ReadLineAsync(CancellationToken cancellation)
    {
        var buffer = new byte[1];
        var line = new StringBuilder();
        while (true)
        {
            var read = await _stream.ReadAsync(buffer, cancellation);
            if (read <= 0)
            {
                throw new EndOfStreamException("QMP connection closed");
            }

            var c = (char)buffer[0];
            if (c == '\n')
            {
                return line.ToString();
            }

            if (c != '\r')
            {
                line.Append(c);
            }
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public ValueTask DisposeAsync()
    {
        _stream.Dispose();
        _client.Dispose();
        return ValueTask.CompletedTask;
    }
}
