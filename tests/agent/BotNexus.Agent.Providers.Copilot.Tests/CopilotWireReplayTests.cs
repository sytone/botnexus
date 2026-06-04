using System.Net;
using System.Text;
using BotNexus.Agent.Providers.Anthropic;
using BotNexus.Agent.Providers.Core;
using BotNexus.Agent.Providers.Core.Models;

namespace BotNexus.Agent.Providers.Copilot.Tests;

/// <summary>
/// Replay regression harness for the Copilot carve-out (#810, Phase 0b).
/// Feeds recorded Copilot SSE bodies through today's parsers and asserts the
/// resulting <see cref="StreamResult"/> shape. The dedicated
/// <c>CopilotProvider</c> introduced in Phase 1 must produce identical results
/// from the same fixtures, so any divergence is a regression caught here.
/// </summary>
public class CopilotWireReplayTests
{
    private static readonly string FixtureRoot = Path.Combine(
        AppContext.BaseDirectory,
        "Fixtures",
        "Wire");

    public static TheoryData<string, string> MessagesFixtures()
    {
        var data = new TheoryData<string, string>();
        var dir = Path.Combine(FixtureRoot, "messages");
        if (!Directory.Exists(dir))
        {
            return data;
        }

        foreach (var modelDir in Directory.EnumerateDirectories(dir).OrderBy(p => p))
        {
            var model = Path.GetFileName(modelDir);
            foreach (var body in Directory.EnumerateFiles(modelDir, "*.body.txt").OrderBy(p => p))
            {
                data.Add(model, Path.GetFileName(body));
            }
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(MessagesFixtures))]
    public async Task MessagesFixture_ReplaysThroughAnthropicProvider(string modelId, string bodyFileName)
    {
        var bodyPath = Path.Combine(FixtureRoot, "messages", modelId, bodyFileName);
        File.Exists(bodyPath).ShouldBeTrue($"missing fixture: {bodyPath}");

        var body = await File.ReadAllTextAsync(bodyPath);
        var handler = new RecordingHandler(_ => SseResponse(body));
        var provider = new AnthropicProvider(new HttpClient(handler));

        // Copilot serves the Anthropic Messages schema under
        // api.enterprise.githubcopilot.com; today the provider doesn't care
        // about the BaseUrl shape, only that the SSE body parses cleanly.
        var model = new LlmModel(
            Id: modelId,
            Name: modelId,
            Api: "anthropic-messages",
            Provider: "github-copilot",
            BaseUrl: "https://api.enterprise.githubcopilot.com",
            Reasoning: true,
            Input: ["text", "image"],
            Cost: new ModelCost(0, 0, 0, 0),
            ContextWindow: 200000,
            MaxTokens: 16384);

        var context = new Context(
            SystemPrompt: "replay",
            Messages: [new UserMessage(new UserMessageContent("replay"), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())]);

        var stream = provider.Stream(model, context, new StreamOptions { ApiKey = "test-key" });
        var result = await stream.GetResultAsync().WaitAsync(TimeSpan.FromSeconds(10));

        // The parser must produce a non-error terminal frame.
        result.ShouldNotBeNull();
        result.StopReason.ShouldNotBe(StopReason.Error);
        result.ErrorMessage.ShouldBeNull();

        // Usage must be populated — every recorded fixture carries a
        // message_delta with output_tokens, so a zero output count means the
        // parser silently dropped the event.
        (result.Usage.Input + result.Usage.Output).ShouldBeGreaterThan(0,
            $"parser produced empty usage for {modelId}/{bodyFileName}");

        // At least one content block must have been emitted (text or
        // thinking). Empty Content means the parser silently dropped every
        // content_block_* event in the recorded stream.
        result.Content.ShouldNotBeEmpty(
            $"parser produced no content blocks for {modelId}/{bodyFileName}");
    }

    private static HttpResponseMessage SseResponse(string payload) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(payload, Encoding.UTF8, "text/event-stream"),
        };

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(responseFactory(request));
    }
}
