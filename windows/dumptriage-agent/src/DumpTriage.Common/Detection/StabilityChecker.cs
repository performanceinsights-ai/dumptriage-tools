namespace DumpTriage.Common.Detection;

/// <summary>
/// FileSystemWatcher fires as soon as a file is created, well before Windows
/// (or WER) finishes writing it — uploading at that point would send a
/// truncated dump. This waits until the file's size has stopped changing and
/// it can be opened exclusively (no writer still holds it open) before
/// treating it as ready to read.
/// </summary>
public static class StabilityChecker
{
    public static Task<bool> WaitUntilStableAsync(
        string filePath,
        TimeSpan pollInterval,
        TimeSpan quietPeriod,
        TimeSpan timeout,
        CancellationToken cancellationToken = default) =>
        WaitUntilStableAsync(filePath, pollInterval, quietPeriod, timeout, OpenForCheck, cancellationToken);

    private static IDisposable OpenForCheck(string filePath) =>
        new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);

    /// <param name="open">Opens the file the way the upload will; a test seam.</param>
    internal static async Task<bool> WaitUntilStableAsync(
        string filePath,
        TimeSpan pollInterval,
        TimeSpan quietPeriod,
        TimeSpan timeout,
        Func<string, IDisposable> open,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        long lastSize = -1;
        DateTimeOffset lastChangeUtc = DateTimeOffset.UtcNow;

        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!File.Exists(filePath))
            {
                // Some dump-writing tools create-then-rename; treat a
                // disappearance mid-check as "not stable yet" and keep polling
                // until the timeout, rather than failing immediately.
                await Task.Delay(pollInterval, cancellationToken);
                continue;
            }

            long currentSize;
            try
            {
                currentSize = new FileInfo(filePath).Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                await Task.Delay(pollInterval, cancellationToken);
                continue;
            }

            if (currentSize != lastSize)
            {
                lastSize = currentSize;
                lastChangeUtc = DateTimeOffset.UtcNow;
                await Task.Delay(pollInterval, cancellationToken);
                continue;
            }

            if (DateTimeOffset.UtcNow - lastChangeUtc < quietPeriod)
            {
                await Task.Delay(pollInterval, cancellationToken);
                continue;
            }

            if (IsUnlocked(filePath, open))
            {
                return true;
            }

            await Task.Delay(pollInterval, cancellationToken);
        }

        return false;
    }

    private static bool IsUnlocked(string filePath, Func<string, IDisposable> open)
    {
        try
        {
            using var stream = open(filePath);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Access denied while the writer (WER, the kernel) still holds the
            // file is "not ready yet", not a reason to drop the dump (website#1493).
            return false;
        }
    }
}
