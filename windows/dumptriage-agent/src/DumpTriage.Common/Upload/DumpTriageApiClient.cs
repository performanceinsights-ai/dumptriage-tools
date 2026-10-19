using System.Net;
using System.Net.Http.Headers;

namespace DumpTriage.Common.Upload;

/// <summary>
/// What an upload attempt means for the dump and for the queue. Mapped from
/// what POST /api/v1/dumps returns (service/backend/app/routers/uploads.py,
/// services/ingestion.py, security/api_keys.py), website#1487.
/// </summary>
public enum UploadOutcome
{
    Success,
    // 400 malformed multipart, 413 over the plan's per-dump size, 415, 422
    // (not a dump, failed malware scan, unsupported OS): sending the same
    // bytes again gets the same answer.
    RejectedPermanently,
    // 429: the account's monthly quota is used up. Resets, so the dump waits;
    // every dump would get the same answer, so the whole queue holds.
    QuotaExceeded,
    TransientFailure,
    // 403 terms_not_accepted: a signup reissue left the account without a
    // Terms acceptance (website#1313). Not the dump's fault: hold it until the
    // owner accepts in the portal, don't spend its retries.
    AwaitingTerms,
    // 401 (key invalid, revoked, or replaced mid-upload) or a 403 for the key
    // other than the two below: no dump can upload until the key is replaced.
    CredentialsRejected,
    // 403 "this account has been closed".
    AccountClosed,
    // The dump file is gone: nothing left to send.
    FileMissing,
    // The dump file couldn't be opened right now (held open by another
    // process, access denied for the moment): try again later. Local, so it
    // says nothing about the server or the other dumps.
    FileUnavailable,
    // The file's bytes are no longer the ones this record was hashed from
    // (checked before upload, website#1493): this record is done, and the file is
    // detected again as a new dump.
    FileChanged,
}

public sealed record UploadResult(UploadOutcome Outcome, HttpStatusCode? StatusCode, string? ErrorMessage)
{
    public bool ShouldRetry => Outcome is UploadOutcome.TransientFailure or UploadOutcome.FileUnavailable;
}

/// <summary>
/// Streams a dump file to POST /api/v1/dumps, matching the request shape the
/// web UI already uses (service/frontend/src/api/client.ts:131-154):
/// multipart/form-data with a "file" field and an optional "hostname" field,
/// bearer auth with the customer's API key. No chunked/resumable upload
/// exists server-side yet, so this streams from disk in one shot rather than
/// buffering — dumps can be tens of GB.
/// </summary>
public sealed class DumpTriageApiClient
{
    private readonly HttpClient _httpClient;

    public DumpTriageApiClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    /// <summary>Longest the request body may stop moving before the upload is abandoned.</summary>
    public TimeSpan StallTimeout { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Longest to wait for the server's answer once the whole body is sent.</summary>
    public TimeSpan ResponseTimeout { get; init; } = TimeSpan.FromMinutes(30);

    public async Task<UploadResult> UploadDumpAsync(
        string filePath,
        string apiKey,
        string? hostnameHash,
        string? expectedSha256 = null,
        CancellationToken cancellationToken = default)
    {
        FileStream fileStream;
        try
        {
            fileStream = new FileStream(
                filePath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 1 << 20, useAsync: true);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return new UploadResult(UploadOutcome.FileMissing, null, ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A sharing violation: antivirus, a backup agent or the writer
            // still has it open. Used to fail the dump for good (website#1489).
            return new UploadResult(UploadOutcome.FileUnavailable, null, ex.Message);
        }
        var body = new ProgressStream(fileStream);
        await using var _ = body;

        using var content = new MultipartFormDataContent();
        var streamContent = new StreamContent(body);
        streamContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Add(streamContent, "file", Path.GetFileName(filePath));

        if (!string.IsNullOrEmpty(hostnameHash))
        {
            content.Add(new StringContent(hostnameHash), "hostname");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/dumps")
        {
            Content = content,
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        if (!string.IsNullOrEmpty(expectedSha256))
        {
            // The hash taken at detection: the server refuses other bytes
            // with a 422 instead of storing them under this dump (website#1495).
            request.Headers.Add(ContentSha256Header, expectedSha256);
        }

        using var watchdog = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var stalled = WatchProgressAsync(body, watchdog);
        try
        {
            using var response = await _httpClient.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, watchdog.Token);

            if (response.StatusCode == HttpStatusCode.Created)
            {
                return new UploadResult(UploadOutcome.Success, response.StatusCode, null);
            }

            var responseBody = await response.Content.ReadAsStringAsync(watchdog.Token);

            return new UploadResult(Classify(response.StatusCode, responseBody), response.StatusCode, responseBody);
        }
        catch (HttpRequestException ex)
        {
            return new UploadResult(UploadOutcome.TransientFailure, null, ex.Message);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            // The watchdog (the upload stopped moving) or an HttpClient
            // timeout, not the caller.
            watchdog.Cancel();
            return new UploadResult(UploadOutcome.TransientFailure, null, await stalled ?? ex.Message);
        }
        finally
        {
            watchdog.Cancel();
            await stalled;
        }
    }

    /// <summary>
    /// No whole-request timeout (HttpClient.Timeout is infinite): it capped an
    /// upload at about 4.5 GB on a 20 Mbit/s line (website#1488). Instead the request
    /// is cancelled when the body stops moving for <see cref="StallTimeout"/>,
    /// or, once the body is sent, when the server hasn't answered within
    /// <see cref="ResponseTimeout"/> (it scans and stores a large dump before
    /// it answers). Returns why it cancelled, or null if it didn't.
    /// </summary>
    private async Task<string?> WatchProgressAsync(ProgressStream body, CancellationTokenSource watchdog)
    {
        var poll = TimeSpan.FromTicks(Math.Max(TimeSpan.TicksPerMillisecond * 10, Math.Min(StallTimeout.Ticks, ResponseTimeout.Ticks) / 10));
        try
        {
            while (true)
            {
                await Task.Delay(poll, watchdog.Token);
                var limit = body.Completed ? ResponseTimeout : StallTimeout;
                if (body.MillisecondsSinceActivity > limit.TotalMilliseconds)
                {
                    var reason = body.Completed
                        ? $"no response within {limit} after the upload finished"
                        : $"upload stalled: no progress for {limit}";
                    watchdog.Cancel();
                    return reason;
                }
            }
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    public const string ContentSha256Header = "X-Content-SHA256";

    internal static UploadOutcome Classify(HttpStatusCode status, string body) => status switch
    {
        // routers/uploads.py CONTENT_SHA256_MISMATCH: the file changed since it was hashed.
        HttpStatusCode.UnprocessableEntity when body.Contains("content_sha256_mismatch", StringComparison.Ordinal) =>
            UploadOutcome.FileChanged,
        HttpStatusCode.BadRequest
            or HttpStatusCode.RequestEntityTooLarge
            or HttpStatusCode.UnsupportedMediaType
            or HttpStatusCode.UnprocessableEntity => UploadOutcome.RejectedPermanently,
        HttpStatusCode.TooManyRequests => UploadOutcome.QuotaExceeded,
        HttpStatusCode.Unauthorized => UploadOutcome.CredentialsRejected,
        HttpStatusCode.Forbidden when body.Contains("terms_not_accepted", StringComparison.Ordinal) => UploadOutcome.AwaitingTerms,
        HttpStatusCode.Forbidden when body.Contains("account has been closed", StringComparison.Ordinal) => UploadOutcome.AccountClosed,
        HttpStatusCode.Forbidden => UploadOutcome.CredentialsRejected,
        _ => UploadOutcome.TransientFailure, // 5xx (503 scanner unavailable), proxies, anything unexpected
    };
}
