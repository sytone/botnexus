using Bunit;
using Microsoft.JSInterop;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

/// <summary>
/// Unit tests for <see cref="ConversationHistoryCache"/> — localStorage-backed
/// conversation history caching with a 5-minute TTL.
/// </summary>
public sealed class ConversationHistoryCacheTests : IDisposable
{
    private readonly BunitContext _ctx = new();

    public void Dispose() => _ctx.Dispose();

    private ConversationHistoryCache CreateCache()
    {
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        return new ConversationHistoryCache(_ctx.JSInterop.JSRuntime);
    }

    // ── GetAsync ──────────────────────────────────────────────────────────

    [Fact]
    public async Task GetAsync_WhenEmpty_ReturnsNull()
    {
        _ctx.JSInterop.Mode = JSRuntimeMode.Strict;
        _ctx.JSInterop.Setup<string?>("localStorage.getItem", "bn:conv-history:conv-1")
            .SetResult(null);

        var cache = new ConversationHistoryCache(_ctx.JSInterop.JSRuntime);
        var result = await cache.GetAsync("conv-1");

        result.ShouldBeNull();
    }

    [Fact]
    public async Task GetAsync_WhenEntryIsFresh_ReturnsCachedMessages()
    {
        var messages = new List<ChatMessage>
        {
            new("User", "Hello", DateTimeOffset.UtcNow.AddMinutes(-2))
        };
        var cached = new CachedHistory("conv-1", DateTimeOffset.UtcNow.AddMinutes(-1), messages);
        var json = System.Text.Json.JsonSerializer.Serialize(cached);

        _ctx.JSInterop.Mode = JSRuntimeMode.Strict;
        _ctx.JSInterop.Setup<string?>("localStorage.getItem", "bn:conv-history:conv-1")
            .SetResult(json);

        var cache = new ConversationHistoryCache(_ctx.JSInterop.JSRuntime);
        var result = await cache.GetAsync("conv-1");

        result.ShouldNotBeNull();
        result!.Messages.Count.ShouldBe(1);
        result.Messages[0].Content.ShouldBe("Hello");
    }

    [Fact]
    public async Task GetAsync_WhenEntryIsStale_5MinutesOld_ReturnsNull()
    {
        var messages = new List<ChatMessage>
        {
            new("User", "Old message", DateTimeOffset.UtcNow.AddMinutes(-10))
        };
        var cached = new CachedHistory("conv-1", DateTimeOffset.UtcNow.AddMinutes(-6), messages);
        var json = System.Text.Json.JsonSerializer.Serialize(cached);

        _ctx.JSInterop.Mode = JSRuntimeMode.Strict;
        _ctx.JSInterop.Setup<string?>("localStorage.getItem", "bn:conv-history:conv-1")
            .SetResult(json);

        var cache = new ConversationHistoryCache(_ctx.JSInterop.JSRuntime);
        var result = await cache.GetAsync("conv-1");

        result.ShouldBeNull();
    }

    [Fact]
    public async Task GetAsync_WhenJsInteropThrows_ReturnsNullGracefully()
    {
        _ctx.JSInterop.Mode = JSRuntimeMode.Strict;
        _ctx.JSInterop.Setup<string?>("localStorage.getItem", "bn:conv-history:conv-1")
            .SetException(new JSException("storage unavailable"));

        var cache = new ConversationHistoryCache(_ctx.JSInterop.JSRuntime);
        var result = await cache.GetAsync("conv-1");

        result.ShouldBeNull();
    }

    // ── SetAsync ──────────────────────────────────────────────────────────

    [Fact]
    public async Task SetAsync_WritesJsonToLocalStorage()
    {
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        var cache = new ConversationHistoryCache(_ctx.JSInterop.JSRuntime);

        var messages = new List<ChatMessage>
        {
            new("Assistant", "Hi there", DateTimeOffset.UtcNow)
        };

        await cache.SetAsync("conv-2", messages);

        // Verify localStorage.setItem was called with our cache key
        var setItemInvocations = _ctx.JSInterop.Invocations
            .Where(i => i.Identifier == "localStorage.setItem" &&
                        i.Arguments.Count > 0 &&
                        i.Arguments[0]?.ToString() == "bn:conv-history:conv-2")
            .ToList();

        setItemInvocations.ShouldNotBeEmpty();
        // The second argument should contain the serialised JSON
        var jsonArg = setItemInvocations[0].Arguments[1]?.ToString();
        jsonArg.ShouldNotBeNullOrEmpty();
        jsonArg!.ShouldContain("Hi there");
    }

    [Fact]
    public async Task SetAsync_WhenJsInteropThrows_DoesNotCrash()
    {
        _ctx.JSInterop.Mode = JSRuntimeMode.Strict;
        _ctx.JSInterop
            .Setup<object>("localStorage.setItem", _ => true)
            .SetException(new JSException("storage full"));

        var cache = new ConversationHistoryCache(_ctx.JSInterop.JSRuntime);

        var messages = new List<ChatMessage> { new("User", "msg", DateTimeOffset.UtcNow) };

        // Must not throw
        await Should.NotThrowAsync(() => cache.SetAsync("conv-3", messages));
    }

    // ── InvalidateAsync ───────────────────────────────────────────────────

    [Fact]
    public async Task InvalidateAsync_RemovesKeyFromLocalStorage()
    {
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        var cache = new ConversationHistoryCache(_ctx.JSInterop.JSRuntime);

        await cache.InvalidateAsync("conv-4");

        var removeInvocations = _ctx.JSInterop.Invocations
            .Where(i => i.Identifier == "localStorage.removeItem" &&
                        i.Arguments.Count > 0 &&
                        i.Arguments[0]?.ToString() == "bn:conv-history:conv-4")
            .ToList();

        removeInvocations.ShouldNotBeEmpty();
    }
}
