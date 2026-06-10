using System.Text.Json;
using System.Text.Json.Nodes;
using BotNexus.Agent.Providers.Core;
using BotNexus.Agent.Providers.Core.Models;

namespace BotNexus.Agent.Providers.Anthropic.Tests;

/// <summary>
/// Tests for BOTNEXUS_CACHE_BOUNDARY system prompt splitting (Issue #806).
/// When the marker is present, the system prompt is split into two blocks:
/// - Stable prefix: gets cache_control breakpoint
/// - Dynamic tail: does NOT get cache_control (prevents cache invalidation)
/// </summary>
public class CacheBoundarySplitTests
{
    private const string Marker = "<!-- BOTNEXUS_CACHE_BOUNDARY -->";

    [Fact]
    public void SystemPrompt_WithBoundary_SplitsIntoTwoBlocks()
    {
        var stable = "You are helpful.\nFollow instructions.";
        var dynamic = "## Memory\nToday is Monday.";
        var systemPrompt = $"{stable}\n{Marker}\n{dynamic}";

        var model = TestHelpers.MakeModel();
        var context = new Context(SystemPrompt: systemPrompt, Messages: [MakeUserMessage()]);
        var options = new StreamOptions { ApiKey = "sk-ant-test", CacheRetention = CacheRetention.Short };

        var body = AnthropicRequestBuilder.BuildRequestBody(
            model, context, options, null, isOAuthToken: false, _ => false);

        var system = body["system"]!.AsArray();
        system.Count.ShouldBe(2);

        // First block = stable prefix WITH cache_control
        var stableBlock = system[0]!.AsObject();
        stableBlock["type"]!.GetValue<string>().ShouldBe("text");
        stableBlock["text"]!.GetValue<string>().ShouldBe(stable);
        stableBlock.ContainsKey("cache_control").ShouldBeTrue();

        // Second block = dynamic tail WITHOUT cache_control
        var dynamicBlock = system[1]!.AsObject();
        dynamicBlock["type"]!.GetValue<string>().ShouldBe("text");
        dynamicBlock["text"]!.GetValue<string>().ShouldBe(dynamic);
        dynamicBlock.ContainsKey("cache_control").ShouldBeFalse();
    }

    [Fact]
    public void SystemPrompt_WithoutBoundary_SingleBlockWithCacheControl()
    {
        var systemPrompt = "You are a helpful assistant.";

        var model = TestHelpers.MakeModel();
        var context = new Context(SystemPrompt: systemPrompt, Messages: [MakeUserMessage()]);
        var options = new StreamOptions { ApiKey = "sk-ant-test", CacheRetention = CacheRetention.Short };

        var body = AnthropicRequestBuilder.BuildRequestBody(
            model, context, options, null, isOAuthToken: false, _ => false);

        var system = body["system"]!.AsArray();
        system.Count.ShouldBe(1);

        var block = system[0]!.AsObject();
        block["type"]!.GetValue<string>().ShouldBe("text");
        block["text"]!.GetValue<string>().ShouldBe(systemPrompt);
        block.ContainsKey("cache_control").ShouldBeTrue();
    }

    [Fact]
    public void SystemPrompt_WithBoundary_CacheRetentionNone_NoBlocks()
    {
        var systemPrompt = $"Stable\n{Marker}\nDynamic";

        var model = TestHelpers.MakeModel();
        var context = new Context(SystemPrompt: systemPrompt, Messages: [MakeUserMessage()]);
        var options = new StreamOptions { ApiKey = "sk-ant-test", CacheRetention = CacheRetention.None };

        var body = AnthropicRequestBuilder.BuildRequestBody(
            model, context, options, null, isOAuthToken: false, _ => false);

        // With CacheRetention.None, system still appears but no cache_control on either block
        var system = body["system"]!.AsArray();
        system.Count.ShouldBe(2);

        var stableBlock = system[0]!.AsObject();
        stableBlock.ContainsKey("cache_control").ShouldBeFalse();

        var dynamicBlock = system[1]!.AsObject();
        dynamicBlock.ContainsKey("cache_control").ShouldBeFalse();
    }

    [Fact]
    public void SystemPrompt_WithBoundary_LongRetention_HasTtl()
    {
        var stable = "Stable prefix";
        var dynamic = "Dynamic tail";
        var systemPrompt = $"{stable}\n{Marker}\n{dynamic}";

        var model = TestHelpers.MakeModel(); // Uses api.anthropic.com base URL
        var context = new Context(SystemPrompt: systemPrompt, Messages: [MakeUserMessage()]);
        var options = new StreamOptions { ApiKey = "sk-ant-test", CacheRetention = CacheRetention.Long };

        var body = AnthropicRequestBuilder.BuildRequestBody(
            model, context, options, null, isOAuthToken: false, _ => false);

        var system = body["system"]!.AsArray();
        system.Count.ShouldBe(2);

        var stableBlock = system[0]!.AsObject();
        stableBlock.ContainsKey("cache_control").ShouldBeTrue();
        var cc = stableBlock["cache_control"]!.AsObject();
        cc["ttl"]!.GetValue<string>().ShouldBe("1h");

        var dynamicBlock = system[1]!.AsObject();
        dynamicBlock.ContainsKey("cache_control").ShouldBeFalse();
    }

    [Fact]
    public void SystemPrompt_WithBoundary_OAuth_SplitsIntoThreeBlocks()
    {
        // OAuth adds a Claude Code system block first, then splits user system prompt
        var stable = "Stable prefix";
        var dynamic = "Dynamic tail";
        var systemPrompt = $"{stable}\n{Marker}\n{dynamic}";

        var model = TestHelpers.MakeModel();
        var context = new Context(SystemPrompt: systemPrompt, Messages: [MakeUserMessage()]);
        var options = new StreamOptions { ApiKey = "sk-ant-oat01-test", CacheRetention = CacheRetention.Short };

        var body = AnthropicRequestBuilder.BuildRequestBody(
            model, context, options, null, isOAuthToken: true, _ => false);

        var system = body["system"]!.AsArray();
        // OAuth: Claude Code block + stable prefix (cached) + dynamic tail (not cached)
        system.Count.ShouldBe(3);

        // First block = Claude Code system (always has cache_control)
        var ccBlock = system[0]!.AsObject();
        ccBlock["text"]!.GetValue<string>().ShouldContain("Claude Code");
        ccBlock.ContainsKey("cache_control").ShouldBeTrue();

        // Second block = stable prefix WITH cache_control
        var stableBlock = system[1]!.AsObject();
        stableBlock["text"]!.GetValue<string>().ShouldBe(stable);
        stableBlock.ContainsKey("cache_control").ShouldBeTrue();

        // Third block = dynamic tail WITHOUT cache_control
        var dynamicBlock = system[2]!.AsObject();
        dynamicBlock["text"]!.GetValue<string>().ShouldBe(dynamic);
        dynamicBlock.ContainsKey("cache_control").ShouldBeFalse();
    }

    [Fact]
    public void SystemPrompt_WithBoundary_EmptyDynamic_SingleBlock()
    {
        // If boundary is at the end with no dynamic content, treat as single block
        var stable = "Stable prefix";
        var systemPrompt = $"{stable}\n{Marker}\n";

        var model = TestHelpers.MakeModel();
        var context = new Context(SystemPrompt: systemPrompt, Messages: [MakeUserMessage()]);
        var options = new StreamOptions { ApiKey = "sk-ant-test", CacheRetention = CacheRetention.Short };

        var body = AnthropicRequestBuilder.BuildRequestBody(
            model, context, options, null, isOAuthToken: false, _ => false);

        var system = body["system"]!.AsArray();
        // Empty dynamic tail means we only emit the stable block
        system.Count.ShouldBe(1);
        var block = system[0]!.AsObject();
        block["text"]!.GetValue<string>().ShouldBe(stable);
        block.ContainsKey("cache_control").ShouldBeTrue();
    }

    [Fact]
    public void SystemPrompt_WithBoundary_EmptyStable_SingleDynamicBlock()
    {
        // If boundary is at the start with no stable prefix, emit dynamic only without cache
        var dynamic = "Dynamic content only";
        var systemPrompt = $"\n{Marker}\n{dynamic}";

        var model = TestHelpers.MakeModel();
        var context = new Context(SystemPrompt: systemPrompt, Messages: [MakeUserMessage()]);
        var options = new StreamOptions { ApiKey = "sk-ant-test", CacheRetention = CacheRetention.Short };

        var body = AnthropicRequestBuilder.BuildRequestBody(
            model, context, options, null, isOAuthToken: false, _ => false);

        var system = body["system"]!.AsArray();
        // Empty stable prefix means we only emit the dynamic block (no cache since it's dynamic)
        system.Count.ShouldBe(1);
        var block = system[0]!.AsObject();
        block["text"]!.GetValue<string>().ShouldBe(dynamic);
        block.ContainsKey("cache_control").ShouldBeFalse();
    }

    private static UserMessage MakeUserMessage() =>
        new(new UserMessageContent("hello"), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
}
