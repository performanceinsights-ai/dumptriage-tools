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
                // Identification only: whatever serves this pipe can't act as the signed-in user (#8).
                await using var pipe = new NamedPipeClientStream(
                    ".", IpcPipe.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous,
                    System.Security.Principal.TokenImpersonationLevel.Identification);
                await pipe.ConnectAsync(5000, cancellationToken);
                if (!ServerIsTheService(pipe))
                {
                    // Some other process created the pipe first (#8): don't show its prompts or send
                    // it answers. The service logs that its pipe name is taken.
                    throw new IOException("The pipe's server is not the DumpTriage service.");
                }

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

    /// <summary>
    /// Whether the pipe's server is the DumpTriageAgent service's own process, as the Service Control
    /// Manager reports it. A process that squats the pipe name while the service restarts is not,
    /// even one in session 0 (an SSH logon, a scheduled task).
    /// </summary>
    private static bool ServerIsTheService(NamedPipeClientStream pipe)
    {
        return GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var serverPid)
            && ServiceProcessId("DumpTriageAgent") is { } servicePid
            && servicePid != 0
            && servicePid == serverPid;
    }

    private static uint? ServiceProcessId(string serviceName)
    {
        const uint ScManagerConnect = 0x0001, ServiceQueryStatus = 0x0004, ScStatusProcessInfo = 0;
        var manager = OpenSCManager(null, null, ScManagerConnect);
        if (manager == IntPtr.Zero) return null;
        try
        {
            var service = OpenService(manager, serviceName, ServiceQueryStatus);
            if (service == IntPtr.Zero) return null;
            try
            {
                var buffer = new byte[36]; // SERVICE_STATUS_PROCESS
                if (!QueryServiceStatusEx(service, ScStatusProcessInfo, buffer, buffer.Length, out _)) return null;
                return BitConverter.ToUInt32(buffer, 28); // dwProcessId
            }
            finally
            {
                CloseServiceHandle(service);
            }
        }
        finally
        {
            CloseServiceHandle(manager);
        }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeServerProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint serverProcessId);

    [System.Runtime.InteropServices.DllImport("advapi32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenSCManager(string? machineName, string? databaseName, uint desiredAccess);

    [System.Runtime.InteropServices.DllImport("advapi32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenService(IntPtr scManager, string serviceName, uint desiredAccess);

    [System.Runtime.InteropServices.DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool QueryServiceStatusEx(IntPtr service, uint infoLevel, byte[] buffer, int bufferSize, out int bytesNeeded);

    [System.Runtime.InteropServices.DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool CloseServiceHandle(IntPtr handle);

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
