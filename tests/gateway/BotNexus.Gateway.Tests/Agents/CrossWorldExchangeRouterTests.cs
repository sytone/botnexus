using System.Net;
using System.Net.Http.Json;
using BotNexus.Domain.AgentExchange;
using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Agents;
using BotNexus.Gateway.Channels;
using BotNexus.Gateway.Configuration;
using BotNexus.Gateway.Conversations;
using BotNexus.Gateway.Sessions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using GatewaySessionStatus = BotNexus.Gateway.Abstractions.Models.SessionStatus;

namespace BotNexus.Gateway.Tests.Agents;

/// <summary>
/// Isolated tests for <see cref="CrossWorldExchangeRouter"/> (#1542). These exercise the
/// cross-world permission / peer / target resolution and the per-turn relay against just the
/// federation config + a fake relay — the whole point of splitting federation routing out of
/// <see cref="AgentExchangeService"/> is that these paths no longer require the full local-exchange
/// machinery (registry, supervisor, both stores, budget tracker) to reach.
/// </summary>
public sealed class CrossWorldExchangeRouterTests
{
    private static readonly AgentId Initiator = AgentId.From("test-agent");
    private static readonly AgentId Target = AgentId.From("world-b:agent-c");
    private static readonly AgentId ResolvedTargetAgent = AgentId.From("agent-c");

    private static CrossWorldExchangeRouter CreateRouter(
        PlatformConfig platformConfig,
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder,
        out InMemorySessionStore sessionStore,
        out InMemoryConversationStore conversationStore)
    {
        conversationStore = new InMemoryConversationStore();
        sessionStore = new InMemorySessionStore(redactor: null, conversationStore: conversationStore);

        var engine = new AgentExchangeTurnEngine(
            sessionStore,
            conversationStore,
            NullLogger.Instance,
            budgetTracker: null);

        var adapter = new CrossWorldChannelAdapter(
            NullLogger<CrossWorldChannelAdapter>.Instance,
            new HttpClient(new StubHttpMessageHandler(responder)));

        return new CrossWorldExchangeRouter(
            engine,
            sessionStore,
            conversationStore,
            Options.Create(platformConfig),
            adapter);
    }

    private static PlatformConfig ConfigWith(bool allowOutbound, bool includePeer)
    {
        var peers = includePeer
            ? new Dictionary<string, CrossWorldPeerConfig>
            {
                ["world-b"] = new()
                {
                    Endpoint = "https://gateway-b.internal",
                    ApiKey = "peer-key",
                    Enabled = true
                }
            }
            : new Dictionary<string, CrossWorldPeerConfig>();

        return new PlatformConfig
        {
            Gateway = new GatewaySettingsConfig
            {
                World = new BotNexus.Domain.WorldIdentity { Id = "world-a", Name = "World A" },
                CrossWorldPermissions =
                [
                    new CrossWorldPermissionConfig
                    {
                        TargetWorldId = "world-b",
                        AllowOutbound = allowOutbound,
                        AllowedAgents = ["test-agent"]
                    }
                ],
                CrossWorld = new CrossWorldFederationConfig
                {
                    Peers = peers
                }
            }
        };
    }

    private static AgentExchangeRequest RequestForTarget(int maxTurns = 1)
        => new()
        {
            InitiatorId = Initiator,
            TargetId = Target,
            Message = "Hello remote world",
            MaxTurns = maxTurns
        };

    [Fact]
    public async Task ConverseCrossWorldAsync_HappyPath_RelaysAndSealsCrossWorldSession()
    {
        HttpRequestMessage? capturedRequest = null;
        var router = CreateRouter(
            ConfigWith(allowOutbound: true, includePeer: true),
            (request, _) =>
            {
                capturedRequest = request;
                var response = new CrossWorldRelayResponse
                {
                    Response = "Remote response",
                    Status = "active",
                    SessionId = "remote-session-1"
                };
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(response)
                });
            },
            out var sessionStore,
            out var conversationStore);

        CrossWorldAgentReference.TryParse(Target, out var parsed).ShouldBeTrue();

        var result = await router.ConverseCrossWorldAsync(
            RequestForTarget(),
            parsed!,
            normalizedChain: [Initiator]);

        result.Status.ShouldBe("sealed");
        result.FinalResponse.ShouldBe("Remote response");

        var session = await sessionStore.GetAsync(result.SessionId);
        session.ShouldNotBeNull();
        session!.ChannelType.ShouldBe(ChannelKey.From("cross-world"));
        session.Status.ShouldBe(GatewaySessionStatus.Sealed);
        session.Metadata["sourceWorldId"].ShouldBe("world-a");
        session.Metadata["targetWorldId"].ShouldBe("world-b");
        session.Metadata["remoteSessionId"].ShouldBe("remote-session-1");

        // The normalized chain is threaded into the session metadata (callChain bookkeeping).
        session.Metadata["callChain"].ShouldBeAssignableTo<string[]>();
        var callChain = (string[])session.Metadata["callChain"]!;
        callChain.ShouldContain("test-agent");
        callChain.ShouldContain("world-b:agent-c");

        // Participants live on the conversation (cross-world variant): initiator + resolved target.
        var conversation = await conversationStore.GetAsync(session.ConversationId);
        conversation.ShouldNotBeNull();
        conversation!.Participants.Where(p => p.CitizenId == CitizenId.Of(Initiator) && p.Role == "initiator").ShouldHaveSingleItem();
        conversation.Participants.Where(p => p.CitizenId == CitizenId.Of(ResolvedTargetAgent) && p.Role == "target").ShouldHaveSingleItem();

        // The relay actually hit the peer endpoint with the configured API key.
        capturedRequest.ShouldNotBeNull();
        capturedRequest!.Headers.TryGetValues("X-Cross-World-Key", out var keys).ShouldBeTrue();
        keys!.ShouldHaveSingleItem().ShouldBe("peer-key");
    }

    [Fact]
    public async Task ConverseCrossWorldAsync_OutboundNotAllowed_ThrowsUnauthorizedAndDoesNotRelay()
    {
        var relayInvoked = false;
        var router = CreateRouter(
            ConfigWith(allowOutbound: false, includePeer: true),
            (_, _) =>
            {
                relayInvoked = true;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
            },
            out _,
            out _);

        CrossWorldAgentReference.TryParse(Target, out var parsed).ShouldBeTrue();

        var act = () => router.ConverseCrossWorldAsync(RequestForTarget(), parsed!, normalizedChain: [Initiator]);

        (await act.ShouldThrowAsync<UnauthorizedAccessException>())
            .Message.ShouldContain("not allowed");
        relayInvoked.ShouldBeFalse();
    }

    [Fact]
    public async Task ConverseCrossWorldAsync_NoPermissionEntryForWorld_ThrowsUnauthorized()
    {
        // Permission list targets a different world, so there is no matching outbound permission.
        var config = new PlatformConfig
        {
            Gateway = new GatewaySettingsConfig
            {
                World = new BotNexus.Domain.WorldIdentity { Id = "world-a", Name = "World A" },
                CrossWorldPermissions =
                [
                    new CrossWorldPermissionConfig
                    {
                        TargetWorldId = "world-z",
                        AllowOutbound = true,
                        AllowedAgents = ["test-agent"]
                    }
                ],
                CrossWorld = new CrossWorldFederationConfig
                {
                    Peers = new Dictionary<string, CrossWorldPeerConfig>
                    {
                        ["world-b"] = new() { Endpoint = "https://gateway-b.internal", ApiKey = "k", Enabled = true }
                    }
                }
            }
        };

        var router = CreateRouter(
            config,
            (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)),
            out _,
            out _);

        CrossWorldAgentReference.TryParse(Target, out var parsed).ShouldBeTrue();

        var act = () => router.ConverseCrossWorldAsync(RequestForTarget(), parsed!, normalizedChain: [Initiator]);

        await act.ShouldThrowAsync<UnauthorizedAccessException>();
    }

    [Fact]
    public async Task ConverseCrossWorldAsync_PermissionAllowedButNoPeer_ThrowsInvalidOperation()
    {
        var router = CreateRouter(
            ConfigWith(allowOutbound: true, includePeer: false),
            (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)),
            out _,
            out _);

        CrossWorldAgentReference.TryParse(Target, out var parsed).ShouldBeTrue();

        var act = () => router.ConverseCrossWorldAsync(RequestForTarget(), parsed!, normalizedChain: [Initiator]);

        (await act.ShouldThrowAsync<InvalidOperationException>())
            .Message.ShouldContain("No cross-world peer configured");
    }

    [Fact]
    public async Task ConverseCrossWorldAsync_AllowedAgentsExcludesInitiator_ThrowsUnauthorized()
    {
        // Permission exists for world-b + AllowOutbound, but the allow-list does not include the initiator.
        var config = new PlatformConfig
        {
            Gateway = new GatewaySettingsConfig
            {
                World = new BotNexus.Domain.WorldIdentity { Id = "world-a", Name = "World A" },
                CrossWorldPermissions =
                [
                    new CrossWorldPermissionConfig
                    {
                        TargetWorldId = "world-b",
                        AllowOutbound = true,
                        AllowedAgents = ["some-other-agent"]
                    }
                ],
                CrossWorld = new CrossWorldFederationConfig
                {
                    Peers = new Dictionary<string, CrossWorldPeerConfig>
                    {
                        ["world-b"] = new() { Endpoint = "https://gateway-b.internal", ApiKey = "k", Enabled = true }
                    }
                }
            }
        };

        var router = CreateRouter(
            config,
            (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)),
            out _,
            out _);

        CrossWorldAgentReference.TryParse(Target, out var parsed).ShouldBeTrue();

        var act = () => router.ConverseCrossWorldAsync(RequestForTarget(), parsed!, normalizedChain: [Initiator]);

        await act.ShouldThrowAsync<UnauthorizedAccessException>();
    }

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => responder(request, cancellationToken);
    }
}
