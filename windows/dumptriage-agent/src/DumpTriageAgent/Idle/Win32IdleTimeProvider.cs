using System.Runtime.InteropServices;
using DumpTriage.Common.Idle;

namespace DumpTriageAgent.Idle;

/// <summary>
/// Backs BatchingConfig's Idle mode: don't compete with the user's network
/// for bandwidth right after a crash — wait until they've stepped away.
/// </summary>
public sealed class Win32IdleTimeProvider : IIdleTimeProvider
{
    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

    public TimeSpan GetIdleDuration()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("GetLastInputInfo is only available on Windows.");
        }

        var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        if (!GetLastInputInfo(ref info))
        {
            // Can't determine idle time (rare) — treat as "not idle" so we
            // never accidentally flush a batch during active use.
            return TimeSpan.Zero;
        }

        var idleTicks = (uint)Environment.TickCount - info.dwTime;
        return TimeSpan.FromMilliseconds(idleTicks);
    }
}
