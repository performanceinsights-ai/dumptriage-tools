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
using DumpTriageAgent.Upload;
using DumpTriageAgent.Watching;
using Serilog;
using Serilog.Events;

var paths = AgentPaths.ForCurrentOs();
var configPath = paths.ConfigPath;
var dbPath = paths.DbPath;
var logDir = paths.LogDir;

IApiKeyProtector CreateApiKeyProtector() => OperatingSystem.IsWindows()
    ? new DpapiApiKeyProtector()
    : new FileApiKeyProtector(paths.ApiKeyPath);

// Bootstrap mode: `DumpTriageAgent.exe --set-api-key <key>` (or --set-api-key-stdin) protects the key
// with machine-scope DPAPI and writes it into config.json, then exits. This
// lets install.ps1 provision the key without reimplementing DPAPI in
// PowerShell — it just shells out to the already-published exe once.
// `--set-api-key-stdin` reads the key from standard input instead, so it never
// appears in the process command line (visible to other users and logs).
string? plaintextApiKey = args switch
{
    ["--set-api-key", var key] => key,
    ["--set-api-key-stdin"] => (await Console.In.ReadLineAsync())?.Trim(),
    _ => null,
};
if (args is ["--set-api-key", _] or ["--set-api-key-stdin"])
{
    if (string.IsNullOrEmpty(plaintextApiKey))
    {
        Console.Error.WriteLine("No API key provided.");
        return 1;
    }

    var bootstrapConfig = await ConfigLoader.LoadOrCreateDefaultAsync(configPath);
    var protector = CreateApiKeyProtector();
    bootstrapConfig.ProtectedApiKey = protector.Protect(plaintextApiKey);
    await ConfigLoader.SaveAsync(configPath, bootstrapConfig);
    Console.WriteLine($"API key stored in {configPath}");
    return 0;
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
    var queueStore = await DumpQueueStore.OpenAsync(dbPath);
    var recovered = await queueStore.RecoverInterruptedSubmissionsAsync();
    if (recovered > 0) Log.Warning("Re-queued {Count} dump(s) interrupted mid-submission", recovered);

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
        client.Timeout = TimeSpan.FromMinutes(30); // large kernel dumps over a slow link
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
