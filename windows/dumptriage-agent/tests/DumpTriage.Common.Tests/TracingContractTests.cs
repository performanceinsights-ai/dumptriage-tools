using System.Text.Json;

namespace DumpTriage.Common.Tests;

/// <summary>
/// The shared tracing fixture (dumptriage-tools#26, website#1999). The Linux suite holds the reference
/// evaluator; this one proves the Windows build can load the same file and its schemas are readable. The
/// trigger service (#29) is then tested against every case here.
/// </summary>
public class TracingContractTests
{
    private static readonly string[] Outcomes = { "capped", "cooldown", "fire", "no_fire" };

    private static string RepoFile(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "schemas")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(new[] { dir!.FullName }.Concat(parts).ToArray());
    }

    private static JsonDocument Load(params string[] parts) => JsonDocument.Parse(File.ReadAllText(RepoFile(parts)));

    [Fact]
    public void TriggerFixture_CasesAreWellFormedAndCoverEveryOutcome()
    {
        using var doc = Load("tests", "fixtures", "tracing", "triggers.json");
        var cases = doc.RootElement.GetProperty("cases").EnumerateArray().ToList();
        Assert.NotEmpty(cases);
        Assert.Equal(cases.Count, cases.Select(c => c.GetProperty("name").GetString()).Distinct().Count());
        foreach (var c in cases)
        {
            var expect = c.GetProperty("expect");
            var outcome = expect.GetProperty("outcome").GetString()!;
            Assert.Contains(outcome, Outcomes);
            Assert.Equal(outcome != "no_fire", expect.TryGetProperty("atSec", out _));
            var times = c.GetProperty("series").EnumerateArray().Select(s => s.GetProperty("tSec").GetInt64()).ToList();
            Assert.Equal(times.OrderBy(t => t).ToList(), times);
        }
        Assert.Equal(Outcomes, cases.Select(c => c.GetProperty("expect").GetProperty("outcome").GetString()!).Distinct().OrderBy(o => o, StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("tracing-config.schema.json")]
    [InlineData("trace-manifest.schema.json")]
    public void Schemas_ParseAndDeclareADraft(string file)
    {
        using var doc = Load("schemas", file);
        Assert.Contains("2020-12", doc.RootElement.GetProperty("$schema").GetString());
        Assert.Equal("object", doc.RootElement.GetProperty("type").GetString());
    }

    [Fact]
    public void ConfigSchema_DefaultsOffAndHeldForApproval()
    {
        using var doc = Load("schemas", "tracing-config.schema.json");
        var props = doc.RootElement.GetProperty("properties");
        Assert.False(props.GetProperty("enabled").GetProperty("default").GetBoolean());
        Assert.Equal("AskFirst", props.GetProperty("submission").GetProperty("default").GetString());
    }
}
