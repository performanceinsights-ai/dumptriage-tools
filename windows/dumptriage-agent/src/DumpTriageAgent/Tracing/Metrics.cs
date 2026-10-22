using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace DumpTriageAgent.Tracing;

/// <summary>One reading of what the triggers watch. A value that cannot be read, or needs a previous reading to compare
/// with, is null: a trigger fed null restarts its clock.</summary>
public sealed record Metrics(double? CpuPercent, double? LoadPerCpu, double? DiskLatencyMs, IReadOnlyDictionary<string, double> ProcessCpuPercent, int NewHangEvents);

public interface IMetricSource
{
    Metrics Read();
}

/// <summary>Which readings the configured triggers use. The others are not taken: a process walk and an event log query
/// every 5 s cost the machine for nothing when no trigger reads them.</summary>
public sealed record MetricNeeds(bool Cpu, bool Load, bool Disk, bool Processes, bool Hangs)
{
    public static MetricNeeds For(IEnumerable<DumpTriage.Common.Tracing.TraceTrigger> triggers)
    {
        bool cpu = false, load = false, disk = false, processes = false, hangs = false;
        foreach (var t in triggers)
        {
            switch (t.Type)
            {
                case "cpuPercent" when !string.IsNullOrWhiteSpace(t.Process):
                case "process":
                    processes = true;
                    break;
                case "cpuPercent": cpu = true; break;
                case "loadPerCpu": load = true; break;
                case "diskLatencyMs": disk = true; break;
                case "hangEvent": hangs = true; break;
            }
        }
        return new MetricNeeds(cpu, load, disk, processes, hangs);
    }
}

/// <summary>
/// Windows readings: total CPU from GetSystemTimes, per-process CPU (percent of one CPU, by process name without .exe)
/// from each process's TotalProcessorTime, the run queue per CPU from <c>\System\Processor Queue Length</c>, disk latency
/// from <c>\PhysicalDisk(_Total)\Avg. Disk sec/Transfer</c>, and Application Hang events (ID 1002) since the last reading.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsMetricSource : IMetricSource, IDisposable
{
    private readonly MetricNeeds _needs;
    private readonly PerformanceCounter? _queue;
    private readonly PerformanceCounter? _disk;
    private (long Idle, long Kernel, long User)? _last;
    private Dictionary<int, (string Name, TimeSpan Cpu)> _lastProcesses = new();
    private DateTime _lastProcessRead;
    private DateTime _hangsSince = DateTime.UtcNow;

    public WindowsMetricSource(MetricNeeds needs)
    {
        _needs = needs;
        if (needs.Load)
        {
            try { _queue = new PerformanceCounter("System", "Processor Queue Length", readOnly: true); } catch (Exception) { _queue = null; }
        }
        if (needs.Disk)
        {
            try { _disk = new PerformanceCounter("PhysicalDisk", "Avg. Disk sec/Transfer", "_Total", readOnly: true); } catch (Exception) { _disk = null; }
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out long idle, out long kernel, out long user);

    public Metrics Read()
    {
        double? cpu = null;
        if (_needs.Cpu && GetSystemTimes(out var idle, out var kernel, out var user))
        {
            if (_last is { } prev)
            {
                var total = (kernel - prev.Kernel) + (user - prev.User); // kernel time includes idle time
                var busy = total - (idle - prev.Idle);
                if (total > 0) cpu = 100.0 * busy / total;
            }
            _last = (idle, kernel, user);
        }
        double? load = null, disk = null;
        try { if (_queue != null) load = _queue.NextValue() / Environment.ProcessorCount; } catch (Exception) { load = null; }
        try { if (_disk != null) disk = _disk.NextValue() * 1000.0; } catch (Exception) { disk = null; }
        return new Metrics(cpu, load, disk,
            _needs.Processes ? ProcessCpu() : new Dictionary<string, double>(), _needs.Hangs ? NewHangs() : 0);
    }

    private Dictionary<string, double> ProcessCpu()
    {
        var now = DateTime.UtcNow;
        var current = new Dictionary<int, (string, TimeSpan)>();
        foreach (var p in Process.GetProcesses())
        {
            try { current[p.Id] = (p.ProcessName, p.TotalProcessorTime); }
            catch (Exception) { /* exited, or access denied (a protected process) */ }
            finally { p.Dispose(); }
        }
        var result = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var elapsed = (now - _lastProcessRead).TotalSeconds;
        if (_lastProcesses.Count > 0 && elapsed > 0)
        {
            foreach (var (pid, (name, cpu)) in current)
            {
                if (_lastProcesses.TryGetValue(pid, out var before) && before.Name == name)
                {
                    result[name] = (result.TryGetValue(name, out var v) ? v : 0) + (cpu - before.Cpu).TotalSeconds / elapsed * 100.0;
                }
            }
        }
        _lastProcesses = current;
        _lastProcessRead = now;
        return result;
    }

    private int NewHangs()
    {
        var since = _hangsSince;
        _hangsSince = DateTime.UtcNow;
        try
        {
            var query = new System.Diagnostics.Eventing.Reader.EventLogQuery("Application", System.Diagnostics.Eventing.Reader.PathType.LogName,
                $"*[System[Provider[@Name='Application Hang'] and (EventID=1002) and TimeCreated[@SystemTime>='{since:yyyy-MM-ddTHH:mm:ss.fffZ}']]]");
            using var reader = new System.Diagnostics.Eventing.Reader.EventLogReader(query);
            var n = 0;
            for (var e = reader.ReadEvent(); e != null; e = reader.ReadEvent()) { n++; e.Dispose(); }
            return n;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    public void Dispose()
    {
        _queue?.Dispose();
        _disk?.Dispose();
    }
}
