using DumpTriage.Common.Config;

namespace DumpTriage.Common.Tests;

/// <summary>The reference label (dumptriage-tools#77): read from config.json, trimmed, and refused at start when the service would refuse it.</summary>
public class AgentConfigReferenceTests
{
    [Fact]
    public async Task The_reference_is_read_from_config_json()
    {
        var path = Path.Combine(Path.GetTempPath(), "dt-cfg-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, """{"reference": "  client=Acme "}""");
        try
        {
            var config = await ConfigLoader.LoadOrCreateDefaultAsync(path);
            Assert.Equal("client=Acme", config.ReferenceToSend);
            Assert.Null(config.ReferenceProblem());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Saving_the_config_writes_the_label_as_set_and_nothing_derived_from_it()
    {
        // ConfigLoader.SaveAsync runs on every --set-api-key-stdin, so it must keep the label and add nothing.
        var path = Path.Combine(Path.GetTempPath(), "dt-cfg-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            await ConfigLoader.SaveAsync(path, new AgentConfig { Reference = "client=Acme" });
            var json = File.ReadAllText(path);
            Assert.Contains("\"reference\": \"client=Acme\"", json);
            Assert.DoesNotContain("referenceToSend", json, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_reference_sends_none(string? reference)
    {
        var config = new AgentConfig { Reference = reference };
        Assert.Null(config.ReferenceToSend);
        Assert.Null(config.ReferenceProblem());
    }

    [Fact]
    public void Over_200_characters_is_a_problem()
    {
        var problem = new AgentConfig { Reference = new string('a', 201) }.ReferenceProblem();
        Assert.Contains("201 characters", problem);
    }

    [Fact]
    public void Characters_are_counted_as_the_service_counts_them()
    {
        // 200 emoji are 400 UTF-16 units, and 200 characters to the service.
        var emoji = string.Concat(Enumerable.Repeat("\U0001F600", 200));
        Assert.Null(new AgentConfig { Reference = emoji }.ReferenceProblem());
        Assert.NotNull(new AgentConfig { Reference = emoji + "a" }.ReferenceProblem());
    }
}
