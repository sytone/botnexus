using BotNexus.Agent.Providers.Core.Models;

namespace BotNexus.Agent.Providers.Anthropic.Tests;

/// <summary>
/// Tests for ApplyMultiBreakpointCacheControl -- the system_and_3 strategy
/// that places cache breakpoints on the last 3 non-system messages.
/// Covers Anthropic Issue #804 (Phase 2 multi-breakpoint caching).
/// </summary>
public class AnthropicCacheControlTests
{
    private const string AnthropicBaseUrl = "https://api.anthropic.com";

    // -----------------------------------------------------------------------
    // BuildCacheControl tests
    // -----------------------------------------------------------------------

    [Fact]
    public void BuildCacheControl_None_ReturnsNull()
    {
        var result = AnthropicMessageConverter.BuildCacheControl(CacheRetention.None, AnthropicBaseUrl);
        result.ShouldBeNull();
    }

    [Fact]
    public void BuildCacheControl_Short_ReturnsEphemeral()
    {
        var result = AnthropicMessageConverter.BuildCacheControl(CacheRetention.Short, AnthropicBaseUrl);
        result.ShouldNotBeNull();
        result!["type"].ShouldBe("ephemeral");
        result.ContainsKey("ttl").ShouldBeFalse();
    }

    [Fact]
    public void BuildCacheControl_Long_AnthropicUrl_ReturnsTtl()
    {
        var result = AnthropicMessageConverter.BuildCacheControl(CacheRetention.Long, AnthropicBaseUrl);
        result.ShouldNotBeNull();
        result!["type"].ShouldBe("ephemeral");
        result["ttl"].ShouldBe("1h");
    }

    [Fact]
    public void BuildCacheControl_Long_NonAnthropicUrl_NoTtl()
    {
        var result = AnthropicMessageConverter.BuildCacheControl(CacheRetention.Long, "https://proxy.example.com");
        result.ShouldNotBeNull();
        result!["type"].ShouldBe("ephemeral");
        result.ContainsKey("ttl").ShouldBeFalse();
    }

    // -----------------------------------------------------------------------
    // ApplyMultiBreakpointCacheControl tests
    // -----------------------------------------------------------------------

    [Fact]
    public void ApplyMultiBreakpoint_None_PlacesNoBreakpoints()
    {
        var messages = MakeMessages("user");

        AnthropicMessageConverter.ApplyMultiBreakpointCacheControl(
            messages, CacheRetention.None, AnthropicBaseUrl);

        NoMessageShouldHaveCacheControl(messages);
    }

    [Fact]
    public void ApplyMultiBreakpoint_OneTurn_PlacesOneBreakpoint()
    {
        // 1 user message -- gets a breakpoint
        var messages = MakeMessages("user");

        AnthropicMessageConverter.ApplyMultiBreakpointCacheControl(
            messages, CacheRetention.Short, AnthropicBaseUrl);

        CountBreakpoints(messages).ShouldBe(1);
        LastMessageHasCacheControl(messages).ShouldBeTrue();
    }

    [Fact]
    public void ApplyMultiBreakpoint_ThreeTurns_PlacesThreeBreakpoints()
    {
        // user / assistant / user / assistant / user  (5 messages, last 3 are: assistant[3], assistant[4], user[5])
        // Wait -- "non-system" means anything with role=user or role=assistant
        // system_and_3 strategy: last 3 non-system messages
        var messages = MakeMessages("user", "assistant", "user", "assistant", "user");

        AnthropicMessageConverter.ApplyMultiBreakpointCacheControl(
            messages, CacheRetention.Short, AnthropicBaseUrl);

        // Last 3: messages[2] (user), messages[3] (assistant), messages[4] (user)
        CountBreakpoints(messages).ShouldBe(3);
    }

    [Fact]
    public void ApplyMultiBreakpoint_FiveTurns_PlacesMaxThreeBreakpoints()
    {
        // 7 messages -- only last 3 get breakpoints
        var messages = MakeMessages("user", "assistant", "user", "assistant", "user", "assistant", "user");

        AnthropicMessageConverter.ApplyMultiBreakpointCacheControl(
            messages, CacheRetention.Short, AnthropicBaseUrl);

        CountBreakpoints(messages).ShouldBe(3);
        // First 4 messages must have no breakpoints
        for (var i = 0; i < 4; i++)
            MessageHasCacheControl(messages[i]).ShouldBeFalse();
    }

    [Fact]
    public void ApplyMultiBreakpoint_StringContentUserMessage_ConvertedToBlock()
    {
        // When content is a plain string, the method must wrap it into a block list
        // so cache_control can be attached
        var messages = new List<Dictionary<string, object?>>
        {
            new() { ["role"] = "user", ["content"] = "plain text" }
        };

        AnthropicMessageConverter.ApplyMultiBreakpointCacheControl(
            messages, CacheRetention.Short, AnthropicBaseUrl);

        // Content must now be a List<object>
        var content = messages[0]["content"];
        content.ShouldBeOfType<List<object>>();
        var blocks = (List<object>)content;
        blocks.Count.ShouldBe(1);
        var block = (Dictionary<string, object?>)blocks[0];
        block["type"].ShouldBe("text");
        block["text"].ShouldBe("plain text");
        block.ContainsKey("cache_control").ShouldBeTrue();
    }

    [Fact]
    public void ApplyMultiBreakpoint_AssistantBlocksMessage_AttachesToLastBlock()
    {
        var assistantBlock = new Dictionary<string, object?>
        {
            ["type"] = "text",
            ["text"] = "assistant reply"
        };
        var messages = new List<Dictionary<string, object?>>
        {
            new() { ["role"] = "assistant", ["content"] = new List<object> { assistantBlock } }
        };

        AnthropicMessageConverter.ApplyMultiBreakpointCacheControl(
            messages, CacheRetention.Short, AnthropicBaseUrl);

        assistantBlock.ContainsKey("cache_control").ShouldBeTrue();
    }

    [Fact]
    public void ApplyMultiBreakpoint_TwoMessages_BothGetBreakpoints()
    {
        var messages = MakeMessages("user", "assistant");

        AnthropicMessageConverter.ApplyMultiBreakpointCacheControl(
            messages, CacheRetention.Short, AnthropicBaseUrl);

        CountBreakpoints(messages).ShouldBe(2);
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static List<Dictionary<string, object?>> MakeMessages(params string[] roles)
    {
        return roles.Select(role => new Dictionary<string, object?>
        {
            ["role"] = role,
            ["content"] = new List<object>
            {
                new Dictionary<string, object?>
                {
                    ["type"] = "text",
                    ["text"] = $"{role} message"
                }
            }
        }).ToList();
    }

    private static bool MessageHasCacheControl(Dictionary<string, object?> message)
    {
        if (message["content"] is List<object> blocks && blocks.Count > 0)
        {
            if (blocks[^1] is Dictionary<string, object?> lastBlock)
                return lastBlock.ContainsKey("cache_control");
        }
        return false;
    }

    private static bool LastMessageHasCacheControl(List<Dictionary<string, object?>> messages)
        => messages.Count > 0 && MessageHasCacheControl(messages[^1]);

    private static void NoMessageShouldHaveCacheControl(List<Dictionary<string, object?>> messages)
    {
        foreach (var msg in messages)
            MessageHasCacheControl(msg).ShouldBeFalse();
    }

    private static int CountBreakpoints(List<Dictionary<string, object?>> messages)
        => messages.Count(m => MessageHasCacheControl(m));
}
