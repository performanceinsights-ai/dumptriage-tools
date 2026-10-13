using System.IO.Pipes;
using System.Text.Json;
using DumpTriage.Common.Ipc;

namespace DumpTriageTray.Ipc;

/// <summary>
/// Connects to the service's named pipe and auto-reconnects if the service
/// restarts or the tray starts before the service is ready. Raises events on
/// the thread that owns the connection loop — callers marshal to the UI
/// thread themselves (see TrayApplicationContext).
/// </summary>
public sealed class PipeClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    private readonly CancellationTokenSource _cts = new();
    private readonly object _writerLock = new();
    private StreamWriter? _writer;
    private Task? _connectionLoop;

    public event Action<IpcMessage>? MessageReceived;
    public event Action<bool>? ConnectionStateChanged; // true = connected

    public void Start()
    {
        _connectionLoop = Task.Run(() => RunConnectionLoopAsync(_cts.Token));
    }

    private async Task RunConnectionLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeClientStream(
                    ".", IpcPipe.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                await pipe.ConnectAsync(5000, cancellationToken);

                using var reader = new StreamReader(pipe);
                var writer = new StreamWriter(pipe) { AutoFlush = true };
                lock (_writerLock) { _writer = writer; }
                ConnectionStateChanged?.Invoke(true);

                while (pipe.IsConnected && !cancellationToken.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync(cancellationToken);
                    if (line is null) break;

                    try
                    {
                        var message = JsonSerializer.Deserialize<IpcMessage>(line, JsonOptions);
                        if (message is not null) MessageReceived?.Invoke(message);
                    }
                    catch (JsonException)
                    {
                        // Ignore malformed line, keep the connection alive.
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception)
            {
                // Service not running yet / pipe busy / connection dropped — retry below.
            }
            finally
            {
                lock (_writerLock) { _writer = null; }
                ConnectionStateChanged?.Invoke(false);
            }

            if (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken).ContinueWith(_ => { });
            }
        }
    }

    public async Task SendAsync(IpcMessage message)
    {
        StreamWriter? writer;
        lock (_writerLock) { writer = _writer; }
        if (writer is null) return;

        try
        {
            var json = JsonSerializer.Serialize(message, JsonOptions);
            await writer.WriteLineAsync(json);
        }
        catch (IOException)
        {
            // Service disconnected between the null-check and the write; the
            // connection loop will notice and reconnect.
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _connectionLoop?.Wait(TimeSpan.FromSeconds(2)); } catch { /* best effort */ }
        _cts.Dispose();
    }
}
