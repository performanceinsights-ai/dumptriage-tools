namespace DumpTriage.Common.Config;

/// <summary>Where the agent keeps its config, queue and logs on this OS.</summary>
public sealed record AgentPaths(string ConfigPath, string DbPath, string LogDir, string ApiKeyPath)
{
    public static AgentPaths ForCurrentOs() =>
        Resolve(OperatingSystem.IsWindows(),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData));

    public static AgentPaths Resolve(bool windows, string commonAppData)
    {
        if (windows)
        {
            var dir = Path.Combine(commonAppData, "DumpTriage");
            return new AgentPaths(
                Path.Combine(dir, "config.json"),
                Path.Combine(dir, "queue.db"),
                Path.Combine(dir, "logs"),
                Path.Combine(dir, "apikey"));
        }

        // FHS layout, matching the systemd unit's StateDirectory/LogsDirectory.
        return new AgentPaths(
            "/etc/dumptriage/config.json",
            "/var/lib/dumptriage/queue.db",
            "/var/log/dumptriage",
            "/etc/dumptriage/apikey");
    }
}
