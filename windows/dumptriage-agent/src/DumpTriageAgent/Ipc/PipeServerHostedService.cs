using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using DumpTriage.Common.Ipc;
using DumpTriageAgent.Runtime;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DumpTriageAgent.Ipc;

/// <summary>
/// Named pipe server the per-user tray app connects to. Only one tray
/// instance is expected to be connected at a time (single interactive
/// session on a workstation), so this accepts one client, serves it until it
/// disconnects, then loops back to accept the next connection (e.g. after a
/// logoff/logon or tray app restart).
/// </summary>
public sealed class PipeServerHostedService : BackgroundService, ITrayChannel
{
    private readonly ApprovalCoordinator _approvalCoordinator;
    private readonly AgentRuntimeState _runtimeState;
    private readonly ILogger<PipeServerHostedService> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    private readonly object _writerLock = new();
    private readonly SemaphoreSlim _sendGate = new(1, 1); // StreamWriter isn't safe for concurrent writes
    private StreamWriter? _currentWriter;

    /// <summary>Fired whenever a tray (re)connects — see DumpWatcherService's use of this to re-send dropped AskApproval prompts.</summary>
    public event Action? TrayConnected;

    public bool IsInteractive => true;

    public PipeServerHostedService(
        ApprovalCoordinator approvalCoordinator,
        AgentRuntimeState runtimeState,
        ILogger<PipeServerHostedService> logger)
    {
        _approvalCoordinator = approvalCoordinator;
        _runtimeState = runtimeState;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await AcceptAndServeOneClientAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (PipeNameTakenException ex)
            {
                // FirstPipeInstance: the name still exists. Right after a restart that is usually the
                // tray's handle on the old instance, gone within seconds; for longer, another process
                // holds it (#8). Trays only talk to the service's own process, so it gets nothing from
                // them. Logged once a minute at most, so it can't flood the event log.
                _pipeTakenSince ??= DateTimeOffset.UtcNow;
                var takenFor = DateTimeOffset.UtcNow - _pipeTakenSince.Value;
                if (takenFor > TimeSpan.FromSeconds(30) && DateTimeOffset.UtcNow - _pipeTakenLoggedAt > TimeSpan.FromMinutes(1))
                {
                    _pipeTakenLoggedAt = DateTimeOffset.UtcNow;
                    _logger.LogCritical(ex.InnerException, "Another process has held the pipe {PipeName} for {Seconds:0} s; trays can't reach the service until it exits", IpcPipe.PipeName, takenFor.TotalSeconds);
                }
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Pipe server loop faulted; retrying");
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
        }
    }

    private DateTimeOffset? _pipeTakenSince;
    private DateTimeOffset _pipeTakenLoggedAt = DateTimeOffset.MinValue;

    private sealed class PipeNameTakenException(Exception inner) : Exception("pipe name taken", inner);

    private async Task AcceptAndServeOneClientAsync(CancellationToken stoppingToken)
    {
        NamedPipeServerStream created;
        try
        {
            created = CreateServerStream();
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new PipeNameTakenException(ex);
        }
        _pipeTakenSince = null;
        await using var pipeServer = created;

        _logger.LogInformation("Waiting for tray connection on pipe {PipeName}", IpcPipe.PipeName);
        await pipeServer.WaitForConnectionAsync(stoppingToken);
        _logger.LogInformation("Tray connected");

        using var reader = new StreamReader(pipeServer);
        var writer = new StreamWriter(pipeServer) { AutoFlush = true };
        lock (_writerLock) { _currentWriter = writer; }
        TrayConnected?.Invoke();

        try
        {
            while (pipeServer.IsConnected && !stoppingToken.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(stoppingToken);
                if (line is null) break; // client disconnected

                HandleIncoming(line);
            }
        }
        finally
        {
            lock (_writerLock)
            {
                if (ReferenceEquals(_currentWriter, writer)) _currentWriter = null;
            }
            _logger.LogInformation("Tray disconnected");
        }
    }

    private static NamedPipeServerStream CreateServerStream()
    {
        if (!OperatingSystem.IsWindows())
        {
            // No DACL support here; the pipe file's permissions are the umask default.
            return new NamedPipeServerStream(
                IpcPipe.PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        }

        // FirstPipeInstance: fail rather than join a pipe some other process created first (#8).
        return NamedPipeServerStreamAcl.Create(
            IpcPipe.PipeName,
            PipeDirection.InOut,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance,
            inBufferSize: 0,
            outBufferSize: 0,
            CreatePipeSecurity());
    }

    /// <summary>
    /// The default pipe DACL lets any local account connect and issue
    /// approve/submit/pause commands. Restrict it to SYSTEM and
    /// Administrators plus interactive logons (the tray runs as the signed-in
    /// user), and explicitly deny network logons.
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    internal static PipeSecurity CreatePipeSecurity()
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.InteractiveSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        return security;
    }

    private void HandleIncoming(string line)
    {
        IpcMessage? message;
        try
        {
            message = JsonSerializer.Deserialize<IpcMessage>(line, JsonOptions);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Discarding malformed IPC message from tray");
            return;
        }

        if (message is null) return;

        switch (message.Type)
        {
            case IpcMessageType.ApprovalResponse when message.DumpId is not null && message.Approved is not null:
                _approvalCoordinator.Resolve(message.DumpId, message.Approved.Value);
                break;
            case IpcMessageType.ManualSubmit when message.DumpId is not null:
                _runtimeState.RequestManualSubmit(message.DumpId);
                break;
            case IpcMessageType.PauseAgent:
                _runtimeState.Pause();
                break;
            case IpcMessageType.ResumeAgent:
                _runtimeState.Resume();
                break;
            default:
                _logger.LogWarning("Unhandled or incomplete IPC message type {Type}", message.Type);
                break;
        }
    }

    /// <summary>Best-effort: if no tray is connected, the message is silently dropped.</summary>
    public async Task SendAsync(IpcMessage message, CancellationToken cancellationToken = default)
    {
        await _sendGate.WaitAsync(cancellationToken);
        try
        {
            // Read after acquiring the gate: a tray reconnect while we queued
            // would otherwise leave us holding the old, disposed writer.
            StreamWriter? writer;
            lock (_writerLock) { writer = _currentWriter; }
            if (writer is null) return;

            var json = JsonSerializer.Serialize(message, JsonOptions);
            await writer.WriteLineAsync(json.AsMemory(), cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // Tray disconnected between our null-check and the write; harmless.
        }
        finally
        {
            _sendGate.Release();
        }
    }
}
