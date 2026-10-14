using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace DumpTriageAgent.Security;

/// <summary>
/// Locks down %ProgramData%\DumpTriage (#3). Left to inherit ProgramData's ACL, any local user
/// could read config.json, and the machine-scope DPAPI blob in it decrypts for any process on the
/// box. Users can also create folders in ProgramData, so one could create DumpTriage\ before the
/// install, own it, and later rewrite config.json (apiBaseUrl, watchedPaths) to make the SYSTEM
/// service send its key and dumps elsewhere. So: refuse a folder that SYSTEM or Administrators don't
/// own, or that is a reparse point (junction), and give it a protected DACL of SYSTEM and
/// Administrators only, inherited by everything inside.
/// </summary>
[SupportedOSPlatform("windows")]
public static class DataDirectoryGuard
{
    private static readonly SecurityIdentifier System = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);

    public static void Secure(string directory)
    {
        var info = new DirectoryInfo(directory);
        if (info.Exists)
        {
            if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw new UnauthorizedAccessException(
                    $"{directory} is a junction or symlink. Delete it and reinstall the agent.");
            }

            var owner = info.GetAccessControl(AccessControlSections.Owner).GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
            if (owner is null || !(owner.Equals(System) || owner.Equals(Administrators)))
            {
                throw new UnauthorizedAccessException(
                    $"{directory} is owned by {Describe(owner)}, not SYSTEM or Administrators, so another account " +
                    "could have created it or changed its contents. Delete it and reinstall the agent.");
            }
        }
        else
        {
            info.Create();
        }

        info.SetAccessControl(LockedDown());
    }

    /// <summary>SYSTEM and Administrators only, inherited by every file and folder inside.</summary>
    public static DirectorySecurity LockedDown()
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        foreach (var sid in new[] { System, Administrators })
        {
            security.AddAccessRule(new FileSystemAccessRule(
                sid, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        }
        return security;
    }

    private static string Describe(SecurityIdentifier? sid)
    {
        if (sid is null) return "an unknown account";
        try { return $"{sid.Translate(typeof(NTAccount))} ({sid})"; }
        catch (IdentityNotMappedException) { return sid.Value; }
    }
}
