namespace DumpTriage.Common.Security;

public enum ApiKeyBootstrapMode
{
    /// <summary>Not a bootstrap invocation; run the agent normally.</summary>
    None,

    /// <summary>`--set-api-key-stdin`, or `--set-api-key` with no argument: read the key from stdin.</summary>
    ReadFromStdin,

    /// <summary>
    /// `--set-api-key &lt;key&gt;`: the pre-#261 form that put the key in plaintext
    /// process listings and shell history. Refuse it instead of accepting it.
    /// </summary>
    RejectLegacyArgument,
}

public static class ApiKeyBootstrapArgs
{
    public static ApiKeyBootstrapMode Parse(string[] args) => args switch
    {
        ["--set-api-key-stdin"] or ["--set-api-key"] => ApiKeyBootstrapMode.ReadFromStdin,
        ["--set-api-key", ..] => ApiKeyBootstrapMode.RejectLegacyArgument,
        _ => ApiKeyBootstrapMode.None,
    };
}
