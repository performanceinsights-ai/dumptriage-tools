using System.Net;
using DumpTriage.Common.Upload;

namespace DumpTriage.Common.Tests;

public class DumpTriageApiClientTests : IDisposable
{
    private readonly string _dumpPath = Path.Combine(Path.GetTempPath(), $"dt-{Guid.NewGuid():N}.dmp");

    public DumpTriageApiClientTests() => File.WriteAllBytes(_dumpPath, new byte[] { 0x4D, 0x44, 0x4D, 0x50 });

    public void Dispose() => File.Delete(_dumpPath);

    private sealed class FixedResponse(HttpStatusCode status, string body, string? retryAfter = null) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(status) { Content = new StringContent(body) };
            if (retryAfter is not null) response.Headers.TryAddWithoutValidation("Retry-After", retryAfter);
            return Task.FromResult(response);
        }
    }

    private Task<UploadResult> Upload(HttpStatusCode status, string body, string? retryAfter = null)
    {
        var http = new HttpClient(new FixedResponse(status, body, retryAfter)) { BaseAddress = new Uri("https://api.example") };
        return new DumpTriageApiClient(http).UploadDumpAsync(_dumpPath, "dts_key", null);
    }

    private sealed class Scripted(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            respond(request, cancellationToken);
    }

    private Task<UploadResult> Upload(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond, TimeSpan stall, TimeSpan response)
    {
        // Timeout infinite, as in Program.cs: only the client's own watchdog may cut it off.
        var http = new HttpClient(new Scripted(respond)) { BaseAddress = new Uri("https://api.example"), Timeout = Timeout.InfiniteTimeSpan };
        return new DumpTriageApiClient(http) { StallTimeout = stall, ResponseTimeout = response }
            .UploadDumpAsync(_dumpPath, "dts_key", null);
    }

    [Fact]
    public async Task An_upload_that_stops_moving_is_abandoned_as_transient()
    {
        // website#1488: the only limit was a 30-minute whole-request timeout.
        var result = await Upload(
            async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); throw new InvalidOperationException(); },
            stall: TimeSpan.FromMilliseconds(200), response: TimeSpan.FromSeconds(30)).WaitAsync(TimeSpan.FromSeconds(20));

        Assert.Equal(UploadOutcome.TransientFailure, result.Outcome);
        Assert.Contains("stalled", result.ErrorMessage);
    }

    [Fact]
    public async Task A_server_working_on_a_finished_upload_longer_than_the_stall_timeout_is_waited_for()
    {
        var result = await Upload(
            async (request, ct) =>
            {
                await request.Content!.ReadAsByteArrayAsync(ct);
                await Task.Delay(TimeSpan.FromMilliseconds(800), ct); // scanning, storing
                return new HttpResponseMessage(HttpStatusCode.Created);
            },
            stall: TimeSpan.FromMilliseconds(200), response: TimeSpan.FromSeconds(30));

        Assert.Equal(UploadOutcome.Success, result.Outcome);
    }

    [Fact]
    public async Task No_answer_after_the_upload_finished_is_abandoned_as_transient()
    {
        var result = await Upload(
            async (request, ct) =>
            {
                await request.Content!.ReadAsByteArrayAsync(ct);
                await Task.Delay(Timeout.Infinite, ct);
                throw new InvalidOperationException();
            },
            stall: TimeSpan.FromSeconds(30), response: TimeSpan.FromMilliseconds(300)).WaitAsync(TimeSpan.FromSeconds(20));

        Assert.Equal(UploadOutcome.TransientFailure, result.Outcome);
        Assert.Contains("no response", result.ErrorMessage);
    }

    [Fact]
    public async Task Terms_not_accepted_holds_the_dump_instead_of_retrying_it()
    {
        // website#1313: a reissued account must accept the Terms before uploading.
        var result = await Upload(
            HttpStatusCode.Forbidden, "{\"detail\":\"terms_not_accepted: accept the Terms of Service before uploading.\"}");

        Assert.Equal(UploadOutcome.AwaitingTerms, result.Outcome);
        Assert.False(result.ShouldRetry);
    }

    [Fact]
    public async Task A_dump_held_open_by_another_process_is_retried_later()
    {
        // website#1489: a sharing violation (AV, backup) failed the dump for good.
        await using (new FileStream(_dumpPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var result = await Upload(HttpStatusCode.Created, "{}");

            Assert.Equal(UploadOutcome.FileUnavailable, result.Outcome);
            Assert.True(result.ShouldRetry);
        }
    }

    [Fact]
    public async Task A_missing_dump_is_not_retried()
    {
        File.Delete(_dumpPath);

        var result = await Upload(HttpStatusCode.Created, "{}");

        Assert.Equal(UploadOutcome.FileMissing, result.Outcome);
        Assert.False(result.ShouldRetry);
    }

    // website#1487: what the server returns (routers/uploads.py, services/ingestion.py,
    // security/api_keys.py), mapped to what the agent should do about it.
    // dumptriage-tools#73: the website's 429s that are not a spent monthly quota (routers/uploads.py, security/api_keys.py)
    // carry a Retry-After of a minute or less; the monthly quota carries none.
    [Theory]
    [InlineData("this account already has 4 uploads in progress; wait for one to finish, then retry", "60", UploadOutcome.RateLimited, 60)]
    [InlineData("this account is over its limit of 600 API requests a minute; retry in 37s", "37", UploadOutcome.RateLimited, 37)]
    [InlineData("rate", "0", UploadOutcome.RateLimited, 0)]
    [InlineData("rate", "300", UploadOutcome.RateLimited, 300)]
    [InlineData("rate", "301", UploadOutcome.QuotaExceeded, 301)]
    [InlineData("monthly quota of 5 dumps exceeded", "86400", UploadOutcome.QuotaExceeded, 86400)]
    [InlineData("monthly quota of 5 dumps exceeded", null, UploadOutcome.QuotaExceeded, null)]
    [InlineData("monthly quota of 5 dumps exceeded", "soon", UploadOutcome.QuotaExceeded, null)]
    public async Task A_429_is_a_short_wait_only_when_its_Retry_After_says_so(string detail, string? retryAfter, UploadOutcome expected, int? seconds)
    {
        var result = await Upload(HttpStatusCode.TooManyRequests, "{\"detail\":\"" + detail + "\"}", retryAfter);

        Assert.Equal(expected, result.Outcome);
        Assert.Equal(seconds is null ? null : TimeSpan.FromSeconds(seconds.Value), result.RetryAfter);
        Assert.False(result.ShouldRetry); // a wait for the whole queue, not a retry of this dump
    }

    [Fact]
    public async Task A_Retry_After_given_as_an_HTTP_date_is_the_time_until_then()
    {
        var date = DateTimeOffset.UtcNow.AddSeconds(90).ToString("r");

        var result = await Upload(HttpStatusCode.TooManyRequests, "{}", date);

        Assert.Equal(UploadOutcome.RateLimited, result.Outcome);
        Assert.InRange(result.RetryAfter!.Value.TotalSeconds, 80, 90);
    }

    [Fact]
    public async Task A_Retry_After_on_another_status_changes_nothing()
    {
        Assert.Equal(UploadOutcome.TransientFailure, (await Upload(HttpStatusCode.ServiceUnavailable, "{}", "5")).Outcome);
        Assert.Equal(UploadOutcome.RejectedPermanently, (await Upload(HttpStatusCode.RequestEntityTooLarge, "{}", "5")).Outcome);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, "{\"detail\":\"no file part\"}", UploadOutcome.RejectedPermanently)]
    [InlineData(HttpStatusCode.RequestEntityTooLarge, "{\"detail\":\"upload exceeds the 1-byte per-dump limit\"}", UploadOutcome.RejectedPermanently)]
    [InlineData(HttpStatusCode.UnsupportedMediaType, "{}", UploadOutcome.RejectedPermanently)]
    [InlineData(HttpStatusCode.UnprocessableEntity, "{\"detail\":\"failed malware scan: x\"}", UploadOutcome.RejectedPermanently)]
    [InlineData(HttpStatusCode.UnprocessableEntity, "{\"detail\":\"content_sha256_mismatch: the uploaded file's sha256 differs\"}", UploadOutcome.FileChanged)]
    [InlineData(HttpStatusCode.TooManyRequests, "{\"detail\":\"monthly quota of 5 dumps exceeded\"}", UploadOutcome.QuotaExceeded)]
    [InlineData(HttpStatusCode.Unauthorized, "{\"detail\":\"invalid API key\"}", UploadOutcome.CredentialsRejected)]
    [InlineData(HttpStatusCode.Unauthorized, "{\"detail\":\"the API key this request used was revoked or replaced before it finished\"}", UploadOutcome.CredentialsRejected)]
    [InlineData(HttpStatusCode.Forbidden, "{\"detail\":\"this account has been closed\"}", UploadOutcome.AccountClosed)]
    [InlineData(HttpStatusCode.Forbidden, "{\"detail\":\"this API key is limited to the 'read' scope\"}", UploadOutcome.CredentialsRejected)]
    [InlineData(HttpStatusCode.ServiceUnavailable, "{\"detail\":\"malware scanner unavailable\"}", UploadOutcome.TransientFailure)]
    [InlineData(HttpStatusCode.InternalServerError, "", UploadOutcome.TransientFailure)]
    [InlineData(HttpStatusCode.BadGateway, "", UploadOutcome.TransientFailure)]
    public async Task Server_answers_map_to_what_the_agent_should_do(HttpStatusCode status, string body, UploadOutcome expected)
    {
        var result = await Upload(status, body);

        Assert.Equal(expected, result.Outcome);
    }

    private static async Task<string> SentBody(string? reference)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dt-{Guid.NewGuid():N}.dmp");
        File.WriteAllBytes(path, new byte[] { 0x4D, 0x44, 0x4D, 0x50 });
        try
        {
            string? body = null;
            var http = new HttpClient(new Scripted(async (request, ct) =>
            {
                body = await request.Content!.ReadAsStringAsync(ct);
                return new HttpResponseMessage(HttpStatusCode.Created);
            })) { BaseAddress = new Uri("https://api.example") };
            var result = await new DumpTriageApiClient(http).UploadDumpAsync(path, "dts_key", null, reference: reference);
            Assert.Equal(UploadOutcome.Success, result.Outcome);
            return body!;
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task The_reference_is_sent_as_its_own_field_before_the_file()
    {
        // dumptriage-tools#77: the label the report webhook echoes (website#3581).
        var body = await SentBody("client=Acme;ticket=INC-4021 é");

        var field = body.IndexOf("name=reference", StringComparison.Ordinal);
        Assert.True(field >= 0, body);
        Assert.Contains("client=Acme;ticket=INC-4021 é", body);
        Assert.True(field < body.IndexOf("name=file", StringComparison.Ordinal), body);
    }

    [Fact]
    public async Task No_reference_sends_no_field()
    {
        Assert.DoesNotContain("name=reference", await SentBody(null));
    }
}
