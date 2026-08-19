using System.Text.Json.Nodes;
using BotNexus.Agent.Providers.Copilot.Responses;
using BotNexus.Agent.Providers.Core;
using BotNexus.Agent.Providers.Core.Models;

namespace BotNexus.Agent.Providers.Copilot.Tests.Responses;

/// <summary>
/// #3417: pins the <c>prompt_cache_key</c> branch of
/// <see cref="CopilotResponsesRequestBuilder"/>.
///
/// <para>
/// This branch was DEAD for every background gateway request. The gate is
/// <c>!string.IsNullOrWhiteSpace(options.SessionId)</c>, and the two background callers
/// (<c>LlmSessionCompactor</c>, <c>ConversationAutoTitleService</c>) built their options without
/// ever setting <c>SessionId</c>, so compaction - the single largest prompt the gateway ever sends -
/// never carried a cache key. These tests exercise the branch directly with a compaction-shaped
/// request so the emitted key is a demonstrated property rather than a claim, and pin the
/// blank-id case as ABSENT rather than empty: an empty <c>prompt_cache_key</c> on the wire would be
/// a worse failure than no key at all.
/// </para>
/// </summary>
public sealed class CopilotResponsesPromptCacheKeyTests
{
    private static readonly LlmModel CopilotModel = new(
        Id: "gpt-4.1",
        Name: "gpt-4.1",
        Api: "github-copilot-responses",
        Provider: "github-copilot",
        BaseUrl: "https://api.enterprise.githubcopilot.com",
        Reasoning: false,
        Input: ["text"],
        Cost: new ModelCost(0, 0, 0, 0),
        ContextWindow: 128000,
        MaxTokens: 4096);

    [Fact]
    public void Build_CompactionShapedRequest_WithSessionId_EmitsPromptCacheKey()
    {
        // A compaction request: one very large user message carrying the summarization prompt,
        // no tools, no system prompt - exactly the shape LlmSessionCompactor sends.
        const string SessionId = "sess-compaction-7f3a91";
        var payload = BuildCompactionPayload(new SimpleStreamOptions { SessionId = SessionId });

        payload.ContainsKey("prompt_cache_key").ShouldBeTrue(
            "the previously dead prompt_cache_key branch must fire for a compaction-shaped request");
        payload["prompt_cache_key"]!.GetValue<string>().ShouldBe(SessionId);
    }

    [Fact]
    public void Build_WithSessionId_AndLongCacheRetention_StillEmitsPromptCacheKey()
    {
        // The gate is (CacheRetention != None) && SessionId non-blank; Long must not suppress it.
        const string SessionId = "sess-long-retention";
        var payload = BuildCompactionPayload(
            new SimpleStreamOptions { SessionId = SessionId, CacheRetention = CacheRetention.Long });

        payload["prompt_cache_key"]!.GetValue<string>().ShouldBe(SessionId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Build_NullOrBlankSessionId_OmitsPromptCacheKeyEntirely(string? sessionId)
    {
        var payload = BuildCompactionPayload(new SimpleStreamOptions { SessionId = sessionId });

        // ABSENT, not empty. An empty key would be sent to the provider as a real cache key value.
        payload.ContainsKey("prompt_cache_key").ShouldBeFalse(
            "a blank session id must leave prompt_cache_key absent rather than emitting an empty key");
    }

    [Fact]
    public void Build_CacheRetentionNone_OmitsPromptCacheKey_EvenWithSessionId()
    {
        // Behaviour parity: an explicit opt-out of caching still wins over a present session id.
        var payload = BuildCompactionPayload(
            new SimpleStreamOptions { SessionId = "sess-abc", CacheRetention = CacheRetention.None });

        payload.ContainsKey("prompt_cache_key").ShouldBeFalse();
    }

    private static JsonObject BuildCompactionPayload(SimpleStreamOptions options)
    {
        var summarizationPrompt =
            "Summarize the conversation below.\n\nConversation:\n" + new string('x', 4096);

        var messages = new List<Message>
        {
            new UserMessage(
                new UserMessageContent(summarizationPrompt),
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
        };

        return CopilotResponsesRequestBuilder.Build(
            CopilotModel,
            systemPrompt: null,
            messages,
            tools: null,
            options,
            convertMessages: static (msgs, _) =>
            {
                var array = new JsonArray();
                for (var i = 0; i < msgs.Count; i++)
                {
                    array.Add(new JsonObject { ["role"] = "user", ["content"] = "stub" });
                }
                return array;
            },
            convertTools: static _ => new JsonArray());
    }
}
