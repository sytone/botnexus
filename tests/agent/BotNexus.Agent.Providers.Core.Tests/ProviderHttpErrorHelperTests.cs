using System.Net;
using System.Net.Http.Headers;

namespace BotNexus.Agent.Providers.Core.Tests;

public sealed class ProviderHttpErrorHelperTests
{
    [Fact]
    public void ThrowForFailedResponse_429WithRetryAfterSeconds_ThrowsProviderRateLimitException()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(10));

        var ex = Assert.Throws<ProviderRateLimitException>(() =>
            ProviderHttpErrorHelper.ThrowForFailedResponse(response, "rate limited", "TestProvider"));

        Assert.Equal(System.Net.HttpStatusCode.TooManyRequests, ex.StatusCode);
        Assert.NotNull(ex.RetryAfter);
        Assert.Equal(TimeSpan.FromSeconds(10), ex.RetryAfter);
        Assert.Contains("TestProvider", ex.Message);
    }

    [Fact]
    public void ThrowForFailedResponse_429WithoutRetryAfter_ThrowsProviderRateLimitExceptionNullDelay()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);

        var ex = Assert.Throws<ProviderRateLimitException>(() =>
            ProviderHttpErrorHelper.ThrowForFailedResponse(response, "rate limited", "TestProvider"));

        Assert.Equal(System.Net.HttpStatusCode.TooManyRequests, ex.StatusCode);
        Assert.Null(ex.RetryAfter);
    }

    [Fact]
    public void ThrowForFailedResponse_429WithLargeRetryAfter_CapsAt2Minutes()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(300));

        var ex = Assert.Throws<ProviderRateLimitException>(() =>
            ProviderHttpErrorHelper.ThrowForFailedResponse(response, "rate limited", "TestProvider"));

        Assert.NotNull(ex.RetryAfter);
        Assert.Equal(TimeSpan.FromMinutes(2), ex.RetryAfter);
    }

    [Fact]
    public void ThrowForFailedResponse_500Error_ThrowsHttpRequestException()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.InternalServerError);

        var ex = Assert.Throws<HttpRequestException>(() =>
            ProviderHttpErrorHelper.ThrowForFailedResponse(response, "server error", "TestProvider"));

        Assert.IsNotType<ProviderRateLimitException>(ex);
        Assert.Contains("500", ex.Message);
    }

    [Fact]
    public void ThrowForFailedResponse_401Error_ThrowsProviderAuthenticationException()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.Unauthorized);

        var ex = Assert.Throws<ProviderAuthenticationException>(() =>
            ProviderHttpErrorHelper.ThrowForFailedResponse(response, "unauthorized", "TestProvider"));

        Assert.IsNotType<ProviderRateLimitException>(ex);
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, ex.StatusCode);
        Assert.Equal("TestProvider", ex.ProviderName);
    }

    [Fact]
    public void ThrowForFailedResponse_401Error_MessageIsActionableAndNamesProvider()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.Unauthorized);

        var ex = Assert.Throws<ProviderAuthenticationException>(() =>
            ProviderHttpErrorHelper.ThrowForFailedResponse(response, "unauthorized", "OpenAI"));

        // The message must name the provider and tell the user what to do, so the
        // failure self-diagnoses instead of walking the fallback ladder silently.
        Assert.Contains("OpenAI", ex.Message);
        Assert.Contains("401", ex.Message);
        Assert.Contains("API key", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ThrowForFailedResponse_403Error_ThrowsProviderAuthenticationException()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.Forbidden);

        var ex = Assert.Throws<ProviderAuthenticationException>(() =>
            ProviderHttpErrorHelper.ThrowForFailedResponse(response, "forbidden", "TestProvider"));

        Assert.Equal(System.Net.HttpStatusCode.Forbidden, ex.StatusCode);
        Assert.Contains("TestProvider", ex.Message);
    }

    [Fact]
    public void ThrowForFailedResponse_AuthExceptionIsHttpRequestException()
    {
        // ProviderAuthenticationException derives from HttpRequestException so existing
        // catch sites that expect HttpRequestException keep working, and the actionable
        // message flows through Agent.cs's ex.Message surfacing path unchanged.
        using var response = new HttpResponseMessage(HttpStatusCode.Unauthorized);

        var ex = Assert.Throws<ProviderAuthenticationException>(() =>
            ProviderHttpErrorHelper.ThrowForFailedResponse(response, "unauthorized", "TestProvider"));

        Assert.IsAssignableFrom<HttpRequestException>(ex);
    }

    [Fact]
    public void ThrowForFailedResponse_429WithDateRetryAfter_ParsesFutureDate()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        var futureDate = DateTimeOffset.UtcNow.AddSeconds(15);
        response.Headers.RetryAfter = new RetryConditionHeaderValue(futureDate);

        var ex = Assert.Throws<ProviderRateLimitException>(() =>
            ProviderHttpErrorHelper.ThrowForFailedResponse(response, "rate limited", "TestProvider"));

        Assert.NotNull(ex.RetryAfter);
        // Should be approximately 15 seconds (within timing tolerance)
        Assert.InRange(ex.RetryAfter.Value.TotalSeconds, 10, 20);
    }
}
