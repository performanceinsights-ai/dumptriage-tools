using DumpTriage.Common.Config;

namespace DumpTriage.Common.Tests;

// dumptriage-tools#60: the API key and the dump travel in the request, so the URL must be https, or http to this machine.
public class ApiUrlTests
{
    [Theory]
    [InlineData("https://api.performanceinsights.ai")]
    [InlineData("https://dumptriage.corp.example:8443/base")]
    [InlineData("http://127.0.0.1:8000")]
    [InlineData("http://localhost:8000")]
    [InlineData("http://[::1]:8000")]
    public void SecureUrlsAreAccepted(string url) => Assert.True(AgentConfig.IsSecureApiUrl(url));

    [Theory]
    [InlineData("http://api.performanceinsights.ai")]
    [InlineData("http://10.0.0.5:8000")]
    [InlineData("http://127.0.0.1.evil.example")]
    [InlineData("http://localhost:x@evil.example")]
    [InlineData("http://127.0.0.1@evil.example")]
    [InlineData("ftp://x.example")]
    [InlineData("api.performanceinsights.ai")]
    [InlineData("")]
    [InlineData(null)]
    public void AnythingElseIsRefused(string? url) => Assert.False(AgentConfig.IsSecureApiUrl(url));

    [Fact]
    public void TheServiceRefusesAnInsecureUrlBeforeItCanSend()
    {
        // The check lives in Program.cs's top-level startup, which no test can run: pin that it follows the config load
        // and comes before the HTTP client and the hosted services are registered, so a refactor can't drop or move it.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "schemas"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var program = File.ReadAllText(Path.Combine(dir!.FullName, "windows", "dumptriage-agent", "src", "DumpTriageAgent", "Program.cs"));
        var load = program.IndexOf("var config = await ConfigLoader.LoadOrCreateDefaultAsync(configPath);", StringComparison.Ordinal);
        var check = program.IndexOf("if (!AgentConfig.IsSecureApiUrl(config.ApiBaseUrl))", StringComparison.Ordinal);
        var client = program.IndexOf("AddHttpClient<DumpTriageApiClient>", StringComparison.Ordinal);
        Assert.True(load >= 0 && check > load && client > check, $"load {load}, check {check}, client {client}");
        Assert.Contains("return 1;", program.Substring(check, 300));
    }
}
