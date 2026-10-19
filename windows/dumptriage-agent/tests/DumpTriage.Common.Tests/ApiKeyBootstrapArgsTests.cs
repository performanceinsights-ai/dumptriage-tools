using DumpTriage.Common.Security;

namespace DumpTriage.Common.Tests;

public class ApiKeyBootstrapArgsTests
{
    [Fact]
    public void NoArgs_RunsNormally()
    {
        Assert.Equal(ApiKeyBootstrapMode.None, ApiKeyBootstrapArgs.Parse([]));
    }

    [Fact]
    public void UnrelatedArgs_RunsNormally()
    {
        Assert.Equal(ApiKeyBootstrapMode.None, ApiKeyBootstrapArgs.Parse(["--some-other-flag"]));
    }

    [Fact]
    public void SetApiKeyAlone_ReadsFromStdin()
    {
        Assert.Equal(ApiKeyBootstrapMode.ReadFromStdin, ApiKeyBootstrapArgs.Parse(["--set-api-key"]));
    }

    [Fact]
    public void SetApiKeyStdin_ReadsFromStdin()
    {
        // What installer/install.ps1 passes.
        Assert.Equal(ApiKeyBootstrapMode.ReadFromStdin, ApiKeyBootstrapArgs.Parse(["--set-api-key-stdin"]));
    }

    [Fact]
    public void SetApiKeyWithArgument_IsRejected()
    {
        // The pre-#261 form: the key on the command line, visible in process
        // listings and shell history. Must never be silently accepted again.
        Assert.Equal(
            ApiKeyBootstrapMode.RejectLegacyArgument,
            ApiKeyBootstrapArgs.Parse(["--set-api-key", "dts_secret"]));
    }

    [Fact]
    public void SetApiKeyWithExtraArguments_IsRejected()
    {
        Assert.Equal(
            ApiKeyBootstrapMode.RejectLegacyArgument,
            ApiKeyBootstrapArgs.Parse(["--set-api-key", "dts_secret", "extra"]));
    }
}
