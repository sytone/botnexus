using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using BotNexus.Agent.Providers.Copilot.Messages;
using BotNexus.Agent.Providers.Core;
using BotNexus.Agent.Providers.Core.Models;

namespace BotNexus.Agent.Providers.Copilot.Tests.Messages;

public class CopilotMessagesEffortFallbackTests
{
    private const string Rejection = "output_config.effort \"max\" is not supported by model claude-opus-4.6-20260205; supported values: [low, medium, high]";

    [Theory]
    [InlineData(Rejection, "claude-opus-4.6-20260205", "low,medium,high")]
    [InlineData("{\"error\":{\"message\":\"output_config.effort \\\"max\\\" is not supported by model claude-opus-4.6-20260205; supported values: [ low , medium , high ]\"}}", "claude-opus-4.6-20260205", "low,medium,high")]
    [InlineData("{\"message\":\"output_config.effort \\\"max\\\" is not supported by model claude-opus-4.6-20260205; supported values: [low medium high]\"}", "claude-opus-4.6-20260205", "low,medium,high")]
    public void TryParseRejection_RawAndJsonWrapped_ReturnsAcceptedKnownValues(string body, string expectedModel, string expectedValues)
    {
        var parsed = CopilotEffortCapabilityCache.TryParseRejection(body, "max", out var modelId, out var supported);

        parsed.ShouldBeTrue();
        modelId.ShouldBe(expectedModel);
        string.Join(',', supported).ShouldBe(expectedValues);
    }

    [Theory]
    [InlineData("output_config.effort \"max\" is bad", "max")]
    [InlineData("{\"error\":{\"message\":123}}", "max")]
    [InlineData("output_config.effort \"high\" is not supported by model x; supported values: [low, medium]", "max")]
    [InlineData("output_config.effort \"max\" is not supported by model x; supported values: [ultra, turbo]", "max")]
    public void TryParseRejection_MalformedUnrelatedOrUnknownOnly_ReturnsFalse(string body, string requested)
    {
        CopilotEffortCapabilityCache.TryParseRejection(body, requested, out _, out _).ShouldBeFalse();
    }

    [Theory]
    [InlineData("high", "high", "low,medium,high")]
    [InlineData("max", "high", "low,medium,high")]
    [InlineData("low", "medium", "medium,high")]
    public void SelectClosest_PrefersSameThenWeakerThenStronger(string requested, string expected, string supported)
    {
        CopilotEffortCapabilityCache.SelectClosest(requested, supported.Split(',')).ShouldBe(expected);
    }

    [Fact]
    public async Task Cache_BoundedConcurrentAndAliasKeyed_ClampsKnownInvalidTier()
    {
        var cache = new CopilotEffortCapabilityCache(capacity: 8);
        var tasks = Enumerable.Range(0, 64).Select(i => Task.Run(() =>
        {
            cache.Remember($"model-{i}", $"alias-{i}", ["low", "medium", "high"]);
            _ = cache.Clamp($"model-{i}", $"alias-{i}", "max");
        }));
        await Task.WhenAll(tasks);

        cache.Count.ShouldBeLessThanOrEqualTo(8);
        cache.Remember("authoritative", "requested-alias", ["low", "medium", "high"]);
        cache.Clamp("authoritative", "other", "max").ShouldBe("high");
        cache.Clamp("other", "requested-alias", "max").ShouldBe("high");
    }

    [Fact]
    public async Task Stream_ExactRejection_RetriesOnceAndCachesAlias()
    {
        var handler = new ScriptedHandler(BadRequest(Rejection), Success(), Success());
        var provider = new CopilotMessagesProvider(new HttpClient(handler));
        var model = BuildModel("claude-opus-alias");

        var first = await DriveAsync(provider, model);
        var second = await DriveAsync(provider, model);

        first.StopReason.ShouldBe(StopReason.Stop);
        second.StopReason.ShouldBe(StopReason.Stop);
        handler.Efforts.ShouldBe(["max", "high", "high"]);
    }

    [Fact]
    public async Task Stream_PersistentRejection_SendsTwoRequestsAndSurfacesSecondError()
    {
        var handler = new ScriptedHandler(BadRequest(Rejection), BadRequest(Rejection));
        var result = await DriveAsync(new CopilotMessagesProvider(new HttpClient(handler)), BuildModel("claude-opus-alias"));

        handler.Efforts.ShouldBe(["max", "high"]);
        result.StopReason.ShouldBe(StopReason.Error);
        result.ErrorMessage.ShouldNotBeNull();
        result.ErrorMessage.ShouldContain("HTTP 400");
    }

    [Theory]
    [InlineData("other bad request")]
    [InlineData("{\"error\":{\"message\":\"output_config.effort \\\"max\\\" is not supported by model x; supported values: [ultra]\"}}")]
    public async Task Stream_UnrelatedOrUnknownOnly_DoesNotRetry(string body)
    {
        var handler = new ScriptedHandler(BadRequest(body));
        var result = await DriveAsync(new CopilotMessagesProvider(new HttpClient(handler)), BuildModel("claude-opus-alias"));

        handler.Efforts.Count.ShouldBe(1);
        result.StopReason.ShouldBe(StopReason.Error);
    }

    [Fact]
    public async Task Stream_Cancellation_DoesNotRetry()
    {
        using var cts = new CancellationTokenSource();
        var handler = new CanceledHandler(cts);
        var result = await DriveAsync(new CopilotMessagesProvider(new HttpClient(handler)), BuildModel("claude-opus-alias"), cts.Token);

        handler.CallCount.ShouldBe(1);
        result.StopReason.ShouldBe(StopReason.Aborted);
    }

    private static async Task<AssistantMessage> DriveAsync(CopilotMessagesProvider provider, LlmModel model, CancellationToken cancellationToken = default)
    {
        var stream = provider.Stream(model, NewContext(), new CopilotMessagesOptions
        {
            ApiKey = "test-token",
            ThinkingEnabled = true,
            Effort = "max",
            CancellationToken = cancellationToken,
        });
        return await stream.GetResultAsync().WaitAsync(TimeSpan.FromSeconds(10));
    }

    private static LlmModel BuildModel(string id) => new(
        Id: id, Name: id, Api: CopilotMessagesProvider.ApiId, Provider: "github-copilot",
        BaseUrl: "https://api.example", Reasoning: true, Input: ["text"], Cost: new ModelCost(0, 0, 0, 0),
        ContextWindow: 200000, MaxTokens: 16000, SupportsExtraHighThinking: true);

    private static Context NewContext() => new("test", [new UserMessage(new UserMessageContent("hi"), 1700000000000L)], []);

    private static HttpResponseMessage BadRequest(string body) => new(HttpStatusCode.BadRequest)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private static HttpResponseMessage Success() => new(HttpStatusCode.OK)
    {
        Content = new StringContent("event: message_start\ndata: {\"type\":\"message_start\",\"message\":{\"id\":\"msg_1\"}}\n\nevent: message_stop\ndata: {\"type\":\"message_stop\"}\n\n", Encoding.UTF8, "text/event-stream"),
    };

    private sealed class ScriptedHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private int _next;
        public List<string> Efforts { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            Efforts.Add(JsonNode.Parse(body)!["output_config"]!["effort"]!.GetValue<string>());
            return responses[Interlocked.Increment(ref _next) - 1];
        }
    }

    private sealed class CanceledHandler(CancellationTokenSource cancellation) : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            cancellation.Cancel();
            return Task.FromCanceled<HttpResponseMessage>(cancellationToken);
        }
    }
}
