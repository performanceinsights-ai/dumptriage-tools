using System.Diagnostics;

namespace DumpTriageAgent.Tracing;

public sealed record WprResult(int ExitCode, string Output)
{
    public bool Ok => ExitCode == 0;
}

/// <summary>The four wpr.exe calls the tracing service makes, behind an interface so the service is tested without Windows.</summary>
public interface IWpr
{
    Task<WprResult> SetProfileIntervalAsync(int hundredNanoseconds, CancellationToken ct);
    Task<WprResult> StartAsync(string profilePath, CancellationToken ct);
    Task<WprResult> StopAsync(string etlPath, CancellationToken ct);
    Task<WprResult> CancelAsync(CancellationToken ct);
    Task<string> VersionAsync(CancellationToken ct);
}

/// <summary>wpr.exe from System32 (in the box since Windows 10 and Server 2016).</summary>
public sealed class WprCli : IWpr
{
    private static readonly TimeSpan StopTimeout = TimeSpan.FromMinutes(5);

    public Task<WprResult> SetProfileIntervalAsync(int hundredNanoseconds, CancellationToken ct) =>
        RunAsync(new[] { "-setprofint", hundredNanoseconds.ToString(System.Globalization.CultureInfo.InvariantCulture) }, TimeSpan.FromSeconds(30), ct);

    public Task<WprResult> StartAsync(string profilePath, CancellationToken ct) =>
        RunAsync(new[] { "-start", profilePath + "!" + DumpTriage.Common.Tracing.WprProfile.ProfileName }, TimeSpan.FromMinutes(1), ct);

    public Task<WprResult> StopAsync(string etlPath, CancellationToken ct) =>
        RunAsync(new[] { "-stop", etlPath }, StopTimeout, ct);

    public Task<WprResult> CancelAsync(CancellationToken ct) => RunAsync(new[] { "-cancel" }, TimeSpan.FromMinutes(1), ct);

    public async Task<string> VersionAsync(CancellationToken ct)
    {
        // "Microsoft Windows Performance Recorder Version 10.0.20348 (CoreSystem)"
        var result = await RunAsync(new[] { "-help" }, TimeSpan.FromSeconds(30), ct);
        var marker = result.Output.IndexOf("Version ", StringComparison.Ordinal);
        if (marker < 0) return "unknown";
        var rest = result.Output[(marker + 8)..].Split(' ', '\r', '\n');
        return rest.Length > 0 && rest[0].Length > 0 ? rest[0] : "unknown";
    }

    private static async Task<WprResult> RunAsync(string[] args, TimeSpan timeout, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "wpr.exe"))
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("wpr.exe did not start");
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            process.Kill(entireProcessTree: true);
            return new WprResult(-1, $"wpr {string.Join(' ', args)} did not finish within {timeout}");
        }
        return new WprResult(process.ExitCode, (await stdout + await stderr).Trim());
    }
}
