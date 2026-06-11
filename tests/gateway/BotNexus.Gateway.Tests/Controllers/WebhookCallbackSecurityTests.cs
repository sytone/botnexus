using System.Net;
using System.Net.Http;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Api.Controllers;
using BotNexus.Gateway.Dispatching;
using BotNexus.Gateway.Webhooks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace BotNexus.Gateway.Tests.Controllers;

public sealed class WebhookCallbackSecurityTests
{
    private readonly IWebhookRegistrationStore _registrations = Substitute.For<IWebhookRegistrationStore>();
    private readonly IWebhookRunStore _runs = Substitute.For<IWebhookRunStore>();
    private readonly IInboundMessageOrchestrator _orchestrator = Substitute.For<IInboundMessageOrchestrator>();
    private readonly IConversationStore _conversations = Substitute.For<IConversationStore>();
    private readonly ISessionStore _sessions = Substitute.For<ISessionStore>();
    private readonly IHttpClientFactory _httpClientFactory = Substitute.For<IHttpClientFactory>();

    [Theory]
    [InlineData("http://127.0.0.1/evil")]
    [InlineData("http://localhost/evil")]
    [InlineData("http://[::1]/evil")]
    [InlineData("http://169.254.169.254/latest/meta-data/")]
    [InlineData("http://10.0.0.1/internal")]
    [InlineData("http://172.16.0.1/internal")]
    [InlineData("http://192.168.1.1/internal")]
    [InlineData("http://metadata.google.internal/computeMetadata/v1/")]
    [InlineData("ftp://example.com/file")]
    public void IsCallbackUrlSafe_RejectsUnsafeUrls(string url)
    {
        var result = WebhookCallbackValidator.IsCallbackUrlSafe(url);

        Assert.False(result.IsSafe);
        Assert.NotNull(result.Reason);
    }

    [Theory]
    [InlineData("https://api.example.com/webhook/callback")]
    [InlineData("https://hooks.slack.com/services/123")]
    [InlineData("http://external.company.net:8080/callback")]
    public void IsCallbackUrlSafe_AcceptsPublicUrls(string url)
    {
        var result = WebhookCallbackValidator.IsCallbackUrlSafe(url);

        Assert.True(result.IsSafe);
        Assert.Null(result.Reason);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-url")]
    public void IsCallbackUrlSafe_RejectsInvalidUrls(string url)
    {
        var result = WebhookCallbackValidator.IsCallbackUrlSafe(url);

        Assert.False(result.IsSafe);
    }

    [Fact]
    public void IsCallbackUrlSafe_RejectsNullUrl()
    {
        var result = WebhookCallbackValidator.IsCallbackUrlSafe(null!);

        Assert.False(result.IsSafe);
    }
}
