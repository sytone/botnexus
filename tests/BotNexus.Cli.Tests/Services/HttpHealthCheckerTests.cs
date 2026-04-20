using System.Net;
using BotNexus.Cli.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace BotNexus.Cli.Tests.Services;

public sealed class HttpHealthCheckerTests
{
    [Fact]
    public async Task WaitForHealthyAsync_WhenEndpointReturns200_ReturnsTrue()
    {
        // Use a mock HTTP message handler to simulate successful health check
        var handler = new MockHttpMessageHandler(HttpStatusCode.OK);
        var httpClient = new HttpClient(handler);
        var checker = new HttpHealthChecker(NullLogger<HttpHealthChecker>.Instance);

        // Use reflection to replace the internal HttpClient
        var httpClientField = typeof(HttpHealthChecker).GetField("_httpClient", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        httpClientField!.SetValue(checker, httpClient);

        var result = await checker.WaitForHealthyAsync(
            "http://localhost:5005/health",
            TimeSpan.FromSeconds(5),
            CancellationToken.None);

        result.ShouldBeTrue();
    }

    [Fact]
    public async Task WaitForHealthyAsync_WhenEndpointAlwaysReturns500_ReturnsFalse()
    {
        var handler = new MockHttpMessageHandler(HttpStatusCode.InternalServerError);
        var httpClient = new HttpClient(handler);
        var checker = new HttpHealthChecker(NullLogger<HttpHealthChecker>.Instance);

        var httpClientField = typeof(HttpHealthChecker).GetField("_httpClient", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        httpClientField!.SetValue(checker, httpClient);

        var result = await checker.WaitForHealthyAsync(
            "http://localhost:5005/health",
            TimeSpan.FromSeconds(1), // Short timeout for faster test
            CancellationToken.None);

        result.ShouldBeFalse();
    }

    [Fact]
    public async Task WaitForHealthyAsync_WhenEndpointUnavailable_ReturnsFalseOnTimeout()
    {
        var handler = new MockHttpMessageHandler(throwException: true);
        var httpClient = new HttpClient(handler);
        var checker = new HttpHealthChecker(NullLogger<HttpHealthChecker>.Instance);

        var httpClientField = typeof(HttpHealthChecker).GetField("_httpClient", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        httpClientField!.SetValue(checker, httpClient);

        var result = await checker.WaitForHealthyAsync(
            "http://localhost:5005/health",
            TimeSpan.FromSeconds(1),
            CancellationToken.None);

        result.ShouldBeFalse();
    }

    [Fact]
    public async Task WaitForHealthyAsync_WhenCancelled_ThrowsOperationCanceledException()
    {
        var handler = new MockHttpMessageHandler(HttpStatusCode.InternalServerError);
        var httpClient = new HttpClient(handler);
        var checker = new HttpHealthChecker(NullLogger<HttpHealthChecker>.Instance);

        var httpClientField = typeof(HttpHealthChecker).GetField("_httpClient", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        httpClientField!.SetValue(checker, httpClient);

        var cts = new CancellationTokenSource();
        cts.CancelAfter(100); // Cancel after 100ms

        await Should.ThrowAsync<OperationCanceledException>(async () =>
        {
            await checker.WaitForHealthyAsync(
                "http://localhost:5005/health",
                TimeSpan.FromSeconds(10),
                cts.Token);
        });
    }

    [Fact]
    public async Task WaitForHealthyAsync_WithExponentialBackoff_RetriesMultipleTimes()
    {
        // Simulate endpoint that fails twice, then succeeds
        var handler = new CountingMockHttpMessageHandler(
            responses: new[]
            {
                HttpStatusCode.ServiceUnavailable,
                HttpStatusCode.ServiceUnavailable,
                HttpStatusCode.OK
            });
        var httpClient = new HttpClient(handler);
        var checker = new HttpHealthChecker(NullLogger<HttpHealthChecker>.Instance);

        var httpClientField = typeof(HttpHealthChecker).GetField("_httpClient", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        httpClientField!.SetValue(checker, httpClient);

        var result = await checker.WaitForHealthyAsync(
            "http://localhost:5005/health",
            TimeSpan.FromSeconds(5),
            CancellationToken.None);

        result.ShouldBeTrue();
        handler.RequestCount.ShouldBe(3); // Verify it tried 3 times
    }

    [Fact]
    public async Task WaitForHealthyAsync_WhenTimeoutReached_StopsRetrying()
    {
        // Always fail
        var handler = new MockHttpMessageHandler(HttpStatusCode.ServiceUnavailable);
        var httpClient = new HttpClient(handler);
        var checker = new HttpHealthChecker(NullLogger<HttpHealthChecker>.Instance);

        var httpClientField = typeof(HttpHealthChecker).GetField("_httpClient", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        httpClientField!.SetValue(checker, httpClient);

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var result = await checker.WaitForHealthyAsync(
            "http://localhost:5005/health",
            TimeSpan.FromSeconds(1),
            CancellationToken.None);
        stopwatch.Stop();

        result.ShouldBeFalse();
        // Should respect the timeout (allow some tolerance)
        stopwatch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(2));
    }

    // Mock HTTP message handler for testing
    private class MockHttpMessageHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode? _statusCode;
        private readonly bool _throwException;

        public MockHttpMessageHandler(HttpStatusCode statusCode)
        {
            _statusCode = statusCode;
            _throwException = false;
        }

        public MockHttpMessageHandler(bool throwException)
        {
            _statusCode = null;
            _throwException = throwException;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (_throwException)
            {
                throw new HttpRequestException("Connection refused");
            }

            return Task.FromResult(new HttpResponseMessage(_statusCode!.Value));
        }
    }

    // Mock handler that counts requests and returns different responses
    private class CountingMockHttpMessageHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode[] _responses;
        private int _currentIndex;

        public int RequestCount { get; private set; }

        public CountingMockHttpMessageHandler(HttpStatusCode[] responses)
        {
            _responses = responses;
            _currentIndex = 0;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;

            var statusCode = _currentIndex < _responses.Length
                ? _responses[_currentIndex]
                : _responses[^1]; // Use last response if we run out

            _currentIndex++;

            return Task.FromResult(new HttpResponseMessage(statusCode));
        }
    }
}
