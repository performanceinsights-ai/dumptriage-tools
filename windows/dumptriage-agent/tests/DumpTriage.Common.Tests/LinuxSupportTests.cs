using DumpTriage.Common.Config;
using DumpTriage.Common.Security;

namespace DumpTriage.Common.Tests;

public class LinuxSupportTests
{
    [Fact]
    public void AgentPaths_WindowsUsesProgramData()
    {
        var p = AgentPaths.Resolve(windows: true, commonAppData: @"C:\ProgramData");
        Assert.Equal(Path.Combine(@"C:\ProgramData", "DumpTriage", "config.json"), p.ConfigPath);
    }

    [Fact]
    public void AgentPaths_LinuxUsesFhsLayout()
    {
        var p = AgentPaths.Resolve(windows: false, commonAppData: "/usr/share");
        Assert.Equal("/etc/dumptriage/config.json", p.ConfigPath);
        Assert.Equal("/var/lib/dumptriage/queue.db", p.DbPath);
        Assert.Equal("/var/log/dumptriage", p.LogDir);
    }

    [Fact]
    public void FileApiKeyProtector_RoundTripsAndKeepsKeyOwnerOnly()
    {
        if (OperatingSystem.IsWindows()) return;

        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var keyPath = Path.Combine(dir, "apikey");
        try
        {
            var protector = new FileApiKeyProtector(keyPath);
            var reference = protector.Protect("dts_secret");

            Assert.DoesNotContain("dts_secret", reference);
            Assert.Equal("dts_secret", protector.Unprotect(reference));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(keyPath));

            // Re-provisioning over a looser existing file tightens it again.
            File.SetUnixFileMode(keyPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherRead);
            protector.Protect("dts_new");
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(keyPath));
            Assert.Equal("dts_new", protector.Unprotect(reference));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void FileApiKeyProtector_RejectsForeignBlob()
    {
        if (OperatingSystem.IsWindows()) return;
        Assert.Throws<InvalidOperationException>(() => new FileApiKeyProtector("/nonexistent").Unprotect("AQAAANCM..."));
    }

    [Fact]
    public void DefaultWatchedPaths_AreOsAppropriate()
    {
        var paths = AgentConfig.DefaultWatchedPaths();
        Assert.Equal(OperatingSystem.IsWindows(), paths.Any(x => x.Contains("CrashDumps")));
        Assert.NotEmpty(AgentConfig.DefaultWatchedFilters());
    }
}
