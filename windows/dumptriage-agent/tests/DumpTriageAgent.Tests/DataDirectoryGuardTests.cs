using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using DumpTriageAgent.Security;

namespace DumpTriageAgent.Tests;

/// <summary>dumptriage-tools#3. Windows only (and elevated, as GitHub's Windows runners are).</summary>
public sealed class DataDirectoryGuardTests : IDisposable
{
    private static readonly string System = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null).Value;
    private static readonly string Administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null).Value;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dt-guard-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private static string[] AllowedSids(FileSystemSecurity security) =>
        security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>()
            .Select(r => r.IdentityReference.Value).Distinct().OrderBy(s => s).ToArray();

    [Fact]
    public void New_folder_is_created_locked_down()
    {
        if (!OperatingSystem.IsWindows()) return;
        var dir = Path.Combine(_root, "DumpTriage");
        Directory.CreateDirectory(_root);

        DataDirectoryGuard.Secure(dir);

        var acl = new DirectoryInfo(dir).GetAccessControl();
        Assert.True(acl.AreAccessRulesProtected);
        Assert.Equal(new[] { System, Administrators }.OrderBy(s => s), AllowedSids(acl));
        Assert.Equal(Administrators, acl.GetOwner(typeof(SecurityIdentifier))!.Value);
    }

    [Fact]
    public void Existing_contents_lose_their_own_aces_and_inherit_the_lockdown()
    {
        if (!OperatingSystem.IsWindows()) return;
        var dir = Path.Combine(_root, "DumpTriage");
        Directory.CreateDirectory(Path.Combine(dir, "logs"));
        var file = new FileInfo(Path.Combine(dir, "logs", "agent.log"));
        File.WriteAllText(file.FullName, "x");
        // As if planted with a protected DACL that lets Users read it.
        var planted = new FileSecurity();
        planted.SetAccessRuleProtection(true, false);
        planted.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), FileSystemRights.Read, AccessControlType.Allow));
        planted.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(Administrators), FileSystemRights.FullControl, AccessControlType.Allow));
        file.SetAccessControl(planted);

        DataDirectoryGuard.Secure(dir);

        var acl = file.GetAccessControl();
        Assert.False(acl.AreAccessRulesProtected);
        Assert.Equal(new[] { System, Administrators }.OrderBy(s => s), AllowedSids(acl));
    }

    [Fact]
    public void A_junction_anywhere_in_the_folder_is_refused()
    {
        if (!OperatingSystem.IsWindows()) return;
        var dir = Path.Combine(_root, "DumpTriage");
        var elsewhere = Path.Combine(_root, "elsewhere");
        Directory.CreateDirectory(dir);
        Directory.CreateDirectory(elsewhere);
        Junction(Path.Combine(dir, "logs"), elsewhere);

        var ex = Assert.Throws<UnauthorizedAccessException>(() => DataDirectoryGuard.Secure(dir));
        Assert.Contains("junction", ex.Message);
    }

    [Fact]
    public void A_folder_owned_by_another_account_is_refused()
    {
        if (!OperatingSystem.IsWindows()) return;
        var dir = Path.Combine(_root, "DumpTriage");
        Directory.CreateDirectory(dir);
        // icacls enables the restore privilege needed to hand ownership to an arbitrary account.
        Run("icacls", $"\"{dir}\" /setowner *S-1-5-32-545"); // BUILTIN\Users

        var ex = Assert.Throws<UnauthorizedAccessException>(() => DataDirectoryGuard.Secure(dir));
        Assert.Contains("owned by", ex.Message);
    }

    private static void Junction(string link, string target) => Run("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"");

    private static void Run(string file, string args)
    {
        using var p = Process.Start(new ProcessStartInfo(file, args) { RedirectStandardOutput = true, RedirectStandardError = true })!;
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, $"{file} {args}: {p.StandardOutput.ReadToEnd()} {p.StandardError.ReadToEnd()}");
    }
}
