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
    private readonly ToolStripMenuItem _submitMenu;
    private readonly ToolStripMenuItem _retryFailedItem;
    private bool _paused;
    private string? _uploadHoldMessage;

    // One approval popup at a time (#12): the rest wait here, each dump once. A re-sent ask (after a
    // service restart) replaces the waiting one, since it carries the service's new deadline.
    private readonly Queue<string> _askOrder = new();
    private readonly Dictionary<string, (IpcMessage Message, DateTime ReceivedUtc)> _waitingAsks = new();
    private string? _askShowingId;

    // NotifyOnly dumps already announced with a balloon in this session: re-sent on every reconnect,
    // but only new ones are announced (#7).
    private readonly HashSet<string> _announcedManual = new();

    public TrayApplicationContext()
    {
        _uiContext = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();

        var menu = new ContextMenuStrip();
        // NotifyOnly dumps wait here until the user submits them (#7).
        _submitMenu = new ToolStripMenuItem("Submit dump") { Visible = false };
        menu.Items.Add(_submitMenu);
        _pauseResumeItem = new ToolStripMenuItem("Pause", null, OnPauseResumeClicked);
        menu.Items.Add(_pauseResumeItem);
        // The only way back from Failed (website#1488): enabled while any dump is.
        _retryFailedItem = new ToolStripMenuItem("Retry failed uploads", null, OnRetryFailedClicked) { Enabled = false };
        menu.Items.Add(_retryFailedItem);
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
            if (connected)
            {
                // The service re-sends everything still pending on every connect; start the list afresh.
                var old = _submitMenu.DropDownItems.Cast<ToolStripItem>().ToList();
                _submitMenu.DropDownItems.Clear();
                old.ForEach(item => item.Dispose());
                _submitMenu.Visible = false;
            }
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

        // ManualOnly mode (no AskTimeoutSeconds set): passive notification, and
        // the dump is listed under "Submit dump" in the tray menu.
        if (message.AskTimeoutSeconds is null)
        {
            if (AddManualSubmitItem(message) && _announcedManual.Add(message.DumpId))
            {
                _notifyIcon.ShowBalloonTip(
                    10_000,
                    "DumpTriage",
                    $"New dump detected: {message.DumpFileName}. Right-click the tray icon and choose Submit dump to send it.",
                    ToolTipIcon.Info);
            }
            return;
        }

        if (message.DumpId == _askShowingId) return; // its popup is open; its answer goes out when it closes
        if (!_waitingAsks.ContainsKey(message.DumpId)) _askOrder.Enqueue(message.DumpId);
        _waitingAsks[message.DumpId] = (message, DateTime.UtcNow);
        ShowNextAsk();
    }

    /// <summary>
    /// Shows queued approval popups one after another. Each answer is sent the moment its popup
    /// closes, instead of a nested modal holding it back until the ones above it close (#12).
    /// </summary>
    private void ShowNextAsk()
    {
        if (_askShowingId is not null) return;
        try
        {
            while (_askOrder.TryDequeue(out var id))
            {
                if (!_waitingAsks.Remove(id, out var next)) continue;
                var message = next.Message;
                // The service's wait ran on while this one waited its turn: show only what is left.
                var remaining = (message.AskTimeoutSeconds ?? 0) - (int)(DateTime.UtcNow - next.ReceivedUtc).TotalSeconds;
                if (remaining <= 0)
                {
                    continue; // the service has already applied its timeout action
                }

                _askShowingId = id;

                using (var popup = new ApprovalPopupForm(
                    message.DumpFileName ?? "dump file",
                    message.DumpSizeBytes ?? 0,
                    message.DumpType?.ToString() ?? "unknown",
                    remaining))
                {
                    popup.ShowDialog();
                    _ = _pipeClient.SendAsync(new IpcMessage
                    {
                        Type = IpcMessageType.ApprovalResponse,
                        DumpId = message.DumpId,
                        Approved = popup.Result ?? false,
                    });
                }
                _askShowingId = null;
            }
        }
        finally
        {
            _askShowingId = null;
        }
    }

    private bool AddManualSubmitItem(IpcMessage message)
    {
        foreach (ToolStripItem existing in _submitMenu.DropDownItems)
        {
            if (Equals(existing.Tag, message.DumpId)) return false; // already listed (re-sent on reconnect)
        }

        var item = new ToolStripMenuItem($"{message.DumpFileName} ({(message.DumpSizeBytes ?? 0) / (1024 * 1024)} MB)")
        {
            Tag = message.DumpId,
        };
        item.Click += (_, _) =>
        {
            _ = _pipeClient.SendAsync(new IpcMessage { Type = IpcMessageType.ManualSubmit, DumpId = message.DumpId });
            _submitMenu.DropDownItems.Remove(item);
            _submitMenu.Visible = _submitMenu.DropDownItems.Count > 0;
        };
        _submitMenu.DropDownItems.Add(item);
        _submitMenu.Visible = true;
        return true;
    }

    private void HandleStatusUpdate(IpcMessage message)
    {
        if (message.Paused is { } paused)
        {
            _paused = paused;
            _pauseResumeItem.Text = _paused ? "Resume" : "Pause";
        }

        if (message.FailedCount is { } failed)
        {
            _retryFailedItem.Enabled = failed > 0;
            _retryFailedItem.Text = failed > 0 ? $"Retry failed uploads ({failed})" : "Retry failed uploads";
        }

        // StatusMessage on a StatusUpdate: the server stopped uploads for the
        // whole queue (terms, key, closed account, quota) — website#1487.
        if (message.StatusMessage != _uploadHoldMessage)
        {
            _uploadHoldMessage = message.StatusMessage;
            if (_uploadHoldMessage is not null)
            {
                _notifyIcon.ShowBalloonTip(10_000, "DumpTriage", _uploadHoldMessage, ToolTipIcon.Warning);
            }
        }

        if (_uploadHoldMessage is not null)
        {
            _notifyIcon.Text = TrayText(_uploadHoldMessage);
        }
        else if (message.QueuedCount is { } queued && queued > 0)
        {
            _notifyIcon.Text = $"DumpTriage Agent — {queued} queued";
        }
        else
        {
            // A hold that ended with nothing queued must not leave its text behind.
            _notifyIcon.Text = "DumpTriage Agent — connected";
        }
    }

    // NotifyIcon.Text throws past 127 characters.
    private static string TrayText(string text) => text.Length <= 127 ? text : text[..126] + "…";

    private void OnRetryFailedClicked(object? sender, EventArgs e)
    {
        _retryFailedItem.Enabled = false; // until the next status update says otherwise
        _ = _pipeClient.SendAsync(new IpcMessage { Type = IpcMessageType.RetryFailed });
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
