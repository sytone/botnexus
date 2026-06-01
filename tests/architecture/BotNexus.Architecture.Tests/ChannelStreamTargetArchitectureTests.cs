using System.Linq;
using System.Reflection;
using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Architecture.Tests;

/// <summary>
/// Architecture fence pinning the typed <see cref="ChannelStreamTarget"/> contract on the
/// streaming surface of <see cref="IChannelAdapter"/> and <see cref="IStreamEventChannelAdapter"/>
/// (PR #677). Previously these methods accepted a loosely-typed <c>string conversationId</c>
/// that meant different things per adapter (SessionId for SignalR, ChannelAddress for Telegram,
/// ignored for TUI). The typed record removes the ambiguity. This fence prevents a future
/// refactor from silently reintroducing a string-based overload alongside the typed one.
/// </summary>
public sealed class ChannelStreamTargetArchitectureTests
{
    [Fact]
    public void IChannelAdapter_SendStreamDeltaAsync_FirstParameter_IsChannelStreamTarget()
    {
        var method = typeof(IChannelAdapter).GetMethod(nameof(IChannelAdapter.SendStreamDeltaAsync))
            ?? throw new InvalidOperationException("IChannelAdapter.SendStreamDeltaAsync not found");

        var firstParam = method.GetParameters().FirstOrDefault()
            ?? throw new InvalidOperationException("SendStreamDeltaAsync has no parameters");

        firstParam.ParameterType.ShouldBe(typeof(ChannelStreamTarget),
            "SendStreamDeltaAsync must take the typed ChannelStreamTarget as its first parameter — never a raw string. " +
            "Mixing string and typed overloads brings back the per-adapter routing ambiguity PR #677 removed.");
    }

    [Fact]
    public void IStreamEventChannelAdapter_SendStreamEventAsync_FirstParameter_IsChannelStreamTarget()
    {
        var method = typeof(IStreamEventChannelAdapter).GetMethod(nameof(IStreamEventChannelAdapter.SendStreamEventAsync))
            ?? throw new InvalidOperationException("IStreamEventChannelAdapter.SendStreamEventAsync not found");

        var firstParam = method.GetParameters().FirstOrDefault()
            ?? throw new InvalidOperationException("SendStreamEventAsync has no parameters");

        firstParam.ParameterType.ShouldBe(typeof(ChannelStreamTarget),
            "SendStreamEventAsync must take the typed ChannelStreamTarget as its first parameter — never a raw string. " +
            "Mixing string and typed overloads brings back the per-adapter routing ambiguity PR #677 removed.");
    }

    [Fact]
    public void IChannelAdapter_DoesNotExposeStringConversationIdOverloads_OnStreamingMethods()
    {
        var streamMethods = typeof(IChannelAdapter).GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.Name == nameof(IChannelAdapter.SendStreamDeltaAsync))
            .ToList();

        streamMethods.Count.ShouldBe(1,
            "IChannelAdapter must expose exactly one SendStreamDeltaAsync method — adding a string-based overload would " +
            "reintroduce the routing-ambiguity bug PR #677 fixed.");

        var firstParamType = streamMethods[0].GetParameters()[0].ParameterType;
        firstParamType.ShouldNotBe(typeof(string),
            "SendStreamDeltaAsync must not take a raw string as its first parameter.");
    }

    [Fact]
    public void IStreamEventChannelAdapter_DoesNotExposeStringConversationIdOverloads_OnStreamingMethods()
    {
        var streamMethods = typeof(IStreamEventChannelAdapter).GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.Name == nameof(IStreamEventChannelAdapter.SendStreamEventAsync))
            .ToList();

        streamMethods.Count.ShouldBe(1,
            "IStreamEventChannelAdapter must expose exactly one SendStreamEventAsync method — adding a string-based overload would " +
            "reintroduce the routing-ambiguity bug PR #677 fixed.");

        var firstParamType = streamMethods[0].GetParameters()[0].ParameterType;
        firstParamType.ShouldNotBe(typeof(string),
            "SendStreamEventAsync must not take a raw string as its first parameter.");
    }
}
