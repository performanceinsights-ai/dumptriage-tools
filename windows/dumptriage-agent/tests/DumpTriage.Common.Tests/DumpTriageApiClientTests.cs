using System.Net;
using DumpTriage.Common.Upload;

namespace DumpTriage.Common.Tests;

public class DumpTriageApiClientTests
{
    private sealed class StubHandler(HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent("body") });
    }

    [Theory]
    [InlineData(HttpStatusCode.Created, UploadOutcome.Success)]
    [InlineData(HttpStatusCode.BadRequest, UploadOutcome.RejectedPermanently)]
    [InlineData(HttpStatusCode.Unauthorized, UploadOutcome.AuthFailed)]
    [InlineData(HttpStatusCode.Forbidden, UploadOutcome.AuthFailed)]
    [InlineData(HttpStatusCode.TooManyRequests, UploadOutcome.QuotaExceeded)]
    [InlineData(HttpStatusCode.ServiceUnavailable, UploadOutcome.TransientFailure)]
    [InlineData(HttpStatusCode.InternalServerError, UploadOutcome.TransientFailure)]
    public async Task MapsStatusToOutcome(HttpStatusCode status, UploadOutcome expected)
    {
        var path = Path.GetTempFileName();
        try
        {
            var http = new HttpClient(new StubHandler(status)) { BaseAddress = new Uri("http://localhost") };
            var result = await new DumpTriageApiClient(http).UploadDumpAsync(path, "key", null);
            Assert.Equal(expected, result.Outcome);
            Assert.Equal(expected == UploadOutcome.TransientFailure, result.ShouldRetry);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
