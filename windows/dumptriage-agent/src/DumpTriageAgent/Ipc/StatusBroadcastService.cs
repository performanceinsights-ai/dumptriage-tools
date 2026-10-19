using DumpTriage.Common.Ipc;
using DumpTriage.Common.Models;
using DumpTriage.Common.Queue;
using DumpTriageAgent.Runtime;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DumpTriageAgent.Ipc;

/// <summary>Periodically pushes queue counts and pause state to the tray, if connected.</summary>
public sealed class StatusBroadcastService : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(10);

    private readonly DumpQueueStore _queueStore;
    private readonly AgentRuntimeState _runtimeState;
    private readonly ITrayChannel _pipeServer;
    private readonly ILogger<StatusBroadcastService> _logger;

    public StatusBroadcastService(
        DumpQueueStore queueStore,
        AgentRuntimeState runtimeState,
        ITrayChannel pipeServer,
        ILogger<StatusBroadcastService> logger)
    {
        _queueStore = queueStore;
        _runtimeState = runtimeState;
        _pipeServer = pipeServer;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var queued = await _queueStore.GetByStateAsync(DumpQueueState.Queued, stoppingToken);
                var pending = await _queueStore.GetByStateAsync(DumpQueueState.PendingApproval, stoppingToken);
                var failed = await _queueStore.GetByStateAsync(DumpQueueState.Failed, stoppingToken);

                await _pipeServer.SendAsync(new IpcMessage
                {
                    Type = IpcMessageType.StatusUpdate,
                    QueuedCount = queued.Count,
                    PendingApprovalCount = pending.Count,
                    FailedCount = failed.Count,
                    Paused = _runtimeState.IsPaused,
                    // Why the server stopped uploads (terms, key, closed
                    // account, quota), so the user can act on it (website#1487).
                    StatusMessage = _runtimeState.ActiveUploadHold(DateTimeOffset.UtcNow)?.Reason,
                }, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // An unhandled exception in a BackgroundService stops the
                // whole host (.NET 8 default), so a queue read error here
                // would take the agent down with it (website#1494).
                _logger.LogWarning(ex, "Status broadcast failed");
            }

            await Task.Delay(Interval, stoppingToken);
        }
    }
}
