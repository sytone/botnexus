using Bunit;
using Microsoft.Extensions.DependencyInjection;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

/// <summary>
/// Tests for <see cref="FeatureFlagsService"/> — localStorage-backed feature flags.
/// </summary>
public sealed class FeatureFlagsServiceTests : IDisposable
{
    private readonly BunitContext _ctx = new();

    public void Dispose() => _ctx.Dispose();

    private FeatureFlagsService CreateService()
    {
        _ctx.JSInterop.Mode = JSRuntimeMode.Strict;
        return new FeatureFlagsService(_ctx.JSInterop.JSRuntime);
    }

    [Fact]
    public async Task Flag_defaults_to_false_when_localStorage_returns_null()
    {
        var svc = CreateService();
        _ctx.JSInterop.Setup<string?>("localStorage.getItem", "bn:feature:conversationHistoryCache")
            .SetResult(null);

        await svc.InitializeAsync();

        svc.ConversationHistoryCache.ShouldBeFalse();
    }

    [Fact]
    public async Task Flag_returns_true_when_localStorage_returns_true()
    {
        var svc = CreateService();
        _ctx.JSInterop.Setup<string?>("localStorage.getItem", "bn:feature:conversationHistoryCache")
            .SetResult("true");

        await svc.InitializeAsync();

        svc.ConversationHistoryCache.ShouldBeTrue();
    }

    [Fact]
    public async Task Flag_returns_false_when_localStorage_returns_false()
    {
        var svc = CreateService();
        _ctx.JSInterop.Setup<string?>("localStorage.getItem", "bn:feature:conversationHistoryCache")
            .SetResult("false");

        await svc.InitializeAsync();

        svc.ConversationHistoryCache.ShouldBeFalse();
    }

    [Fact]
    public async Task Flag_returns_false_when_localStorage_returns_arbitrary_string()
    {
        var svc = CreateService();
        _ctx.JSInterop.Setup<string?>("localStorage.getItem", "bn:feature:conversationHistoryCache")
            .SetResult("yes");

        await svc.InitializeAsync();

        svc.ConversationHistoryCache.ShouldBeFalse();
    }
}
