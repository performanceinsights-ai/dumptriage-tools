using DumpTriage.Common.Config;
using DumpTriage.Common.Models;

namespace DumpTriage.Common.CentralCopy;

/// <summary>
/// Best-effort archival copy to a shared location (see CentralCopyConfig).
/// Streams rather than using File.Copy so a multi-GB kernel dump doesn't
/// block the calling async pipeline, and never throws — a share being
/// offline shouldn't affect submission, only get logged.
/// </summary>
public static class CentralCopyService
{
    private const int BufferSize = 1 << 20;

    public static async Task<(bool Success, string? Error)> TryCopyAsync(
        DumpRecord record,
        CentralCopyConfig config,
        string machineName,
        CancellationToken cancellationToken = default)
    {
        if (!config.Enabled)
        {
            return (false, null);
        }

        if (string.IsNullOrWhiteSpace(config.DestinationPath))
        {
            return (false, "CentralCopy is enabled but no DestinationPath is configured");
        }

        try
        {
            var destinationDir = config.OrganizeByHostname
                ? Path.Combine(config.DestinationPath, SanitizeForPath(machineName))
                : config.DestinationPath;

            Directory.CreateDirectory(destinationDir);

            var destinationPath = ResolveNonClashingDestination(destinationDir, record);

            await using var source = new FileStream(
                record.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, useAsync: true);
            await using var destination = new FileStream(
                destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize, useAsync: true);

            try
            {
                await source.CopyToAsync(destination, BufferSize, cancellationToken);
            }
            catch
            {
                // Don't leave a truncated archival copy behind: it would look
                // like a valid file and force the retry onto a suffixed name.
                await destination.DisposeAsync();
                TryDelete(destinationPath);
                throw;
            }

            return (true, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            // Share offline, permissions issue, etc. — not fatal to submission.
            return (false, ex.Message);
        }
    }

    /// <summary>
    /// Two different dumps can share a file name (e.g. two machines both hit
    /// MEMORY.DMP). Disambiguate with the dump's own id (sha256) rather than
    /// silently overwriting a prior archival copy.
    /// </summary>
    private static string ResolveNonClashingDestination(string destinationDir, DumpRecord record)
    {
        var fileName = Path.GetFileName(record.FilePath);
        var candidate = Path.Combine(destinationDir, fileName);
        if (!File.Exists(candidate))
        {
            return candidate;
        }

        var shortId = record.Id.Length > 8 ? record.Id[..8] : record.Id;
        var nameWithoutExt = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        return Path.Combine(destinationDir, $"{nameWithoutExt}.{shortId}{ext}");
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static string SanitizeForPath(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(value.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
    }
}
