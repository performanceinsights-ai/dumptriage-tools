using System.Net;
using System.Net.Http.Headers;

namespace DumpTriage.Common.Upload;

public enum UploadOutcome
{
    Success,
    RejectedPermanently,
    QuotaExceeded,
    /// <summary>401/403: the API key is missing, invalid or revoked. Retrying can't help.</summary>
    AuthFailed,
    TransientFailure,
}

public sealed record UploadResult(UploadOutcome Outcome, HttpStatusCode? StatusCode, string? ErrorMessage)
{
    public bool ShouldRetry => Outcome == UploadOutcome.TransientFailure;
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

    public async Task<UploadResult> UploadDumpAsync(
        string filePath,
        string apiKey,
        string? hostnameHash,
        CancellationToken cancellationToken = default)
    {
        await using var fileStream = new FileStream(
            filePath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 1 << 20, useAsync: true);

        using var content = new MultipartFormDataContent();
        var streamContent = new StreamContent(fileStream);
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

        try
        {
            using var response = await _httpClient.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            if (response.StatusCode == HttpStatusCode.Created)
            {
                return new UploadResult(UploadOutcome.Success, response.StatusCode, null);
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            return response.StatusCode switch
            {
                HttpStatusCode.BadRequest =>
                    new UploadResult(UploadOutcome.RejectedPermanently, response.StatusCode, body),
                HttpStatusCode.RequestEntityTooLarge or HttpStatusCode.TooManyRequests =>
                    new UploadResult(UploadOutcome.QuotaExceeded, response.StatusCode, body),
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                    new UploadResult(UploadOutcome.AuthFailed, response.StatusCode, body),
                HttpStatusCode.ServiceUnavailable =>
                    new UploadResult(UploadOutcome.TransientFailure, response.StatusCode, body),
                _ =>
                    new UploadResult(UploadOutcome.TransientFailure, response.StatusCode, body),
            };
        }
        catch (HttpRequestException ex)
        {
            return new UploadResult(UploadOutcome.TransientFailure, null, ex.Message);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            // Request-level timeout, not caller cancellation.
            return new UploadResult(UploadOutcome.TransientFailure, null, ex.Message);
        }
    }
}
