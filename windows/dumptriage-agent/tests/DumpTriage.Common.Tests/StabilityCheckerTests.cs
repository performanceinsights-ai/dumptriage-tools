using DumpTriage.Common.Detection;

namespace DumpTriage.Common.Tests;

public class StabilityCheckerTests
{
    [Fact]
    public async Task StableFile_ReturnsTrue()
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(path, new byte[1024]);

            var result = await StabilityChecker.WaitUntilStableAsync(
                path,
                pollInterval: TimeSpan.FromMilliseconds(20),
                quietPeriod: TimeSpan.FromMilliseconds(50),
                timeout: TimeSpan.FromSeconds(2));

            Assert.True(result);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task GrowingFile_WaitsUntilSizeStopsChanging()
    {
        var path = Path.GetTempFileName();
        using var cts = new CancellationTokenSource();
        try
        {
            var writerTask = Task.Run(async () =>
            {
                for (var i = 0; i < 5; i++)
                {
                    await using (var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                    {
                        await stream.WriteAsync(new byte[100]);
                    }
                    await Task.Delay(30);
                }
            });

            await writerTask;

            var result = await StabilityChecker.WaitUntilStableAsync(
                path,
                pollInterval: TimeSpan.FromMilliseconds(20),
                quietPeriod: TimeSpan.FromMilliseconds(50),
                timeout: TimeSpan.FromSeconds(2));

            Assert.True(result);
            Assert.Equal(500, new FileInfo(path).Length);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task NonexistentFile_TimesOutAndReturnsFalse()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".dmp");

        var result = await StabilityChecker.WaitUntilStableAsync(
            path,
            pollInterval: TimeSpan.FromMilliseconds(20),
            quietPeriod: TimeSpan.FromMilliseconds(50),
            timeout: TimeSpan.FromMilliseconds(150));

        Assert.False(result);
    }
}
