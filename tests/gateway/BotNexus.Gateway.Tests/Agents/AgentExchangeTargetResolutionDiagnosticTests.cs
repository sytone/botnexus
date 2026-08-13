using BotNexus.Domain.AgentExchange;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Agents;
using BotNexus.Gateway.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace BotNexus.Gateway.Tests.Agents;

/// <summary>
/// Regression tests for #2877: an <c>agent_converse</c> target that fails to resolve must say so in
/// terms a caller can act on. The old message ("Target agent 'x' is not registered.") is thrown
/// BEFORE any policy evaluation, so it was indistinguishable from a policy denial - the caller's
/// rational next step was to stop trying rather than to correct the identifier.
/// </summary>
/// <remarks>
/// Scope is the DIAGNOSTIC only. Display-name addressing is deliberately still a failure here; the
/// actual resolution of a display name to an id is a separate enhancement. These tests pin the
/// message text so a future edit cannot silently drop the hint (AC5).
/// </remarks>
public sealed class AgentExchangeTargetResolutionDiagnosticTests
{
    private static AgentDescriptor Agent(string id, string displayName) => new()
    {
        AgentId = AgentId.From(id),
        DisplayName = displayName,
        ModelId = "gpt-4o",
        ApiProvider = "openai"
    };

    private static (AgentExchangeService Service, Mock<ISessionStore> Sessions, Mock<IConversationStore> Conversations, Mock<IAgentSupervisor> Supervisor) Build(
        params AgentDescriptor[] registered)
    {
        var sessionStore = new Mock<ISessionStore>(MockBehavior.Strict);
        var conversationStore = new Mock<IConversationStore>(MockBehavior.Strict);
        var supervisor = new Mock<IAgentSupervisor>(MockBehavior.Strict);

        var all = new List<AgentDescriptor>(registered) { Agent("nova", "Nova") };
        var registry = new Mock<IAgentRegistry>();
        registry.Setup(r => r.GetAll()).Returns(all);
        registry.Setup(r => r.Get(It.IsAny<AgentId>()))
            .Returns((AgentId id) => all.Find(d => d.AgentId == id));
        registry.Setup(r => r.Contains(It.IsAny<AgentId>()))
            .Returns((AgentId id) => all.Exists(d => d.AgentId == id));

        var service = new AgentExchangeService(
            registry.Object,
            supervisor.Object,
            sessionStore.Object,
            conversationStore.Object,
            Options.Create(new GatewayOptions()),
            NullLogger<AgentExchangeService>.Instance);

        return (service, sessionStore, conversationStore, supervisor);
    }

    private static AgentExchangeRequest RequestTo(string targetId) => new()
    {
        InitiatorId = AgentId.From("nova"),
        TargetId = AgentId.From(targetId),
        Message = "hello",
        MaxTurns = 1
    };

    /// <summary>AC1: exactly one display-name match names both the correct id and the display name.</summary>
    [Fact]
    public async Task ConverseAsync_UnknownIdMatchingOneDisplayName_NamesTheCorrectAgentId()
    {
        var (service, sessions, conversations, supervisor) = Build(
            Agent("ub-warning-cleanup", "Sentinel"),
            Agent("keel", "Keel"));

        var ex = await Assert.ThrowsAsync<KeyNotFoundException>(() => service.ConverseAsync(RequestTo("sentinel")));

        Assert.Contains("Target agent 'sentinel' is not registered.", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Did you mean 'ub-warning-cleanup' (display name: Sentinel)?", ex.Message, StringComparison.Ordinal);

        sessions.VerifyNoOtherCalls();
        conversations.VerifyNoOtherCalls();
        supervisor.VerifyNoOtherCalls();
    }

    /// <summary>
    /// AC2: no display-name match states plainly that no agent has that id, and must not read as a
    /// policy denial - so it may not contain the vocabulary of authorization.
    /// </summary>
    [Fact]
    public async Task ConverseAsync_UnknownIdWithNoDisplayNameMatch_StatesNoAgentHasThatId()
    {
        var (service, sessions, conversations, supervisor) = Build(
            Agent("ub-warning-cleanup", "Sentinel"));

        var ex = await Assert.ThrowsAsync<KeyNotFoundException>(() => service.ConverseAsync(RequestTo("does-not-exist")));

        Assert.Contains("Target agent 'does-not-exist' is not registered.", ex.Message, StringComparison.Ordinal);
        Assert.Contains("No registered agent has that id", ex.Message, StringComparison.Ordinal);
        Assert.Contains("not a policy denial", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("not allowed", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("denied", ex.Message, StringComparison.OrdinalIgnoreCase);

        sessions.VerifyNoOtherCalls();
        conversations.VerifyNoOtherCalls();
        supervisor.VerifyNoOtherCalls();
    }

    /// <summary>AC3: two agents sharing a display name is reported as ambiguous, listing every candidate id.</summary>
    [Fact]
    public async Task ConverseAsync_UnknownIdMatchingTwoDisplayNames_ReportsAmbiguityWithAllCandidateIds()
    {
        var (service, sessions, conversations, supervisor) = Build(
            Agent("ub-warning-cleanup", "Sentinel"),
            Agent("sentinel-two", "sentinel"));

        var ex = await Assert.ThrowsAsync<KeyNotFoundException>(() => service.ConverseAsync(RequestTo("Sentinel")));

        Assert.Contains("Target agent 'Sentinel' is not registered.", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Multiple registered agents have that display name", ex.Message, StringComparison.Ordinal);
        Assert.Contains("'ub-warning-cleanup'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("'sentinel-two'", ex.Message, StringComparison.Ordinal);
        // Ambiguity must not silently collapse into a single suggestion.
        Assert.DoesNotContain("Did you mean", ex.Message, StringComparison.Ordinal);

        sessions.VerifyNoOtherCalls();
        conversations.VerifyNoOtherCalls();
        supervisor.VerifyNoOtherCalls();
    }

    /// <summary>
    /// Happy path: a target id that IS registered resolves past the diagnostic throw site entirely.
    /// Proves the new lookup is only reached on failure and cannot reject a valid id.
    /// </summary>
    [Fact]
    public async Task ConverseAsync_RegisteredTargetId_DoesNotThrowResolutionDiagnostic()
    {
        var (service, _, _, _) = Build(Agent("ub-warning-cleanup", "Sentinel"));

        // The strict session/conversation mocks make the call fail LATER (at conversation creation),
        // which is precisely the evidence that resolution succeeded.
        var thrown = await Record.ExceptionAsync(() => service.ConverseAsync(RequestTo("ub-warning-cleanup")));

        Assert.NotNull(thrown);
        Assert.IsNotType<KeyNotFoundException>(thrown);
    }

    /// <summary>
    /// AC4: a well-formed cross-world reference still parses and is unaffected by the diagnostic -
    /// it must not be rejected with "is not registered" just because no local agent has that id.
    /// </summary>
    [Fact]
    public async Task ConverseAsync_CrossWorldReference_IsNotRejectedByTheDiagnostic()
    {
        var (service, _, _, _) = Build(Agent("ub-warning-cleanup", "Sentinel"));

        var thrown = await Record.ExceptionAsync(() => service.ConverseAsync(RequestTo("other-world:sentinel")));

        Assert.IsNotType<KeyNotFoundException>(thrown);
    }
}
