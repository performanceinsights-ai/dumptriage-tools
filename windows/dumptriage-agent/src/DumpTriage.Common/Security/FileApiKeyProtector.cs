using System.Text;

namespace DumpTriage.Common.Security;

/// <summary>
/// Linux counterpart to DPAPI, which doesn't exist there. The key lives in a
/// separate file created with mode 0600 (owner-only), and config.json holds
/// only a `file:` reference, so the config itself can be world-readable
/// without leaking the credential.
/// </summary>
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public sealed class FileApiKeyProtector : IApiKeyProtector
{
    private const string Prefix = "file:";
    private readonly string _keyPath;

    public FileApiKeyProtector(string keyPath)
    {
        _keyPath = keyPath;
    }

    public string Protect(string plaintextApiKey)
    {
        var dir = Path.GetDirectoryName(_keyPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        // Replace rather than overwrite: an existing file may have looser
        // permissions, and the secret must never be written into it.
        File.Delete(_keyPath);

        // Create the file owner-only *before* writing the secret into it.
        using (var stream = new FileStream(_keyPath, new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
        }))
        {
            stream.Write(Encoding.UTF8.GetBytes(plaintextApiKey));
        }

        return Prefix + _keyPath;
    }

    public string Unprotect(string protectedApiKey)
    {
        if (!protectedApiKey.StartsWith(Prefix, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Unrecognised API key reference; re-run --set-api-key-stdin.");
        }

        return File.ReadAllText(protectedApiKey[Prefix.Length..]).Trim();
    }
}
