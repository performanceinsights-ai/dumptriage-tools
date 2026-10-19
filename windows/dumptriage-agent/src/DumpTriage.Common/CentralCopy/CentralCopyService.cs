using DumpTriage.Common.Config;
using DumpTriage.Common.Models;

namespace DumpTriage.Common.CentralCopy;

/// <summary>
/// Best-effort archival copy to a shared location (see CentralCopyConfig).
/// Streams rather than using File.Copy so a multi-GB kernel dump doesn't
/// block the calling async pipeline, and never throws except on
/// cancellation — a share being offline shouldn't affect submission, only
/// get logged.
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

        string? tempPath = null;
        try
        {
            var destinationDir = config.OrganizeByHostname
                ? Path.Combine(config.DestinationPath, SanitizeForPath(machineName))
                : config.DestinationPath;

            Directory.CreateDirectory(destinationDir);

            var destinationPath = ResolveNonClashingDestination(destinationDir, record);

            // Copied under a temporary name and renamed once complete, so a
            // copy cut off half-way never sits at the final name, where it
            // clashed with every retry (website#1490).
            tempPath = $"{destinationPath}.{Guid.NewGuid():N}.tmp";
            await using (var source = new FileStream(
                record.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, useAsync: true))
            await using (var destination = new FileStream(
                tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize, useAsync: true))
            {
                await source.CopyToAsync(destination, BufferSize, cancellationToken);
            }

            File.Move(tempPath, destinationPath, overwrite: false);
            tempPath = null;
            return (true, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Share offline, permissions, a bad DestinationPath, a path too
            // long — anything: it must never touch submission (website#1490).
            return (false, ex.Message);
        }
        finally
        {
            if (tempPath is not null)
            {
                TryDelete(tempPath);
            }
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception)
        {
            // Best effort: the share may have gone away mid-copy.
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

    private static string SanitizeForPath(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(value.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
    }
}
