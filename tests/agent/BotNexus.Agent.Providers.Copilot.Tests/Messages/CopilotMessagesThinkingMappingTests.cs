using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using BotNexus.Agent.Providers.Copilot.Messages;
using BotNexus.Agent.Providers.Core;
using BotNexus.Agent.Providers.Core.Models;

namespace BotNexus.Agent.Providers.Copilot.Tests.Messages;

/// <summary>
/// Issue #1702 - adaptive thinking effort mapping for Copilot Opus models.
/// Proves that Opus 4.8 (and 4.6) honour the Max thinking level by emitting
/// "max" effort, that mapping is gated by the SupportsExtraHighThinking
/// capability rather than a model-id literal, and that every thinking level
/// maps to a valid adaptive effort string. ExtraHigh on a Max-capable model
/// continues to map to "max" for parity with the prior 4.6 behaviour.
/// </summary>
public class CopilotMessagesThinkingMappingTests
{
    private static LlmModel BuildAdaptiveModel(string id, bool supportsExtraHigh) => new(
        Id: id,
        Name: id,
        Api: "github-copilot-messages",
        Provider: "github-copilot",
        BaseUrl: "https://api.enterprise.githubcopilot.com",
        Reasoning: true,
        Input: ["text", "image"],
        Cost: new ModelCost(0, 0, 0, 0),
        ContextWindow: 1000000,
        MaxTokens: 64000,
        SupportsExtraHighThinking: supportsExtraHigh);

    private static Context BuildContext() => new(
        SystemPrompt: "test",
        Messages: [new UserMessage(new UserMessageContent("hi"), 1_700_000_000_000L)],
        Tools: []);

    private static async Task<string?> CaptureEffortAsync(string modelId, ThinkingLevel level, bool supportsExtraHigh)
    {
        var handler = new RecordingHandler();
        var provider = new CopilotMessagesProvider(new HttpClient(handler));
        var model = BuildAdaptiveModel(modelId, supportsExtraHigh);

        var stream = provider.StreamSimple(model, BuildContext(), new SimpleStreamOptions
        {
            ApiKey = "test-token",
            Reasoning = level,
        });
        _ = await stream.GetResultAsync().WaitAsync(TimeSpan.FromSeconds(10));

        var body = JsonNode.Parse(handler.RequestBody!)!.AsObject();
        return body["output_config"]?["effort"]?.GetValue<string>();
    }

    [Fact]
    public async Task Opus48_Max_MapsToMaxEffort()
    {
        var effort = await CaptureEffortAsync("claude-opus-4.8", ThinkingLevel.Max, supportsExtraHigh: true);
        effort.ShouldBe("max", "Opus 4.8 must honour the Max thinking level instead of downgrading to high.");
    }

    [Fact]
    public async Task Opus46_Max_MapsToMaxEffort()
    {
        var effort = await CaptureEffortAsync("claude-opus-4.6", ThinkingLevel.Max, supportsExtraHigh: true);
        effort.ShouldBe("max");
    }

    [Fact]
    public async Task Opus48_ExtraHigh_MapsToMaxEffort()
    {
        var effort = await CaptureEffortAsync("claude-opus-4.8", ThinkingLevel.ExtraHigh, supportsExtraHigh: true);
        effort.ShouldBe("max", "Max-capable models map ExtraHigh to max effort, gated by capability not model id.");
    }

    [Fact]
    public async Task Max_OnNonCapableModel_DoesNotEmitMax()
    {
        // Capability gate: an adaptive model that does not advertise extra-high must not be sent "max".
        var effort = await CaptureEffortAsync("claude-sonnet-4.6", ThinkingLevel.Max, supportsExtraHigh: false);
        effort.ShouldBe("high");
    }

    [Theory]
    [InlineData(ThinkingLevel.Minimal, "low")]
    [InlineData(ThinkingLevel.Low, "low")]
    [InlineData(ThinkingLevel.Medium, "medium")]
    [InlineData(ThinkingLevel.High, "high")]
    [InlineData(ThinkingLevel.ExtraHigh, "max")]
    [InlineData(ThinkingLevel.Max, "max")]
    public async Task AllLevels_MapToValidEffort(ThinkingLevel level, string expected)
    {
        var effort = await CaptureEffortAsync("claude-opus-4.8", level, supportsExtraHigh: true);
        effort.ShouldBe(expected);
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public string? RequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            const string sse =
                "event: message_start\n" +
                "data: {\"type\":\"message_start\",\"message\":{\"id\":\"msg_01Fixture\"}}\n\n" +
                "event: message_stop\n" +
                "data: {\"type\":\"message_stop\"}\n\n";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(sse, Encoding.UTF8, "text/event-stream"),
            };
        }
    }
}
