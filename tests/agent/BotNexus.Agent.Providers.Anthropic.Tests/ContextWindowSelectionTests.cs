using System.Net;
using System.Text;
using BotNexus.Agent.Providers.Anthropic;
using BotNexus.Agent.Providers.Core;
using BotNexus.Agent.Providers.Core.Models;

namespace BotNexus.Agent.Providers.Anthropic.Tests;

/// <summary>
/// Context-window selection on the Anthropic messages path. The 1M beta header
/// (context-1m-2025-08-07) is emitted on the Anthropic-direct path ONLY when 1M is the
/// selected context window. Copilot is fixed at 200K and never emits the header or a toggle.
/// </summary>
public sealed class ContextWindowSelectionTests
{
    private const string OneMillionBetaToken = "context-1m-2025-08-07";
    private const int OneMillion = 1_000_000;
    private const int TwoHundredK = 200_000;

    [Fact]
    public async Task AnthropicDirect_TwoHundredKSelected_DoesNotEmitOneMillionBetaHeader()
    {
        var handler = new RecordingHandler(_ => SseResponse());
        var provider = new AnthropicProvider(new HttpClient(handler));
        var model = TestHelpers.MakeModel(provider: "anthropic");
        var context = TestHelpers.MakeContext();
        var options = new AnthropicOptions { ApiKey = "test-key", ContextWindow = TwoHundredK };

        var stream = provider.Stream(model, context, options);
        _ = await stream.GetResultAsync().WaitAsync(TimeSpan.FromSeconds(10));

        BetaHeaderValue(handler).ShouldNotContain(OneMillionBetaToken);
    }

    [Fact]
    public async Task AnthropicDirect_OneMillionSelected_EmitsOneMillionBetaHeader()
    {
        var handler = new RecordingHandler(_ => SseResponse());
        var provider = new AnthropicProvider(new HttpClient(handler));
        var model = TestHelpers.MakeModel(provider: "anthropic");
        var context = TestHelpers.MakeContext();
        var options = new AnthropicOptions { ApiKey = "test-key", ContextWindow = OneMillion };

        var stream = provider.Stream(model, context, options);
        _ = await stream.GetResultAsync().WaitAsync(TimeSpan.FromSeconds(10));

        handler.RequestHeaders.ShouldContainKey("anthropic-beta");
        handler.RequestHeaders["anthropic-beta"].ShouldContain(OneMillionBetaToken);
    }

    [Fact]
    public async Task AnthropicDirect_ContextWindowUnset_DoesNotEmitOneMillionBetaHeader()
    {
        var handler = new RecordingHandler(_ => SseResponse());
        var provider = new AnthropicProvider(new HttpClient(handler));
        var model = TestHelpers.MakeModel(provider: "anthropic");
        var context = TestHelpers.MakeContext();

        // No ContextWindow set -> default behaviour, no beta header.
        var stream = provider.Stream(model, context, new AnthropicOptions { ApiKey = "test-key" });
        _ = await stream.GetResultAsync().WaitAsync(TimeSpan.FromSeconds(10));

        BetaHeaderValue(handler).ShouldNotContain(OneMillionBetaToken);
    }

    [Fact]
    public async Task Copilot_OneMillionRequested_NeverEmitsOneMillionBetaHeader()
    {
        var handler = new RecordingHandler(_ => SseResponse());
        var provider = new AnthropicProvider(new HttpClient(handler));
        var model = TestHelpers.MakeModel(provider: "github-copilot");
        var context = TestHelpers.MakeContext();

        // Even if a caller asks for 1M, Copilot is fixed at 200K: no beta header.
        var options = new AnthropicOptions { ApiKey = "test-key", ContextWindow = OneMillion };
        var stream = provider.Stream(model, context, options);
        _ = await stream.GetResultAsync().WaitAsync(TimeSpan.FromSeconds(10));

        BetaHeaderValue(handler).ShouldNotContain(OneMillionBetaToken);
    }

    [Fact]
    public async Task Copilot_DefaultSelection_DoesNotEmitOneMillionBetaHeader()
    {
        var handler = new RecordingHandler(_ => SseResponse());
        var provider = new AnthropicProvider(new HttpClient(handler));
        var model = TestHelpers.MakeModel(provider: "github-copilot");
        var context = TestHelpers.MakeContext();

        var stream = provider.Stream(model, context, new AnthropicOptions { ApiKey = "test-key" });
        _ = await stream.GetResultAsync().WaitAsync(TimeSpan.FromSeconds(10));

        BetaHeaderValue(handler).ShouldNotContain(OneMillionBetaToken);
    }

    private static string BetaHeaderValue(RecordingHandler handler) =>
        handler.RequestHeaders.TryGetValue("anthropic-beta", out var value) ? value : string.Empty;

    private static HttpResponseMessage SseResponse() =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "event: message_start\n" +
                "data: {\"type\":\"message_start\",\"message\":{\"id\":\"msg_1\"}}\n\n" +
                "event: message_stop\n" +
                "data: {\"type\":\"message_stop\"}\n",
                Encoding.UTF8,
                "text/event-stream")
        };

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        public Dictionary<string, string> RequestHeaders { get; private set; } = new(StringComparer.OrdinalIgnoreCase);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestHeaders = request.Headers.ToDictionary(
                header => header.Key,
                header => string.Join(",", header.Value),
                StringComparer.OrdinalIgnoreCase);
            if (request.Content is not null)
                _ = await request.Content.ReadAsStringAsync(cancellationToken);

            return responseFactory(request);
        }
    }
}
