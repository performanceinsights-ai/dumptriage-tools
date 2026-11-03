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
    [InlineData("ftp://x.example")]
    [InlineData("api.performanceinsights.ai")]
    [InlineData("")]
    [InlineData(null)]
    public void AnythingElseIsRefused(string? url) => Assert.False(AgentConfig.IsSecureApiUrl(url));

    [Fact]
    public async Task AConfigWithAPlainHttpUrlIsRefusedAtLoad()
    {
        var path = Path.Combine(Path.GetTempPath(), "dt-cfg-" + Guid.NewGuid().ToString("N") + ".json");
        await File.WriteAllTextAsync(path, """{"apiBaseUrl": "http://dumptriage.corp.example"}""");
        try
        {
            var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => ConfigLoader.LoadOrCreateDefaultAsync(path));
            Assert.Contains("must be an https URL", refused.Message);
            await File.WriteAllTextAsync(path, """{"apiBaseUrl": "https://dumptriage.corp.example"}""");
            Assert.Equal("https://dumptriage.corp.example", (await ConfigLoader.LoadOrCreateDefaultAsync(path)).ApiBaseUrl);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
