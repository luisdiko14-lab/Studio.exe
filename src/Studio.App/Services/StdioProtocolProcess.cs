using System.Diagnostics;
using System.Text;

namespace StudioApp.Services;

/// <summary>
/// Bridges a standards-based LSP or DAP adapter that uses Content-Length framed
/// JSON messages over stdio. The WebView speaks unframed JSON via WebView2 messages.
/// </summary>
public sealed class StdioProtocolProcess : IDisposable
{
    private readonly Process _process;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _readerTask;
    private bool _disposed;

    public event Action<string>? MessageReceived;
    public event Action<string>? DiagnosticReceived;
    public event Action? Exited;

    public bool IsRunning
    {
        get { try { return !_process.HasExited; } catch { return false; } }
    }

    public int ProcessId => _process.Id;

    public StdioProtocolProcess(string executable, IEnumerable<string> arguments, string workingDirectory)
    {
        var start = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = Directory.Exists(workingDirectory) ? workingDirectory : Environment.CurrentDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        _process = new Process { StartInfo = start, EnableRaisingEvents = true };
        _process.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) DiagnosticReceived?.Invoke(e.Data); };
        _process.Exited += (_, _) => Exited?.Invoke();
        if (!_process.Start()) throw new InvalidOperationException($"Could not start protocol adapter: {executable}");
        _process.BeginErrorReadLine();
        _readerTask = Task.Run(ReadLoopAsync);
    }

    public async Task SendAsync(string json, CancellationToken cancellationToken = default)
    {
        if (!IsRunning) throw new InvalidOperationException("The language server or debug adapter is not running.");
        var payload = Encoding.UTF8.GetBytes(json);
        var header = Encoding.ASCII.GetBytes($"Content-Length: {payload.Length}\r\n\r\n");
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _process.StandardInput.BaseStream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
            await _process.StandardInput.BaseStream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
            await _process.StandardInput.BaseStream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { _writeLock.Release(); }
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            var stream = _process.StandardOutput.BaseStream;
            while (!_shutdown.IsCancellationRequested && _process.HasExited == false)
            {
                var headers = await ReadHeadersAsync(stream, _shutdown.Token).ConfigureAwait(false);
                if (headers is null) break;
                var length = ParseContentLength(headers);
                if (length < 0 || length > 64 * 1024 * 1024)
                    throw new InvalidDataException($"Invalid protocol Content-Length: {length}");
                var body = new byte[length];
                await ReadExactlyAsync(stream, body, _shutdown.Token).ConfigureAwait(false);
                MessageReceived?.Invoke(Encoding.UTF8.GetString(body));
            }
        }
        catch (OperationCanceledException) { }
        catch (EndOfStreamException) { }
        catch (Exception ex) { DiagnosticReceived?.Invoke("Protocol adapter reader: " + ex.Message); }
    }

    private static async Task<string?> ReadHeadersAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var one = new byte[1];
        var matched = 0;
        while (buffer.Length < 8192)
        {
            var read = await stream.ReadAsync(one.AsMemory(0, 1), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                if (buffer.Length == 0) return null;
                throw new EndOfStreamException("Protocol adapter closed while writing headers.");
            }
            buffer.WriteByte(one[0]);
            matched = (matched, one[0]) switch
            {
                (0, 13) => 1,
                (1, 10) => 2,
                (2, 13) => 3,
                (3, 10) => 4,
                (_, 13) => 1,
                _ => 0
            };
            if (matched == 4) return Encoding.ASCII.GetString(buffer.ToArray());
        }
        throw new InvalidDataException("Protocol message headers exceeded 8 KiB.");
    }

    private static int ParseContentLength(string headers)
    {
        foreach (var line in headers.Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = line.IndexOf(':');
            if (separator <= 0) continue;
            if (line[..separator].Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(line[(separator + 1)..].Trim(), out var length))
                return length;
        }
        throw new InvalidDataException("Protocol message has no Content-Length header.");
    }

    private static async Task ReadExactlyAsync(Stream stream, byte[] destination, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < destination.Length)
        {
            var read = await stream.ReadAsync(destination.AsMemory(offset), cancellationToken).ConfigureAwait(false);
            if (read == 0) throw new EndOfStreamException("Protocol adapter closed during a message.");
            offset += read;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _shutdown.Cancel();
        try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); } catch { }
        try { _process.Dispose(); } catch { }
        _writeLock.Dispose();
        _shutdown.Dispose();
    }
}
