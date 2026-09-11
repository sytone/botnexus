using BotNexus.Agent.Core.Types;
using BotNexus.Gateway.Isolation;

namespace BotNexus.Gateway.Tests.Isolation;

/// <summary>
/// The blocking path must report the same four usage components the streaming path does.
///
/// <para>
/// It used to build the four-field <c>AgentResponseUsage</c> from two of them, silently dropping
/// CacheRead and CacheWrite. On a cache-aware provider those are the two components that GROW: the
/// last cache breakpoint sits on the newest user message, so system prompt, tools and the entire
/// conversation are billed to cache tokens, and Anthropic excludes those from input_tokens.
/// </para>
///
/// <para>
/// The result was a REST usage figure structurally incapable of moving. Measured on a live gateway
/// it read 1222 / 1222 / 1222 across three turns, and stayed at 1221 when handed an 11.8 KB
/// message. Anything costing on that number under-reports the prompt by the whole conversation.
/// </para>
/// </summary>
public sealed class BlockingResponseUsageTests
{
    [Fact]
    public void BuildResponse_CarriesTheCacheComponents()
    {
        var response = InProcessAgentHandle.BuildResponse(
            [Assistant(new AgentUsage(InputTokens: 1222, OutputTokens: 4, CacheRead: 9100, CacheWrite: 640))]);

        response.Usage.ShouldNotBeNull();
        response.Usage!.InputTokens.ShouldBe(1222);
        response.Usage.OutputTokens.ShouldBe(4);
        // The two that were dropped. Without these the caller sees 1222 for an ~11,000-token prompt.
        response.Usage.CacheRead.ShouldBe(9100);
        response.Usage.CacheWrite.ShouldBe(640);
    }

    [Fact]
    public void BuildResponse_ReadsTheLastAssistantTurn()
    {
        var response = InProcessAgentHandle.BuildResponse(
        [
            Assistant(new AgentUsage(InputTokens: 1, OutputTokens: 1, CacheRead: 1, CacheWrite: 1)),
            Assistant(new AgentUsage(InputTokens: 2, OutputTokens: 2, CacheRead: 2, CacheWrite: 2)),
        ]);

        response.Usage!.CacheRead.ShouldBe(2);
    }

    [Fact]
    public void BuildResponse_PreservesAbsentCountsAsNullRatherThanZero()
    {
        // A provider that reports nothing must stay distinguishable from one reporting a real zero,
        // or "no cache activity" and "not measured" collapse into the same number.
        var response = InProcessAgentHandle.BuildResponse(
            [Assistant(new AgentUsage(InputTokens: 10, OutputTokens: 2))]);

        response.Usage!.CacheRead.ShouldBeNull();
        response.Usage.CacheWrite.ShouldBeNull();
    }

    [Fact]
    public void BuildResponse_WithNoAssistantTurn_ReportsNoUsage()
    {
        InProcessAgentHandle.BuildResponse([]).Usage.ShouldBeNull();
    }

    private static AssistantAgentMessage Assistant(AgentUsage usage) => new("ok", Usage: usage);
}
