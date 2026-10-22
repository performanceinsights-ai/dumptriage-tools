using DumpTriage.Common.Policy;

namespace DumpTriage.Common.Config;

public sealed class AgentConfig
{
    public string ApiBaseUrl { get; init; } = "https://api.performanceinsights.ai";

    /// <summary>Protected API key: a DPAPI blob (base64) on Windows, or a `file:` reference to a root-only key file on Linux. See Security/IApiKeyProtector.</summary>
    public string? ProtectedApiKey { get; set; }

    /// <summary>
    /// Whether to send the machine's hostname (hashed server-side, never
    /// stored raw — see the "hostname" field in DumpTriageApiClient).
    /// </summary>
    public bool SendHostname { get; init; } = true;

    public List<string> WatchedPaths { get; init; } = DefaultWatchedPaths();

    /// <summary>File-name filters applied inside each watched path.</summary>
    public List<string> WatchedFilters { get; init; } = DefaultWatchedFilters();

    public static List<string> DefaultWatchedPaths() => OperatingSystem.IsWindows()
        ? new() { "%LOCALAPPDATA%\\CrashDumps", "%WINDIR%\\Minidump", "%WINDIR%" }
        : new() { "/var/crash", "/var/lib/dumptriage/incoming" };

    // Linux file systems are case-sensitive, so spell out both cases there.
    public static List<string> DefaultWatchedFilters() => OperatingSystem.IsWindows()
        ? new() { "*.dmp", "MEMORY.DMP" }
        : new() { "*.dmp", "*.DMP" };

    /// <summary>
    /// On start, dumps already in the watched folders that were last written
    /// within this many days are looked at too (website#1514): a dump written while
    /// the service was stopped, or a MEMORY.DMP Windows wrote early in the boot
    /// after a crash, raises no watcher event. Ones already in the queue are
    /// skipped cheaply. The cutoff keeps a first install from offering years
    /// of old dumps; 0 turns the scan off.
    /// </summary>
    public int StartupScanMaxAgeDays { get; init; } = 30;

    public AgentPolicyConfig Policy { get; init; } = new();

    public CentralCopyConfig CentralCopy { get; init; } = new();

    /// <summary>Performance traces with WPR (website#1999, dumptriage-tools#29). Off by default.</summary>
    public Tracing.TracingConfig Tracing { get; init; } = new();
}

/// <summary>
/// Optional archival copy: every dump approved for submission is also copied
/// to a shared location (typically a UNC path like \\fileserver\dumps),
/// independent of whether the API upload itself succeeds — this is a plain
/// file copy, not a substitute for submission, so IT can keep its own
/// retention copy without relying on the DumpTriage backend.
/// </summary>
public sealed class CentralCopyConfig
{
    public bool Enabled { get; init; } = false;

    /// <summary>
    /// Destination root. Works equally for a UNC share (\\fileserver\dumps)
    /// or a local/mapped-drive folder — the agent doesn't need to
    /// distinguish, it's just a path File.Copy can reach.
    /// </summary>
    public string? DestinationPath { get; init; }

    /// <summary>Group copies under DestinationPath\&lt;hostname&gt;\ rather than dumping everything in one flat folder.</summary>
    public bool OrganizeByHostname { get; init; } = true;
}
