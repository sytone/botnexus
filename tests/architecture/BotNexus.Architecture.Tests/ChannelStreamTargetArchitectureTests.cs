using System.Linq;
using System.Reflection;
using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Abstractions.Models;
using Shouldly;

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

    // ── PR1.5 (#682) — ConversationId on ChannelStreamTarget + wire payloads ─────────

    [Fact]
    public void ChannelStreamTarget_HasRequiredConversationIdAsFirstPositionalParameter()
    {
        // The primary constructor's parameters drive both deconstruction order and the
        // public init properties. ConversationId must be the first positional parameter
        // because it is the routing key SignalR uses to keep streams alive across
        // compaction (#682). If a refactor reorders this, downstream code that uses
        // positional construction (helpers, deconstructions) silently swaps fields.
        var ctor = typeof(ChannelStreamTarget).GetConstructors()
            .FirstOrDefault(c => c.GetParameters().Length >= 3)
            ?? throw new InvalidOperationException("ChannelStreamTarget primary constructor not found.");

        var first = ctor.GetParameters()[0];
        first.Name.ShouldBe("ConversationId",
            "ChannelStreamTarget's first positional parameter must be ConversationId — it is the " +
            "primary delivery key for SignalR and the one that survives compaction (#682).");
        first.ParameterType.Name.ShouldBe("ConversationId",
            "ConversationId parameter must be the typed BotNexus.Domain.Primitives.ConversationId value object.");
    }

    [Fact]
    public void ChannelStreamTarget_ExposesConversationIdProperty()
    {
        var prop = typeof(ChannelStreamTarget).GetProperty("ConversationId")
            ?? throw new InvalidOperationException("ChannelStreamTarget.ConversationId property not found.");

        prop.PropertyType.Name.ShouldBe("ConversationId",
            "ChannelStreamTarget.ConversationId must be the typed value object — never raw string.");
    }

    [Fact]
    public void AgentStreamEvent_CarriesNullableConversationId_ForClientRouting()
    {
        // Wire payload must carry ConversationId so the Blazor client can route a
        // post-compaction stream delta to the right ConversationState even when the
        // new sessionId is not yet registered locally (#682).
        var prop = typeof(AgentStreamEvent).GetProperty("ConversationId")
            ?? throw new InvalidOperationException("AgentStreamEvent.ConversationId property not found.");

        prop.PropertyType.Name.ShouldStartWith("Nullable", Case.Sensitive,
            "AgentStreamEvent.ConversationId must be a nullable ConversationId — server-only events " +
            "may legitimately omit it during the transition window, but the property must exist so " +
            "the client routing layer can prefer it over session→conversation lookup.");
    }
}
