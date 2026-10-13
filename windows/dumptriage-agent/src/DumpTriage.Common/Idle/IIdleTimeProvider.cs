namespace DumpTriage.Common.Idle;

/// <summary>
/// How long since the last user input (keyboard/mouse) on this machine.
/// Backs BatchScheduler's Idle mode. The real implementation
/// (Win32IdleTimeProvider, in the service project) P/Invokes
/// GetLastInputInfo; this interface lives in Common so BatchScheduler and its
/// tests don't depend on a Windows-only type.
/// </summary>
public interface IIdleTimeProvider
{
    TimeSpan GetIdleDuration();
}
