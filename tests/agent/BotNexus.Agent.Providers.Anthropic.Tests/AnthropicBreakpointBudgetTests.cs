using System.Text.Json;
using System.Text.RegularExpressions;
using BotNexus.Agent.Providers.Core.Models;

namespace BotNexus.Agent.Providers.Anthropic.Tests;

/// <summary>
/// Pins the shared cache-breakpoint budget in <see cref="AnthropicRequestBuilder"/>.
///
/// Anthropic rejects a request carrying more than four <c>cache_control</c> markers. Before the
/// budget was centralised the OAuth path stamped two system blocks and up to three messages --
/// five markers -- so any OAuth conversation past two messages was refused outright. These tests
/// exist to keep the ceiling enforced and the allocation in prefix order (tools, then system,
/// then the newest messages).
/// </summary>
public class AnthropicBreakpointBudgetTests
{
    private static readonly long Ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void LongConversationWithTools_NeverExceedsTheBreakpointCeiling(bool isOAuthToken)
    {
        var body = Build(MakeContext(messageCount: 8, toolCount: 3), isOAuthToken);

        CountBreakpoints(body).ShouldBe(AnthropicRequestBuilder.MaxCacheBreakpoints);
    }

    [Fact]
    public void OAuth_StampsTheStablePromptAndNotThePreamble()
    {
        var body = Build(MakeContext(messageCount: 4, toolCount: 2), isOAuthToken: true);

        var system = body["system"]!.AsArray();
        system.Count.ShouldBe(2);
        // The preamble and the stable prompt are adjacent and equally stable; one marker covers
        // both, and spending a second on the preamble is what pushed the request over the ceiling.
        system[0]!["cache_control"].ShouldBeNull();
        system[1]!["cache_control"].ShouldNotBeNull();
    }

    [Fact]
    public void ToolsCarryTheirOwnBreakpointOnTheLastEntry()
    {
        var body = Build(MakeContext(messageCount: 2, toolCount: 3), isOAuthToken: false);

        var tools = body["tools"]!.AsArray();
        tools[0]!["cache_control"].ShouldBeNull();
        tools[1]!["cache_control"].ShouldBeNull();
        tools[2]!["cache_control"].ShouldNotBeNull();
    }

    [Fact]
    public void NoSystemPrompt_StillGivesToolsABreakpoint()
    {
        var context = MakeContext(messageCount: 2, toolCount: 2) with { SystemPrompt = null };

        var body = Build(context, isOAuthToken: false);

        body["system"].ShouldBeNull();
        body["tools"]!.AsArray()[^1]!["cache_control"].ShouldNotBeNull();
    }

    [Fact]
    public void CacheRetentionNone_PlacesNoBreakpointsAnywhere()
    {
        var body = Build(
            MakeContext(messageCount: 6, toolCount: 2),
            isOAuthToken: true,
            retention: CacheRetention.None);

        CountBreakpoints(body).ShouldBe(0);
    }

    [Fact]
    public void LongTtl_AppliesToEverySegmentItStamps()
    {
        var body = Build(
            MakeContext(messageCount: 4, toolCount: 2),
            isOAuthToken: false,
            retention: CacheRetention.Long);

        var json = body.ToJsonString();
        // Every marker this builder places comes from one BuildCacheControl call, so the 1h TTL
        // must reach the tools array as well as the messages -- a mixed TTL would silently create
        // two entries with different lifetimes for the same prefix.
        Regex.Matches(json, "\"ttl\":\"1h\"").Count.ShouldBe(CountBreakpoints(body));
    }

    private static System.Text.Json.Nodes.JsonObject Build(
        Core.Models.Context context,
        bool isOAuthToken,
        CacheRetention retention = CacheRetention.Short)
        => AnthropicRequestBuilder.BuildRequestBody(
            TestHelpers.MakeModel(),
            context,
            new Core.StreamOptions { CacheRetention = retention },
            anthropicOpts: null,
            isOAuthToken: isOAuthToken,
            isAdaptiveThinkingModel: _ => false);

    private static Core.Models.Context MakeContext(int messageCount, int toolCount)
    {
        var messages = new List<Message>();
        for (var i = 0; i < messageCount; i++)
        {
            messages.Add(i % 2 == 0
                ? new UserMessage(new UserMessageContent($"user turn {i}"), Ts + i)
                : new AssistantMessage(
                    Content: [new TextContent($"assistant turn {i}")],
                    Api: "anthropic-messages",
                    Provider: "anthropic",
                    ModelId: "claude-sonnet-4",
                    Usage: Usage.Empty(),
                    StopReason: StopReason.Stop,
                    ErrorMessage: null,
                    ResponseId: $"resp_{i}",
                    Timestamp: Ts + i));
        }

        var tools = new List<Tool>();
        for (var i = 0; i < toolCount; i++)
        {
            tools.Add(new Tool(
                $"tool_{i}",
                $"Tool number {i}.",
                JsonDocument.Parse("""{"type":"object","properties":{}}""").RootElement));
        }

        return new Core.Models.Context(
            SystemPrompt: "Stable instructions that never change.",
            Messages: messages,
            Tools: tools);
    }

    private static int CountBreakpoints(System.Text.Json.Nodes.JsonObject body)
        => Regex.Matches(body.ToJsonString(), "\"cache_control\"").Count;
}
