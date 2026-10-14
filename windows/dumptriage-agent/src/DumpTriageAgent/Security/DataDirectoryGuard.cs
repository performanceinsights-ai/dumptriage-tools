using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace DumpTriageAgent.Security;

/// <summary>
/// Locks down %ProgramData%\DumpTriage (#3). Left to inherit ProgramData's ACL, any local user
/// could read config.json, and the machine-scope DPAPI blob in it decrypts for any process on the
/// box. Users can also create folders and files in ProgramData, so one could create DumpTriage\
/// before the install (or plant files in it under the old inherited ACL) and later rewrite
/// config.json, the queue's journal or a log file. So: the folder and everything in it must be owned
/// by SYSTEM, Administrators or a member of Administrators, and contain no junctions or symlinks.
/// Then the folder gets Administrators as owner and a protected DACL of SYSTEM and Administrators
/// only, and everything inside is reset to inherit just that.
/// </summary>
[SupportedOSPlatform("windows")]
public static class DataDirectoryGuard
{
    private static readonly SecurityIdentifier System = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);

    public static void Secure(string directory)
    {
        var info = new DirectoryInfo(directory);
        if (!info.Exists)
        {
            // Created with its owner and ACL in one call, so it never has ProgramData's. If someone
            // else created it in between, this does nothing and the checks below refuse it.
            info.Create(LockedDown());
            info.Refresh();
        }

        VetTree(info, TrustedOwners(), directory);
        info.SetAccessControl(LockedDown());
        InheritTree(info);
    }

    private static readonly EnumerationOptions AllEntries = new() { AttributesToSkip = 0, IgnoreInaccessible = false };

    // By hand rather than RecurseSubdirectories: a junction is refused before anything below it is
    // listed, so one pointing at C:\ can't make this walk the disk.
    private static void VetTree(DirectoryInfo dir, IReadOnlySet<SecurityIdentifier> trusted, string root)
    {
        Vet(dir, trusted, root);
        foreach (var entry in dir.EnumerateFileSystemInfos("*", AllEntries))
        {
            if (entry is DirectoryInfo sub) VetTree(sub, trusted, root);
            else Vet(entry, trusted, root);
        }
    }

    private static void InheritTree(DirectoryInfo dir)
    {
        foreach (var entry in dir.EnumerateFileSystemInfos("*", AllEntries))
        {
            Inherit(entry);
            if (entry is DirectoryInfo sub) InheritTree(sub);
        }
    }

    /// <summary>Owner Administrators; SYSTEM and Administrators only, inherited by everything inside.</summary>
    public static DirectorySecurity LockedDown()
    {
        var security = new DirectorySecurity();
        security.SetOwner(Administrators);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        foreach (var sid in new[] { System, Administrators })
        {
            security.AddAccessRule(new FileSystemAccessRule(
                sid, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        }
        return security;
    }

    private static void Vet(FileSystemInfo entry, IReadOnlySet<SecurityIdentifier> trusted, string root)
    {
        if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new UnauthorizedAccessException(
                $"{entry.FullName} is a junction or symlink. Delete {root} and reinstall the agent.");
        }

        var owner = OwnerOf(entry);
        if (owner is null || !trusted.Contains(owner))
        {
            throw new UnauthorizedAccessException(
                $"{entry.FullName} is owned by {Describe(owner)}, not SYSTEM or an administrator, so another " +
                $"account could have created it or changed it. Delete {root} and reinstall the agent.");
        }
    }

    private static void Inherit(FileSystemInfo entry)
    {
        // Owner Administrators, no explicit or protected ACEs: only what the folder hands down.
        if (entry is DirectoryInfo dir)
        {
            var security = new DirectorySecurity();
            security.SetOwner(Administrators);
            security.SetAccessRuleProtection(isProtected: false, preserveInheritance: false);
            dir.SetAccessControl(security);
        }
        else if (entry is FileInfo file)
        {
            var security = new FileSecurity();
            security.SetOwner(Administrators);
            security.SetAccessRuleProtection(isProtected: false, preserveInheritance: false);
            file.SetAccessControl(security);
        }
    }

    private static SecurityIdentifier? OwnerOf(FileSystemInfo entry) => entry switch
    {
        DirectoryInfo d => d.GetAccessControl(AccessControlSections.Owner).GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier,
        FileInfo f => f.GetAccessControl(AccessControlSections.Owner).GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier,
        _ => null,
    };

    /// <summary>
    /// SYSTEM, Administrators, and the direct members of the local Administrators group: with
    /// "default owner = object creator" in effect, a folder an admin created (an older install's
    /// --set-api-key-stdin, or a config.json written by hand) is owned by that admin's own account.
    /// </summary>
    public static IReadOnlySet<SecurityIdentifier> TrustedOwners()
    {
        var trusted = new HashSet<SecurityIdentifier> { System, Administrators };
        foreach (var member in LocalAdministratorMembers())
        {
            trusted.Add(member);
        }
        return trusted;
    }

    private static IEnumerable<SecurityIdentifier> LocalAdministratorMembers()
    {
        string groupName;
        try
        {
            groupName = ((NTAccount)Administrators.Translate(typeof(NTAccount))).Value.Split('\\')[^1];
        }
        catch (Exception)
        {
            yield break;
        }

        var status = NetLocalGroupGetMembers(null, groupName, 0, out var buffer, -1, out var read, out _, IntPtr.Zero);
        if (status != 0 || buffer == IntPtr.Zero) yield break;
        try
        {
            var size = Marshal.SizeOf<IntPtr>();
            for (var i = 0; i < read; i++)
            {
                var sidPointer = Marshal.ReadIntPtr(buffer, i * size);
                yield return new SecurityIdentifier(sidPointer);
            }
        }
        finally
        {
            NetApiBufferFree(buffer);
        }
    }

    private static string Describe(SecurityIdentifier? sid)
    {
        if (sid is null) return "an unknown account";
        try { return $"{sid.Translate(typeof(NTAccount))} ({sid})"; }
        catch (Exception) { return sid.Value; } // unmapped, or the domain can't be reached
    }

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int NetLocalGroupGetMembers(
        string? serverName, string localGroupName, int level, out IntPtr buffer, int prefMaxLength,
        out int entriesRead, out int totalEntries, IntPtr resumeHandle);

    [DllImport("netapi32.dll")]
    private static extern int NetApiBufferFree(IntPtr buffer);
}
