using System.Security.Cryptography;
using System.Text;
using DumpTriage.Common.Security;

namespace DumpTriageAgent.Security;

/// <summary>
/// Encrypts the API key with Windows DPAPI at machine scope (not user scope —
/// the service runs as LocalSystem with no interactive user profile). Only
/// decryptable on the same machine, which is fine: the key is provisioned
/// per-install by the installer script, not synced/backed up.
/// </summary>
public sealed class DpapiApiKeyProtector : IApiKeyProtector
{
    // Ties decrypted blobs to this app, so another machine-scope DPAPI
    // consumer's ciphertext can't be fed in and silently "work".
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("DumpTriageAgent.ApiKey.v1");

    public string Protect(string plaintextApiKey)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("DPAPI is only available on Windows.");
        }

        var plaintextBytes = Encoding.UTF8.GetBytes(plaintextApiKey);
        var encrypted = ProtectedData.Protect(plaintextBytes, Entropy, DataProtectionScope.LocalMachine);
        return Convert.ToBase64String(encrypted);
    }

    public string Unprotect(string protectedApiKey)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("DPAPI is only available on Windows.");
        }

        var encryptedBytes = Convert.FromBase64String(protectedApiKey);
        var decrypted = ProtectedData.Unprotect(encryptedBytes, Entropy, DataProtectionScope.LocalMachine);
        return Encoding.UTF8.GetString(decrypted);
    }
}
