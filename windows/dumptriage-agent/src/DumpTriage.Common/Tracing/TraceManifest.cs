using System.Text.Json;
using System.Text.Json.Nodes;

namespace DumpTriage.Common.Tracing;

/// <summary>
/// manifest.json for a WPR trace (<c>schemas/trace-manifest.schema.json</c>, #26). Carries a random per-install host
/// token, never the hostname; the command is the exact wpr command line, which names no user.
/// </summary>
public static class TraceManifest
{
    public static string Build(
        TraceTrigger trigger, double? observed, DateTimeOffset startUtc, DateTimeOffset stopUtc, int lookbackSec,
        string wprVersion, string command, string osBuild, string hostToken, string agentVersion)
    {
        var trig = new JsonObject { ["name"] = trigger.Name, ["type"] = trigger.Type };
        if (trigger.Threshold is { } threshold) trig["threshold"] = threshold;
        if (observed is { } value && double.IsFinite(value)) trig["observed"] = Math.Round(value, 3);
        if (!string.IsNullOrWhiteSpace(trigger.Process)) trig["process"] = trigger.Process;
        var manifest = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["traceType"] = "etl",
            ["trigger"] = trig,
            ["window"] = new JsonObject
            {
                ["startUtc"] = startUtc.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ"),
                ["stopUtc"] = stopUtc.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ"),
                ["lookbackSec"] = Math.Max(lookbackSec, 0),
            },
            ["tool"] = new JsonObject { ["name"] = "wpr", ["version"] = wprVersion, ["command"] = command },
            ["os"] = new JsonObject { ["family"] = "windows", ["build"] = osBuild },
            ["hostToken"] = hostToken,
            ["agentVersion"] = agentVersion,
        };
        return manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
    }
}
