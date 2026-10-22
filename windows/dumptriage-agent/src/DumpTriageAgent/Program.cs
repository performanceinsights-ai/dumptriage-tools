using DumpTriage.Common.Config;
using DumpTriage.Common.Idle;
using DumpTriage.Common.Ipc;
using DumpTriage.Common.Policy;
using DumpTriage.Common.Queue;
using DumpTriage.Common.Security;
using DumpTriage.Common.Upload;
using DumpTriageAgent.Idle;
using DumpTriageAgent.Ipc;
using DumpTriageAgent.Runtime;
using DumpTriageAgent.Security;
using DumpTriageAgent.Tracing;
using DumpTriageAgent.Upload;
using DumpTriageAgent.Watching;
using Serilog;
using Serilog.Events;

var paths = AgentPaths.ForCurrentOs();
var configPath = paths.ConfigPath;
var dbPath = paths.DbPath;
var logDir = paths.LogDir;

// Before anything reads or writes the data folder, in every mode (service and --set-api-key-stdin):
// it must be ours and readable by SYSTEM and Administrators only (#3).
if (OperatingSystem.IsWindows())
{
    try
    {
        DataDirectoryGuard.Secure(Path.GetDirectoryName(configPath)!);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Refusing to start: {ex.Message}");
        try
        {
            System.Diagnostics.EventLog.WriteEntry("DumpTriageAgent", $"Refusing to start: {ex.Message}", System.Diagnostics.EventLogEntryType.Error);
        }
        catch (Exception)
        {
            // No event source yet (first run) or no right to create one: stderr is all we have.
        }
        return 3;
    }
}

IApiKeyProtector CreateApiKeyProtector() => OperatingSystem.IsWindows()
    ? new DpapiApiKeyProtector()
    : new FileApiKeyProtector(paths.ApiKeyPath);

// Bootstrap mode: `DumpTriageAgent.exe --set-api-key-stdin` (or `--set-api-key`) reads the key
// from stdin (one line), protects it (machine-scope DPAPI on Windows), writes it into
// config.json, then exits. This lets install.ps1 provision the key without
// reimplementing DPAPI in PowerShell — it just shells out to the already-
// published exe once. The key used to be a command-line argument, which put
// it in plaintext process listings and shell history (website#261); stdin avoids both.
switch (ApiKeyBootstrapArgs.Parse(args))
{
    case ApiKeyBootstrapMode.ReadFromStdin:
    {
        var plaintextApiKey = Console.In.ReadLine()?.Trim();
        if (string.IsNullOrEmpty(plaintextApiKey))
        {
            await Console.Error.WriteLineAsync("Expected the API key on stdin (one line); got none.");
            return 1;
        }

        var bootstrapConfig = await ConfigLoader.LoadOrCreateDefaultAsync(configPath);
        var protector = CreateApiKeyProtector();
        bootstrapConfig.ProtectedApiKey = protector.Protect(plaintextApiKey);
        await ConfigLoader.SaveAsync(configPath, bootstrapConfig);
        Console.WriteLine($"API key stored in {configPath}");
        return 0;
    }
    case ApiKeyBootstrapMode.RejectLegacyArgument:
        await Console.Error.WriteLineAsync(
            "--set-api-key takes the key on stdin now, not as an argument (it used to be " +
            "visible in process listings and shell history). Pipe it in instead, e.g.: " +
            "Get-Content key.txt | DumpTriageAgent.exe --set-api-key-stdin");
        return 1;
}

#pragma warning disable CA1416 // guarded by OperatingSystem.IsWindows() below; only ever deployed on Windows anyway
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.File(
        Path.Combine(logDir, "agent-.log"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 14)
    .WriteTo.Conditional(
        _ => OperatingSystem.IsWindows(),
        sink => sink.EventLog("DumpTriageAgent", manageEventSource: true, restrictedToMinimumLevel: LogEventLevel.Warning))
    .CreateLogger();
#pragma warning restore CA1416

try
{
    var config = await ConfigLoader.LoadOrCreateDefaultAsync(configPath);
    var queueStore = await DumpQueueStore.OpenAsync(
        dbPath, new Serilog.Extensions.Logging.SerilogLoggerFactory(Log.Logger).CreateLogger<DumpQueueStore>());

    var builder = Host.CreateApplicationBuilder(args);
    builder.Services.AddSerilog();
    if (OperatingSystem.IsWindows())
    {
        builder.Services.AddWindowsService(options => options.ServiceName = "DumpTriageAgent");
    }
    else
    {
        builder.Services.AddSystemd();
    }

    builder.Services.AddSingleton(config);
    builder.Services.AddSingleton(queueStore);
    builder.Services.AddSingleton(CreateApiKeyProtector());
    if (OperatingSystem.IsWindows())
    {
        builder.Services.AddSingleton<IIdleTimeProvider, Win32IdleTimeProvider>();
    }
    else
    {
        builder.Services.AddSingleton<IIdleTimeProvider, NullIdleTimeProvider>();
        if (config.Policy.Batching.Mode == BatchMode.Idle)
        {
            Log.Warning("Idle batching can't detect user activity on a headless host; dumps will never flush. Use Immediate or ScheduledTimeOfDay.");
        }
    }
    builder.Services.AddSingleton<ApprovalCoordinator>();
    builder.Services.AddSingleton<AgentRuntimeState>();

    builder.Services.AddHttpClient<DumpTriageApiClient>(client =>
    {
        client.BaseAddress = new Uri(config.ApiBaseUrl);
        // No whole-request timeout: it capped an upload at about 4.5 GB on a
        // 20 Mbit/s line (website#1488). DumpTriageApiClient cancels a stalled upload
        // or a missing answer itself (StallTimeout, ResponseTimeout).
        client.Timeout = Timeout.InfiniteTimeSpan;
    });

    if (OperatingSystem.IsWindows())
    {
        // The tray app is Windows-only; Linux runs headless with no IPC surface.
        builder.Services.AddSingleton<PipeServerHostedService>();
        builder.Services.AddSingleton<ITrayChannel>(sp => sp.GetRequiredService<PipeServerHostedService>());
        builder.Services.AddHostedService(sp => sp.GetRequiredService<PipeServerHostedService>());
    }
    else
    {
        builder.Services.AddSingleton<ITrayChannel, NullTrayChannel>();
    }
    builder.Services.AddHostedService<DumpWatcherService>();
    builder.Services.AddHostedService<UploadWorkerService>();
    builder.Services.AddHostedService<StatusBroadcastService>();
    // Performance traces (website#1999): idle unless the tracing section is enabled.
    builder.Services.AddSingleton<IWpr, WprCli>();
    if (OperatingSystem.IsWindows())
    {
#pragma warning disable CA1416 // inside OperatingSystem.IsWindows(); the analyzer does not follow it into the lambda
        builder.Services.AddSingleton<IMetricSource>(_ => new WindowsMetricSource(MetricNeeds.For(config.Tracing.Triggers)));
#pragma warning restore CA1416
    }
    builder.Services.AddHostedService(sp => new TracingService(
        config, queueStore, sp.GetRequiredService<ApprovalCoordinator>(), sp.GetRequiredService<ITrayChannel>(),
        sp.GetRequiredService<IWpr>(), sp.GetService<IMetricSource>(), sp.GetRequiredService<ILogger<TracingService>>(),
        traceDir: Path.Combine(Path.GetDirectoryName(dbPath)!, "traces")));

    var host = builder.Build();
    await host.RunAsync();
}
catch (Exception ex)
{
    Log.Fatal(ex, "DumpTriageAgent terminated unexpectedly");
    return 1;
}
finally
{
    await Log.CloseAndFlushAsync();
}

return 0;
