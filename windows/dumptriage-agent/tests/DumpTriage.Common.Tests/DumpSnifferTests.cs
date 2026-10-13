using DumpTriage.Common.Detection;
using DumpTriage.Common.Models;

namespace DumpTriage.Common.Tests;

public class DumpSnifferTests
{
    [Fact]
    public async Task DetectsUserModeMinidump()
    {
        var path = await WriteTempFileAsync("MDMP"u8.ToArray().Concat(new byte[100]).ToArray());
        try
        {
            Assert.Equal(DumpType.UserMode, await DumpSniffer.DetectAsync(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task DetectsKernelModeRawDump()
    {
        var path = await WriteTempFileAsync("PAGE"u8.ToArray().Concat(new byte[100]).ToArray());
        try
        {
            Assert.Equal(DumpType.KernelMode, await DumpSniffer.DetectAsync(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task UnrecognizedHeader_ReturnsUnknown()
    {
        var path = await WriteTempFileAsync("XXXX"u8.ToArray().Concat(new byte[100]).ToArray());
        try
        {
            Assert.Equal(DumpType.Unknown, await DumpSniffer.DetectAsync(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static async Task<string> WriteTempFileAsync(byte[] content)
    {
        var path = Path.GetTempFileName();
        await File.WriteAllBytesAsync(path, content);
        return path;
    }
}
