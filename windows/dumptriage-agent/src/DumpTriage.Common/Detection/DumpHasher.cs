using System.Security.Cryptography;

namespace DumpTriage.Common.Detection;

/// <summary>
/// Computes sha256 over a dump file by streaming it, never loading the whole
/// file into memory (dumps can be tens of GB). This is purely a local queue
/// key for dedup/logging on the client side — the server computes its own
/// sha256 independently while staging the upload.
/// </summary>
public static class DumpHasher
{
    public static async Task<string> ComputeSha256Async(string filePath, CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(
            filePath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 1 << 20, useAsync: true);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
