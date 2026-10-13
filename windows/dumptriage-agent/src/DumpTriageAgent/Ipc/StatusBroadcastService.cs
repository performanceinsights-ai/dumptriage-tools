using DumpTriage.Common.Ipc;
using DumpTriage.Common.Models;
using DumpTriage.Common.Queue;
using DumpTriageAgent.Runtime;
using Microsoft.Extensions.Hosting;

namespace DumpTriageAgent.Ipc;

/// <summary>Periodically pushes queue counts and pause state to the tray, if connected.</summary>
public sealed class StatusBroadcastService : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(10);

    private readonly DumpQueueStore _queueStore;
    private readonly AgentRuntimeState _runtimeState;
    private readonly ITrayChannel _pipeServer;

    public StatusBroadcastService(DumpQueueStore queueStore, AgentRuntimeState runtimeState, ITrayChannel pipeServer)
    {
        _queueStore = queueStore;
        _runtimeState = runtimeState;
        _pipeServer = pipeServer;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var queued = await _queueStore.GetByStateAsync(DumpQueueState.Queued, stoppingToken);
            var pending = await _queueStore.GetByStateAsync(DumpQueueState.PendingApproval, stoppingToken);

            await _pipeServer.SendAsync(new IpcMessage
            {
                Type = IpcMessageType.StatusUpdate,
                QueuedCount = queued.Count,
                PendingApprovalCount = pending.Count,
                Paused = _runtimeState.IsPaused,
            }, stoppingToken);

            await Task.Delay(Interval, stoppingToken);
        }
    }
}
