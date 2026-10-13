using DumpTriage.Common.CentralCopy;
using DumpTriage.Common.Config;
using DumpTriage.Common.Models;

namespace DumpTriage.Common.Tests;

public class CentralCopyServiceTests
{
    private static DumpRecord MakeRecord(string sourcePath, string id = "abc12345")
    {
        return new DumpRecord
        {
            Id = id,
            FilePath = sourcePath,
            SizeBytes = 0,
            DetectedAtUtc = DateTimeOffset.UtcNow,
        };
    }

    [Fact]
    public async Task Disabled_DoesNothing()
    {
        var config = new CentralCopyConfig { Enabled = false };
        var (success, error) = await CentralCopyService.TryCopyAsync(MakeRecord("/nonexistent"), config, "HOST1");
        Assert.False(success);
        Assert.Null(error);
    }

    [Fact]
    public async Task Enabled_NoDestination_ReturnsError()
    {
        var config = new CentralCopyConfig { Enabled = true, DestinationPath = null };
        var (success, error) = await CentralCopyService.TryCopyAsync(MakeRecord("/nonexistent"), config, "HOST1");
        Assert.False(success);
        Assert.NotNull(error);
    }

    [Fact]
    public async Task CopiesFileIntoHostnameSubfolder()
    {
        var destRoot = Path.Combine(Path.GetTempPath(), "dt-central-" + Guid.NewGuid().ToString("N"));
        var sourceFile = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".dmp");
        await File.WriteAllBytesAsync(sourceFile, new byte[] { 1, 2, 3, 4 });

        try
        {
            var config = new CentralCopyConfig { Enabled = true, DestinationPath = destRoot, OrganizeByHostname = true };
            var record = MakeRecord(sourceFile);

            var (success, error) = await CentralCopyService.TryCopyAsync(record, config, "HOST1");

            Assert.True(success);
            Assert.Null(error);
            var expectedPath = Path.Combine(destRoot, "HOST1", Path.GetFileName(sourceFile));
            Assert.True(File.Exists(expectedPath));
            Assert.Equal(new byte[] { 1, 2, 3, 4 }, await File.ReadAllBytesAsync(expectedPath));
        }
        finally
        {
            File.Delete(sourceFile);
            if (Directory.Exists(destRoot)) Directory.Delete(destRoot, recursive: true);
        }
    }

    [Fact]
    public async Task FlatLayout_WhenOrganizeByHostnameDisabled()
    {
        var destRoot = Path.Combine(Path.GetTempPath(), "dt-central-" + Guid.NewGuid().ToString("N"));
        var sourceFile = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".dmp");
        await File.WriteAllBytesAsync(sourceFile, new byte[] { 9 });

        try
        {
            var config = new CentralCopyConfig { Enabled = true, DestinationPath = destRoot, OrganizeByHostname = false };
            var (success, _) = await CentralCopyService.TryCopyAsync(MakeRecord(sourceFile), config, "HOST1");

            Assert.True(success);
            Assert.True(File.Exists(Path.Combine(destRoot, Path.GetFileName(sourceFile))));
            Assert.False(Directory.Exists(Path.Combine(destRoot, "HOST1")));
        }
        finally
        {
            File.Delete(sourceFile);
            if (Directory.Exists(destRoot)) Directory.Delete(destRoot, recursive: true);
        }
    }

    [Fact]
    public async Task NameClash_DisambiguatesWithDumpId_InsteadOfOverwriting()
    {
        var destRoot = Path.Combine(Path.GetTempPath(), "dt-central-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(destRoot, "HOST1"));
        var existingPath = Path.Combine(destRoot, "HOST1", "MEMORY.DMP");
        await File.WriteAllBytesAsync(existingPath, new byte[] { 0xAA }); // pre-existing archival copy from a different dump

        var sourceFile = Path.Combine(Path.GetTempPath(), "MEMORY.DMP." + Guid.NewGuid().ToString("N"));
        var sourceDir = Path.GetDirectoryName(sourceFile)!;
        var actualSource = Path.Combine(sourceDir, "MEMORY.DMP");
        // Simulate a second machine's MEMORY.DMP with the same filename.
        await File.WriteAllBytesAsync(actualSource, new byte[] { 0xBB });

        try
        {
            var config = new CentralCopyConfig { Enabled = true, DestinationPath = destRoot, OrganizeByHostname = true };
            var record = MakeRecord(actualSource, "deadbeef00");

            var (success, _) = await CentralCopyService.TryCopyAsync(record, config, "HOST1");

            Assert.True(success);
            // Original copy untouched.
            Assert.Equal(new byte[] { 0xAA }, await File.ReadAllBytesAsync(existingPath));
            // New copy landed alongside it under a disambiguated name.
            var disambiguated = Path.Combine(destRoot, "HOST1", "MEMORY.deadbeef.DMP");
            Assert.True(File.Exists(disambiguated));
            Assert.Equal(new byte[] { 0xBB }, await File.ReadAllBytesAsync(disambiguated));
        }
        finally
        {
            File.Delete(actualSource);
            if (Directory.Exists(destRoot)) Directory.Delete(destRoot, recursive: true);
        }
    }
}
