using System.Globalization;
using System.Text.RegularExpressions;

namespace DumpTriage.Common.Tracing;

/// <summary>
/// The WPR profile the recorder runs: the shipped <c>PerfInsights.wprp</c> with its ring buffer sized from the disk
/// budget. In memory mode the trace WPR writes is about the size of the ring, so a quarter of maxDiskMB, between 16 and
/// 256 buffers of 1 MB. Only the kernel collector's ring is sized; the .NET events keep their own small one.
/// </summary>
public static class WprProfile
{
    public const string ProfileName = "FlightRecorder";

    private static readonly Regex KernelRing =
        new("(<SystemCollector\\b(?:(?!</SystemCollector>).)*?)<Buffers Value=\"\\d+\"\\s*/>", RegexOptions.Singleline);

    public static int BufferCount(TraceLimits limits) => Math.Clamp(limits.MaxDiskMB / 4, 16, 256);

    public static string Render(string template, TraceLimits limits)
    {
        var buffers = BufferCount(limits).ToString(CultureInfo.InvariantCulture);
        return KernelRing.Replace(template, m => $"{m.Groups[1].Value}<Buffers Value=\"{buffers}\"/>", 1);
    }
}
