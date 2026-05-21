using System.Net;
using System.Text;
using BotNexus.Agent.Providers.Anthropic;
using BotNexus.Agent.Providers.Core;
using BotNexus.Agent.Providers.Core.Models;

namespace BotNexus.Agent.Providers.Anthropic.Tests;

public class StreamSetupTimeoutTests
{
    private static readonly string MinimalSsePayload = string.Join("\n",
        "event: message_start",
        "data: {\"type\":\"message_start\",\"message\":{\"id\":\"msg_1\"}}",
        "event: content_block_start",
        "data: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"text\"}}",
        "event: content_block_delta",
        "data: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"hello\"}}",
        "event: content_block_stop",
        "data: {\"type\":\"content_block_stop\",\"index\":0}",
        "event: message_delta",
        "data: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\"}}",
        "event: message_stop",
        "data: {\"type\":\"message_stop\"}");

    [Fact]
    public async Task Stream_StalledBeforeFirstToken_TimesOutAndReturnsError()
    {
        var handler = new StallingHandler();
        var provider = new AnthropicProvider(new HttpClient(handler));
        var model = TestHelpers.MakeModel();
        var context = TestHelpers.MakeContext();
        var options = new AnthropicOptions
        {
            ApiKey = "sk-ant-test",
            StreamSetupTimeoutMs = 150
        };

        var stream = provider.Stream(model, context, options);
        var result = await stream.GetResultAsync().WaitAsync(TimeSpan.FromSeconds(5));

        result.StopReason.ShouldBe(StopReason.Error);
    }

    [Fact]
    public async Task Stream_RespondsQuickly_CompletesNormally()
    {
        var handler = new ImmediateResponseHandler(MinimalSsePayload);
        var provider = new AnthropicProvider(new HttpClient(handler));
        var model = TestHelpers.MakeModel();
        var context = TestHelpers.MakeContext();
        var options = new AnthropicOptions
        {
            ApiKey = "sk-ant-test",
            StreamSetupTimeoutMs = 5000
        };

        var stream = provider.Stream(model, context, options);
        var result = await stream.GetResultAsync().WaitAsync(TimeSpan.FromSeconds(10));

        result.StopReason.ShouldBe(StopReason.Stop);
        result.Content.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task Stream_SetupTimeoutZero_DisablesTimeout_CompletesNormally()
    {
        var handler = new ImmediateResponseHandler(MinimalSsePayload);
        var provider = new AnthropicProvider(new HttpClient(handler));
        var model = TestHelpers.MakeModel();
        var context = TestHelpers.MakeContext();
        var options = new AnthropicOptions
        {
            ApiKey = "sk-ant-test",
            StreamSetupTimeoutMs = 0
        };

        var stream = provider.Stream(model, context, options);
        var result = await stream.GetResultAsync().WaitAsync(TimeSpan.FromSeconds(10));

        result.StopReason.ShouldBe(StopReason.Stop);
    }

    [Fact]
    public async Task Stream_CallerCancels_SurfacesAsAborted()
    {
        var handler = new StallingHandler();
        var provider = new AnthropicProvider(new HttpClient(handler));
        var model = TestHelpers.MakeModel();
        var context = TestHelpers.MakeContext();
        using var cts = new CancellationTokenSource(200);
        var options = new AnthropicOptions
        {
            ApiKey = "sk-ant-test",
            StreamSetupTimeoutMs = 30000,
            CancellationToken = cts.Token
        };

        var stream = provider.Stream(model, context, options);
        var result = await stream.GetResultAsync().WaitAsync(TimeSpan.FromSeconds(5));

        result.StopReason.ShouldBe(StopReason.Aborted);
    }

    private sealed class StallingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    private sealed class ImmediateResponseHandler(string sseBody) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(sseBody, Encoding.UTF8, "text/event-stream")
            };
            return Task.FromResult(response);
        }
    }
}
