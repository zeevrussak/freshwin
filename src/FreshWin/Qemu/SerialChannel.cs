// FreshWin (c) Ze'ev Russak — Apache License 2.0 (see LICENSE + NOTICE).
// Derived from ZuTM's SerialConsoleSession. Expect-style driver over the
// guest's TCP serial port: background reader builds a log, WaitForAsync
// scans it for markers, SendLineAsync feeds input (serialized writes —
// concurrent NetworkStream writes corrupt the stream).

using System.Net.Sockets;
using System.Text;

namespace FreshWin.Qemu;

public sealed class SerialChannel : IAsyncDisposable
{
    private readonly TcpClient _client = new();
    private readonly NetworkStream _stream;
    private readonly CancellationTokenSource _readerCts = new();
    private readonly Task _readerTask;
    private readonly SemaphoreSlim _writeLock = new(1, 1); // NetworkStream writes are not thread-safe
    private readonly StringBuilder _output = new();
    private readonly object _outputLock = new();

    private SerialChannel(NetworkStream stream)
    {
        _stream = stream;
        _readerTask = Task.Run(() => ReadForeverAsync(_readerCts.Token));
    }

    public static async Task<SerialChannel> ConnectAsync(string host, int port, CancellationToken cancellation = default)
    {
        var client = new TcpClient();
        await client.ConnectAsync(host, port, cancellation);
        return new SerialChannel(client.GetStream());
    }

    private async Task ReadForeverAsync(CancellationToken cancellation)
    {
        var buffer = new byte[4096];
        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                var read = await _stream.ReadAsync(buffer, cancellation);
                if (read <= 0)
                {
                    return;
                }

                lock (_outputLock)
                {
                    _output.Append(Encoding.UTF8.GetString(buffer, 0, read));
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException)
        {
            // Serial goes away on guest reset/shutdown — that is normal.
        }
    }

    /// <summary>Everything received so far (snapshot).</summary>
    public string Log
    {
        get
        {
            lock (_outputLock)
            {
                return _output.ToString();
            }
        }
    }

    public async Task SendLineAsync(string line, CancellationToken cancellation = default)
    {
        var bytes = Encoding.UTF8.GetBytes(line + "\r");
        await _writeLock.WaitAsync(cancellation);
        try
        {
            await _stream.WriteAsync(bytes, cancellation);
            await _stream.FlushAsync(cancellation);
            lock (_outputLock)
            {
                _output.Append("<<< ").AppendLine(line);
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>Waits until the marker appears in received output (never matches our own echoed input, which is prefixed with "<<< ").</summary>
    public async Task WaitForAsync(string marker, TimeSpan timeout, CancellationToken cancellation = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        cts.CancelAfter(timeout);
        while (true)
        {
            if (Log.Contains(marker, StringComparison.Ordinal))
            {
                return;
            }

            await Task.Delay(250, cts.Token);
        }
    }

    public ValueTask DisposeAsync()
    {
        _readerCts.Cancel();
        _stream.Dispose();
        _client.Dispose();
        return ValueTask.CompletedTask;
    }
}
