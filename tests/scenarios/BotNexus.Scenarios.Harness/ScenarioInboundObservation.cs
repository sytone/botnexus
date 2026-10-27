using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Channels;
using BotNexus.Gateway.Dispatching;

namespace BotNexus.Scenarios.Harness;

/// <summary>An adapter completion tied to the exact attempted inbound, or its propagated exception.</summary>
public sealed record ScenarioInboundOutcome(InboundMessage Message, ChannelDispatchOutcome? Outcome, Exception? Error);

/// <summary>A typed orchestrator result, retained before the legacy Task-only boundary erases it.</summary>
public sealed record ScenarioInboundAcceptance(InboundMessage Message, InboundDispatchResult Result);

/// <summary>Provider entry with the latest user text; this is context identity, not a transport message id.</summary>
public sealed record ScenarioProviderInvocation(int TurnIndex, string? UserText);
