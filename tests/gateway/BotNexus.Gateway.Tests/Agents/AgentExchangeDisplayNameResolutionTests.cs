using BotNexus.Domain.AgentExchange;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Agents;
using BotNexus.Gateway.Configuration;
using BotNexus.Gateway.Conversations;
using BotNexus.Gateway.Sessions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace BotNexus.Gateway.Tests.Agents;

/// <summary>
/// Tests for #2878: <c>agent_converse</c> resolves a target by unambiguous case-insensitive
/// <c>DisplayName</c> when the supplied target matches no agent id and is not a parseable
/// cross-world reference.
/// </summary>
/// <remarks>
/// The precedence is the contract, not the lookup: an exact id ALWAYS wins (so no existing
/// id-addressed call can change meaning), cross-world parsing is attempted BEFORE the display-name
/// fallback, and resolution happens before the access-policy check so display-name addressing can
/// never be a whitelist bypass. Ambiguity is an error naming every candidate - never a guess.
/// </remarks>
public sealed class AgentExchangeDisplayNameResolutionTests
{
    private static AgentDescriptor Agent(
        string id,
        string displayName,
        IReadOnlyList<string>? subAgentIds = null) => new()
        {
            AgentId = AgentId.From(id),
            DisplayName = displayName,
            ModelId = "gpt-5-mini",
            ApiProvider = "copilot",
            SubAgentIds = subAgentIds ?? []
        };

    private sealed record Harness(
        AgentExchangeService Service,
        Mock<IAgentSupervisor> Supervisor,
        List<AgentId> PromptedTargets);

    private static Harness Build(string accessPolicy, params AgentDescriptor[] registered)
    {
        var all = new List<AgentDescriptor>(registered);
        var registry = new Mock<IAgentRegistry>();
        registry.Setup(r => r.GetAll()).Returns(all);
        registry.Setup(r => r.Get(It.IsAny<AgentId>()))
            .Returns((AgentId id) => all.Find(d => d.AgentId == id));
        registry.Setup(r => r.Contains(It.IsAny<AgentId>()))
            .Returns((AgentId id) => all.Exists(d => d.AgentId == id));

        var prompted = new List<AgentId>();
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor
            .Setup(s => s.GetOrCreateAsync(It.IsAny<AgentId>(), It.IsAny<SessionId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((AgentId id, SessionId _, CancellationToken _) =>
            {
                prompted.Add(id);
                var handle = new Mock<IAgentHandle>();
                handle.Setup(h => h.PromptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync(new AgentResponse { Content = $"reply from {id.Value}" });
                return handle.Object;
            });

        var conversationStore = new InMemoryConversationStore();
        var sessionStore = new InMemorySessionStore(redactor: null, conversationStore: conversationStore);

        var service = new AgentExchangeService(
            registry.Object,
            supervisor.Object,
            sessionStore,
            conversationStore,
            Options.Create(new GatewayOptions()),
            NullLogger<AgentExchangeService>.Instance,
            exchangeOptions: Options.Create(new AgentExchangeOptions { AccessPolicy = accessPolicy }));

        return new Harness(service, supervisor, prompted);
    }

    private static AgentExchangeRequest RequestTo(string targetId, string initiator = "nova") => new()
    {
        InitiatorId = AgentId.From(initiator),
        TargetId = AgentId.From(targetId),
        Message = "hello",
        MaxTurns = 1
    };

    /// <summary>
    /// AC1: a unique case-insensitive display name reaches the target agent and completes the
    /// exchange. "sentinel" must dispatch to the agent whose id is <c>ub-warning-cleanup</c>.
    /// </summary>
    [Fact]
    public async Task ConverseAsync_UniqueDisplayName_ReachesTargetAndCompletes()
    {
        var harness = Build("open",
            Agent("nova", "Nova"),
            Agent("ub-warning-cleanup", "Sentinel"),
            Agent("keel", "Keel"));

        var result = await harness.Service.ConverseAsync(RequestTo("sentinel"));

        Assert.Equal("sealed", result.Status);
        Assert.Equal("reply from ub-warning-cleanup", result.FinalResponse);
        Assert.Equal([AgentId.From("ub-warning-cleanup")], harness.PromptedTargets);
    }

    /// <summary>
    /// AC2: an exact agent id beats any display-name match. Here agent <c>keel</c> has display name
    /// "sentinel" while a DIFFERENT agent has the id <c>sentinel</c>; the id must win.
    /// </summary>
    [Fact]
    public async Task ConverseAsync_IdMatchBeatsDisplayNameMatch()
    {
        var harness = Build("open",
            Agent("nova", "Nova"),
            Agent("sentinel", "Watchtower"),
            Agent("keel", "sentinel"));

        var result = await harness.Service.ConverseAsync(RequestTo("sentinel"));

        Assert.Equal("reply from sentinel", result.FinalResponse);
        Assert.Equal([AgentId.From("sentinel")], harness.PromptedTargets);
        Assert.DoesNotContain(AgentId.From("keel"), harness.PromptedTargets);
    }

    /// <summary>
    /// AC3: two agents sharing a display name is an ambiguity error listing every candidate id, and
    /// dispatches to neither.
    /// </summary>
    [Fact]
    public async Task ConverseAsync_AmbiguousDisplayName_ThrowsListingCandidatesAndDispatchesToNeither()
    {
        var harness = Build("open",
            Agent("nova", "Nova"),
            Agent("ub-warning-cleanup", "Sentinel"),
            Agent("sentinel-two", "sentinel"));

        var ex = await Assert.ThrowsAsync<KeyNotFoundException>(
            () => harness.Service.ConverseAsync(RequestTo("SENTINEL")));

        Assert.Contains("Multiple registered agents have that display name", ex.Message, StringComparison.Ordinal);
        Assert.Contains("'ub-warning-cleanup'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("'sentinel-two'", ex.Message, StringComparison.Ordinal);
        Assert.Empty(harness.PromptedTargets);
        harness.Supervisor.Verify(
            s => s.GetOrCreateAsync(It.IsAny<AgentId>(), It.IsAny<SessionId>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>
    /// AC4 (sad path): the access-policy check runs against the RESOLVED agent, so addressing a
    /// non-whitelisted agent by display name is denied exactly as addressing it by id would be.
    /// Without this the resolution would be a whitelist bypass.
    /// </summary>
    [Fact]
    public async Task ConverseAsync_DisplayNameUnderWhitelist_DeniedWhenResolvedTargetNotGranted()
    {
        var harness = Build("whitelist",
            Agent("nova", "Nova", subAgentIds: ["keel"]),
            Agent("ub-warning-cleanup", "Sentinel"),
            Agent("keel", "Keel"));

        var ex = await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => harness.Service.ConverseAsync(RequestTo("Sentinel")));

        // The denial names the RESOLVED id, proving the policy saw the resolved agent.
        Assert.Contains("ub-warning-cleanup", ex.Message, StringComparison.Ordinal);
        Assert.Empty(harness.PromptedTargets);
    }

    /// <summary>
    /// AC4 (happy path): a display name whose RESOLVED id IS whitelisted is allowed, so the policy
    /// check is genuinely evaluated against the resolved agent rather than merely always failing.
    /// </summary>
    [Fact]
    public async Task ConverseAsync_DisplayNameUnderWhitelist_AllowedWhenResolvedTargetGranted()
    {
        var harness = Build("whitelist",
            Agent("nova", "Nova", subAgentIds: ["ub-warning-cleanup"]),
            Agent("ub-warning-cleanup", "Sentinel"));

        var result = await harness.Service.ConverseAsync(RequestTo("Sentinel"));

        Assert.Equal("sealed", result.Status);
        Assert.Equal([AgentId.From("ub-warning-cleanup")], harness.PromptedTargets);
    }

    /// <summary>
    /// AC5: cross-world parsing is attempted BEFORE the display-name fallback. An agent whose
    /// display name is literally "other-world:sentinel" must NOT capture a cross-world reference -
    /// the target still routes as federation, so the local agent is never prompted.
    /// </summary>
    [Fact]
    public async Task ConverseAsync_CrossWorldReference_IsParsedBeforeDisplayNameFallback()
    {
        var harness = Build("open",
            Agent("nova", "Nova"),
            Agent("decoy", "other-world:sentinel"));

        // Cross-world routing has no configured peer world here, so it fails - but it must fail as
        // ROUTING, never by resolving to the local decoy agent.
        var thrown = await Record.ExceptionAsync(() => harness.Service.ConverseAsync(RequestTo("other-world:sentinel")));

        Assert.NotNull(thrown);
        Assert.Empty(harness.PromptedTargets);
    }

    /// <summary>
    /// Sad path: a target matching neither an id nor any display name still fails to resolve, and
    /// says so as a resolution failure rather than a policy denial.
    /// </summary>
    [Fact]
    public async Task ConverseAsync_NoIdAndNoDisplayNameMatch_StillThrowsResolutionFailure()
    {
        var harness = Build("open",
            Agent("nova", "Nova"),
            Agent("ub-warning-cleanup", "Sentinel"));

        var ex = await Assert.ThrowsAsync<KeyNotFoundException>(
            () => harness.Service.ConverseAsync(RequestTo("does-not-exist")));

        Assert.Contains("No registered agent has that id", ex.Message, StringComparison.Ordinal);
        Assert.Empty(harness.PromptedTargets);
    }
}
