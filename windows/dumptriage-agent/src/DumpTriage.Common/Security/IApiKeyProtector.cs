namespace DumpTriage.Common.Security;

/// <summary>
/// Encrypts the customer's DumpTriage API key at rest so it doesn't sit as
/// plaintext in %ProgramData%\DumpTriage\config.json (which is admin-writable
/// but shouldn't hold a live bearer credential in the clear). The real
/// implementation (DpapiApiKeyProtector, in the service project) uses Windows
/// DPAPI at machine scope; this interface lives in Common so the rest of the
/// agent doesn't depend on a Windows-only type.
/// </summary>
public interface IApiKeyProtector
{
    string Protect(string plaintextApiKey);
    string Unprotect(string protectedApiKey);
}
