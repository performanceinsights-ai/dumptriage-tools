using DumpTriage.Common.Ipc;
using DumpTriageTray.Ipc;

namespace DumpTriageTray;

/// <summary>
/// Owns the tray icon and reacts to messages from the service: shows the
/// approval popup for AskApproval, a passive balloon tip for status changes,
/// and relays Pause/Resume from the context menu back to the service.
/// </summary>
public sealed class TrayApplicationContext : ApplicationContext
{
    private readonly NotifyIcon _notifyIcon;
    private readonly PipeClient _pipeClient = new();
    private readonly SynchronizationContext _uiContext;
    private readonly ToolStripMenuItem _pauseResumeItem;
    private bool _paused;

    public TrayApplicationContext()
    {
        _uiContext = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();

        var menu = new ContextMenuStrip();
        _pauseResumeItem = new ToolStripMenuItem("Pause", null, OnPauseResumeClicked);
        menu.Items.Add(_pauseResumeItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Exit", null, (_, _) => ExitThread()));

        _notifyIcon = new NotifyIcon
        {
            Icon = SystemIcons.Application, // TODO: replace with branded tray.ico once available
            Text = "DumpTriage Agent",
            ContextMenuStrip = menu,
            Visible = true,
        };

        _pipeClient.MessageReceived += OnMessageReceived;
        _pipeClient.ConnectionStateChanged += OnConnectionStateChanged;
        _pipeClient.Start();
    }

    private void OnConnectionStateChanged(bool connected)
    {
        _uiContext.Post(_ =>
        {
            _notifyIcon.Text = connected ? "DumpTriage Agent — connected" : "DumpTriage Agent — service unavailable";
        }, null);
    }

    private void OnMessageReceived(IpcMessage message)
    {
        switch (message.Type)
        {
            case IpcMessageType.AskApproval:
                _uiContext.Post(_ => HandleAskApproval(message), null);
                break;
            case IpcMessageType.StatusUpdate:
                _uiContext.Post(_ => HandleStatusUpdate(message), null);
                break;
        }
    }

    private void HandleAskApproval(IpcMessage message)
    {
        if (message.DumpId is null) return;

        // ManualOnly mode (no AskTimeoutSeconds set): passive notification,
        // submission happens later from the queue view, not from this popup.
        if (message.AskTimeoutSeconds is null)
        {
            _notifyIcon.ShowBalloonTip(
                10_000,
                "DumpTriage",
                $"New dump detected: {message.DumpFileName}. Right-click the tray icon to submit it.",
                ToolTipIcon.Info);
            return;
        }

        using var popup = new ApprovalPopupForm(
            message.DumpFileName ?? "dump file",
            message.DumpSizeBytes ?? 0,
            message.DumpType?.ToString() ?? "unknown",
            message.AskTimeoutSeconds.Value);

        popup.ShowDialog();

        _ = _pipeClient.SendAsync(new IpcMessage
        {
            Type = IpcMessageType.ApprovalResponse,
            DumpId = message.DumpId,
            Approved = popup.Result ?? false,
        });
    }

    private void HandleStatusUpdate(IpcMessage message)
    {
        if (message.Paused is { } paused)
        {
            _paused = paused;
            _pauseResumeItem.Text = _paused ? "Resume" : "Pause";
        }

        if (message.QueuedCount is { } queued && queued > 0)
        {
            _notifyIcon.Text = $"DumpTriage Agent — {queued} queued";
        }
    }

    private void OnPauseResumeClicked(object? sender, EventArgs e)
    {
        _paused = !_paused;
        _pauseResumeItem.Text = _paused ? "Resume" : "Pause";
        _ = _pipeClient.SendAsync(new IpcMessage { Type = _paused ? IpcMessageType.PauseAgent : IpcMessageType.ResumeAgent });
    }

    protected override void ExitThreadCore()
    {
        _pipeClient.Dispose();
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        base.ExitThreadCore();
    }
}
