using BotNexus.Domain.AgentExchange;
using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;
using BotNexus.Gateway.Channels;
using System.Net;
using System.Net.Http.Json;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Agents;
using BotNexus.Gateway.Configuration;
using BotNexus.Gateway.Sessions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using GatewaySessionStatus = BotNexus.Gateway.Abstractions.Models.SessionStatus;

namespace BotNexus.Gateway.Tests.Agents;

public sealed class AgentExchangeServiceTests
{
    [Fact]
    public async Task ConverseAsync_SingleTurn_CreatesSealedAgentAgentSessionVisibleToBothAgents()
    {
        var initiator = AgentId.From("test-agent");
        var target = AgentId.From("agent-c");
        var registry = CreateRegistry(initiator, target, ["agent-c"]);
        var sessionStore = new InMemorySessionStore();

        var handle = new Mock<IAgentHandle>();
        handle.Setup(h => h.PromptAsync("Review this design", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentResponse { Content = "Looks good with two fixes." });
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetOrCreateAsync(target, It.IsAny<SessionId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);

        var service = new AgentExchangeService(
            registry.Object,
            supervisor.Object,
            sessionStore,
            Options.Create(new GatewayOptions()),
            NullLogger<AgentExchangeService>.Instance);

        var result = await service.ConverseAsync(new AgentExchangeRequest
        {
            InitiatorId = initiator,
            TargetId = target,
            Message = "Review this design",
            MaxTurns = 1
        });

        result.Status.ShouldBe("sealed");
        result.Turns.ShouldBe(2);
        result.Transcript.Where(entry => entry.Role == "user" && entry.Content == "Review this design").ShouldHaveSingleItem();
        result.Transcript.Where(entry => entry.Role == "assistant" && entry.Content.Contains("Looks good")).ShouldHaveSingleItem();

        var session = await sessionStore.GetAsync(result.SessionId);
        session.ShouldNotBeNull();
        session!.SessionType.ShouldBe(SessionType.AgentAgent);
        session.Status.ShouldBe(GatewaySessionStatus.Sealed);
        session.Participants.Where(p => p.CitizenId == CitizenId.Of(AgentId.From("test-agent")) && p.Role == "initiator").ShouldHaveSingleItem();
        session.Participants.Where(p => p.CitizenId == CitizenId.Of(AgentId.From("agent-c")) && p.Role == "target").ShouldHaveSingleItem();

        var initiatorExistence = await sessionStore.GetExistenceAsync(initiator, new ExistenceQuery());
        var targetExistence = await sessionStore.GetExistenceAsync(target, new ExistenceQuery());
        initiatorExistence.ShouldContain(item => item.SessionId == result.SessionId);
        targetExistence.ShouldContain(item => item.SessionId == result.SessionId);
    }

    [Fact]
    public async Task ConverseAsync_WhenTargetNotAllowed_ThrowsUnauthorizedAccessException()
    {
        var initiator = AgentId.From("test-agent");
        var target = AgentId.From("agent-c");
        var registry = CreateRegistry(initiator, target, []);
        var service = new AgentExchangeService(
            registry.Object,
            Mock.Of<IAgentSupervisor>(),
            new InMemorySessionStore(),
            Options.Create(new GatewayOptions()),
            NullLogger<AgentExchangeService>.Instance);

        Func<Task> action = () => service.ConverseAsync(new AgentExchangeRequest
        {
            InitiatorId = initiator,
            TargetId = target,
            Message = "hello"
        });

        await action.ShouldThrowAsync<UnauthorizedAccessException>();
    }

    [Fact]
    public async Task ConverseAsync_WhenCycleDetected_ThrowsInvalidOperationException()
    {
        var initiator = AgentId.From("test-agent");
        var target = AgentId.From("agent-c");
        var registry = CreateRegistry(initiator, target, ["agent-c"]);
        var service = new AgentExchangeService(
            registry.Object,
            Mock.Of<IAgentSupervisor>(),
            new InMemorySessionStore(),
            Options.Create(new GatewayOptions { AgentConversationMaxDepth = 4 }),
            NullLogger<AgentExchangeService>.Instance);

        Func<Task> action = () => service.ConverseAsync(new AgentExchangeRequest
        {
            InitiatorId = initiator,
            TargetId = target,
            Message = "hello",
            CallChain = [AgentId.From("test-agent"), AgentId.From("agent-c")]
        });

        (await action.ShouldThrowAsync<InvalidOperationException>())
            .Message.ShouldStartWith("Cycle detected:");
    }

    [Fact]
    public async Task ConverseAsync_WhenDepthExceeded_ThrowsInvalidOperationException()
    {
        var initiator = AgentId.From("test-agent");
        var target = AgentId.From("agent-c");
        var registry = CreateRegistry(initiator, target, ["agent-c"]);
        var service = new AgentExchangeService(
            registry.Object,
            Mock.Of<IAgentSupervisor>(),
            new InMemorySessionStore(),
            Options.Create(new GatewayOptions { AgentConversationMaxDepth = 2 }),
            NullLogger<AgentExchangeService>.Instance);

        Func<Task> action = () => service.ConverseAsync(new AgentExchangeRequest
        {
            InitiatorId = initiator,
            TargetId = target,
            Message = "hello",
            CallChain = [AgentId.From("alpha"), AgentId.From("test-agent")]
        });

        (await action.ShouldThrowAsync<InvalidOperationException>())
            .Message.ShouldContain("exceeded maximum configured depth");
    }

    [Fact]
    public async Task ConverseAsync_CrossWorldTarget_CreatesCrossWorldSessionAndRelaysMessage()
    {
        var initiator = AgentId.From("test-agent");
        var target = AgentId.From("world-b:agent-c");
        var registry = CreateRegistry(initiator, AgentId.From("agent-c"), ["world-b:agent-c"]);
        registry.Setup(r => r.Contains(target)).Returns(false);
        var sessionStore = new InMemorySessionStore();

        HttpRequestMessage? capturedRequest = null;
        var handler = new StubHttpMessageHandler((request, _) =>
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
        });
        var adapter = new CrossWorldChannelAdapter(
            NullLogger<CrossWorldChannelAdapter>.Instance,
            new HttpClient(handler));

        var service = new AgentExchangeService(
            registry.Object,
            Mock.Of<IAgentSupervisor>(),
            sessionStore,
            Options.Create(new GatewayOptions()),
            NullLogger<AgentExchangeService>.Instance,
            Options.Create(new PlatformConfig
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
                            AllowedAgents = ["test-agent"]
                        }
                    ],
                    CrossWorld = new CrossWorldFederationConfig
                    {
                        Peers = new Dictionary<string, CrossWorldPeerConfig>
                        {
                            ["world-b"] = new()
                            {
                                Endpoint = "https://gateway-b.internal",
                                ApiKey = "peer-key",
                                Enabled = true
                            }
                        }
                    }
                }
            }),
            adapter);

        var result = await service.ConverseAsync(new AgentExchangeRequest
        {
            InitiatorId = initiator,
            TargetId = target,
            Message = "Hello remote world",
            MaxTurns = 1
        });

        result.Status.ShouldBe("sealed");
        result.FinalResponse.ShouldBe("Remote response");
        var session = await sessionStore.GetAsync(result.SessionId);
        session.ShouldNotBeNull();
        session!.ChannelType.ShouldBe(ChannelKey.From("cross-world"));
        session.Participants.Where(p => p.CitizenId == CitizenId.Of(AgentId.From("test-agent")) && p.Role == "initiator").ShouldHaveSingleItem();
        session.Participants.Where(p => p.CitizenId == CitizenId.Of(AgentId.From("agent-c")) && p.Role == "target").ShouldHaveSingleItem();
        session.Metadata["sourceWorldId"].ShouldBe("world-a");
        session.Metadata["targetWorldId"].ShouldBe("world-b");

        capturedRequest.ShouldNotBeNull();
        capturedRequest!.Headers.TryGetValues("X-Cross-World-Key", out var keys).ShouldBeTrue();
        keys!.ShouldHaveSingleItem().ShouldBe("peer-key");
    }

    [Fact]
    public async Task ConverseAsync_CrossWorldTargetWithoutOutboundPermission_ThrowsUnauthorizedAccessException()
    {
        var initiator = AgentId.From("test-agent");
        var target = AgentId.From("world-b:agent-c");
        var registry = CreateRegistry(initiator, AgentId.From("agent-c"), ["world-b:agent-c"]);
        registry.Setup(r => r.Contains(target)).Returns(false);

        var service = new AgentExchangeService(
            registry.Object,
            Mock.Of<IAgentSupervisor>(),
            new InMemorySessionStore(),
            Options.Create(new GatewayOptions()),
            NullLogger<AgentExchangeService>.Instance,
            Options.Create(new PlatformConfig
            {
                Gateway = new GatewaySettingsConfig
                {
                    CrossWorldPermissions =
                    [
                        new CrossWorldPermissionConfig
                        {
                            TargetWorldId = "world-b",
                            AllowOutbound = false,
                            AllowedAgents = ["test-agent"]
                        }
                    ],
                    CrossWorld = new CrossWorldFederationConfig
                    {
                        Peers = new Dictionary<string, CrossWorldPeerConfig>
                        {
                            ["world-b"] = new()
                            {
                                Endpoint = "https://gateway-b.internal",
                                ApiKey = "peer-key",
                                Enabled = true
                            }
                        }
                    }
                }
            }));

        Func<Task> action = () => service.ConverseAsync(new AgentExchangeRequest
        {
            InitiatorId = initiator,
            TargetId = target,
            Message = "blocked"
        });

        (await action.ShouldThrowAsync<UnauthorizedAccessException>())
            .Message.ShouldContain("not allowed");
    }


    // ── IsObjectiveMet heuristic tests (issue #379) ─────────────────────────────────

    [Theory]
    [InlineData("We are done with the review.")]
    [InlineData("I'm done")]
    [InlineData("done")]
    [InlineData("Almost done here")]
    [InlineData("The work is done!")]
    public async Task ConverseAsync_ResponseContainingDoneButNotObjectiveMet_DoesNotTerminateEarly(string targetResponse)
    {
        // Before fix, "done" caused premature termination — this must NOT terminate after 1 turn
        var initiator = AgentId.From("test-agent");
        var target = AgentId.From("agent-c");
        var registry = CreateRegistry(initiator, target, ["agent-c"]);
        var sessionStore = new InMemorySessionStore();

        var callCount = 0;
        var handle = new Mock<IAgentHandle>();
        handle.Setup(h => h.PromptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                callCount++;
                return new AgentResponse { Content = callCount == 1 ? targetResponse : "OBJECTIVE MET" };
            });
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetOrCreateAsync(target, It.IsAny<SessionId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);

        var service = new AgentExchangeService(
            registry.Object,
            supervisor.Object,
            sessionStore,
            Options.Create(new GatewayOptions()),
            NullLogger<AgentExchangeService>.Instance);

        var result = await service.ConverseAsync(new AgentExchangeRequest
        {
            InitiatorId = initiator,
            TargetId = target,
            Message = "Do the thing",
            Objective = "Complete the task",
            MaxTurns = 3
        });

        // Should have continued past the first "done" response
        callCount.ShouldBe(2);
        result.CompletionReason.ShouldBe("objectiveMet");
    }

    [Fact]
    public async Task ConverseAsync_ResponseContainsObjectiveMet_TerminatesEarly()
    {
        var initiator = AgentId.From("test-agent");
        var target = AgentId.From("agent-c");
        var registry = CreateRegistry(initiator, target, ["agent-c"]);
        var sessionStore = new InMemorySessionStore();

        var handle = new Mock<IAgentHandle>();
        handle.Setup(h => h.PromptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentResponse { Content = "All changes applied. OBJECTIVE MET" });
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetOrCreateAsync(target, It.IsAny<SessionId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);

        var service = new AgentExchangeService(
            registry.Object,
            supervisor.Object,
            sessionStore,
            Options.Create(new GatewayOptions()),
            NullLogger<AgentExchangeService>.Instance);

        var result = await service.ConverseAsync(new AgentExchangeRequest
        {
            InitiatorId = initiator,
            TargetId = target,
            Message = "Do the thing",
            Objective = "Complete the task",
            MaxTurns = 5
        });

        handle.Verify(h => h.PromptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        result.CompletionReason.ShouldBe("objectiveMet");
    }

    [Fact]
    public async Task ConverseAsync_ResponseContainsCompletedObjective_TerminatesEarly()
    {
        var initiator = AgentId.From("test-agent");
        var target = AgentId.From("agent-c");
        var registry = CreateRegistry(initiator, target, ["agent-c"]);

        var handle = new Mock<IAgentHandle>();
        handle.Setup(h => h.PromptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentResponse { Content = "I have completed objective successfully." });
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetOrCreateAsync(target, It.IsAny<SessionId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);

        var service = new AgentExchangeService(
            registry.Object,
            supervisor.Object,
            new InMemorySessionStore(),
            Options.Create(new GatewayOptions()),
            NullLogger<AgentExchangeService>.Instance);

        var result = await service.ConverseAsync(new AgentExchangeRequest
        {
            InitiatorId = initiator,
            TargetId = target,
            Message = "Do the thing",
            Objective = "Complete the task",
            MaxTurns = 5
        });

        handle.Verify(h => h.PromptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        result.CompletionReason.ShouldBe("objectiveMet");
    }

    [Fact]
    public async Task ConverseAsync_MaxTurnsReachedWithoutObjectiveMet_SetsMaxTurnsReachedReason()
    {
        var initiator = AgentId.From("test-agent");
        var target = AgentId.From("agent-c");
        var registry = CreateRegistry(initiator, target, ["agent-c"]);

        var handle = new Mock<IAgentHandle>();
        handle.Setup(h => h.PromptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentResponse { Content = "Still working on it." });
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetOrCreateAsync(target, It.IsAny<SessionId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);

        var service = new AgentExchangeService(
            registry.Object,
            supervisor.Object,
            new InMemorySessionStore(),
            Options.Create(new GatewayOptions()),
            NullLogger<AgentExchangeService>.Instance);

        var result = await service.ConverseAsync(new AgentExchangeRequest
        {
            InitiatorId = initiator,
            TargetId = target,
            Message = "Do the thing",
            Objective = "Complete the task",
            MaxTurns = 2
        });

        handle.Verify(h => h.PromptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
        result.CompletionReason.ShouldBe("maxTurnsReached");
    }

    [Fact]
    public async Task ConverseAsync_NoObjectiveSet_ObjectiveMetReasonReturned()
    {
        // When no objective is set, IsObjectiveMet returns true immediately
        var initiator = AgentId.From("test-agent");
        var target = AgentId.From("agent-c");
        var registry = CreateRegistry(initiator, target, ["agent-c"]);

        var handle = new Mock<IAgentHandle>();
        handle.Setup(h => h.PromptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentResponse { Content = "Here is the answer." });
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetOrCreateAsync(target, It.IsAny<SessionId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);

        var service = new AgentExchangeService(
            registry.Object,
            supervisor.Object,
            new InMemorySessionStore(),
            Options.Create(new GatewayOptions()),
            NullLogger<AgentExchangeService>.Instance);

        var result = await service.ConverseAsync(new AgentExchangeRequest
        {
            InitiatorId = initiator,
            TargetId = target,
            Message = "What is the answer?",
            MaxTurns = 3
        });

        handle.Verify(h => h.PromptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        result.CompletionReason.ShouldBe("objectiveMet");
    }

    [Fact]
    public async Task ConverseAsync_WithSubAgentRole_Succeeds()
    {
        var initiator = AgentId.From("test-agent");
        var target = AgentId.From("specialist-agent");
        var registry = new Mock<IAgentRegistry>();
        registry.Setup(r => r.Get(initiator)).Returns(new AgentDescriptor
        {
            AgentId = initiator,
            DisplayName = "Initiator",
            ModelId = "gpt-5-mini",
            ApiProvider = "copilot",
            SubAgentRoles = ["specialist"]
        });
        registry.Setup(r => r.Get(target)).Returns(new AgentDescriptor
        {
            AgentId = target,
            DisplayName = "Specialist",
            ModelId = "gpt-5-mini",
            ApiProvider = "copilot",
            Metadata = new Dictionary<string, object?> { ["role"] = "specialist" }
        });
        registry.Setup(r => r.Contains(target)).Returns(true);

        var sessionStore = new InMemorySessionStore();
        var handle = new Mock<IAgentHandle>();
        handle.Setup(h => h.PromptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentResponse { Content = "OBJECTIVE MET" });
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetOrCreateAsync(target, It.IsAny<SessionId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);

        var service = new AgentExchangeService(
            registry.Object,
            supervisor.Object,
            sessionStore,
            Options.Create(new GatewayOptions()),
            NullLogger<AgentExchangeService>.Instance);

        var result = await service.ConverseAsync(new AgentExchangeRequest
        {
            InitiatorId = initiator,
            TargetId = target,
            Message = "hello",
            MaxTurns = 1
        });

        result.Status.ShouldBe("sealed");
    }

    [Fact]
    public async Task ConverseAsync_WithSubAgentRole_JsonElementRole_Succeeds()
    {
        var initiator = AgentId.From("test-agent");
        var target = AgentId.From("specialist-agent");
        var registry = new Mock<IAgentRegistry>();
        registry.Setup(r => r.Get(initiator)).Returns(new AgentDescriptor
        {
            AgentId = initiator,
            DisplayName = "Initiator",
            ModelId = "gpt-5-mini",
            ApiProvider = "copilot",
            SubAgentRoles = ["specialist"]
        });
        // role stored as a JsonElement (as it would be when loaded from JSON config)
        var meta = new Dictionary<string, object?>
        {
            ["role"] = System.Text.Json.JsonDocument.Parse("\"specialist\"").RootElement
        };
        registry.Setup(r => r.Get(target)).Returns(new AgentDescriptor
        {
            AgentId = target,
            DisplayName = "Specialist",
            ModelId = "gpt-5-mini",
            ApiProvider = "copilot",
            Metadata = meta
        });
        registry.Setup(r => r.Contains(target)).Returns(true);

        var sessionStore = new InMemorySessionStore();
        var handle = new Mock<IAgentHandle>();
        handle.Setup(h => h.PromptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentResponse { Content = "OBJECTIVE MET" });
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetOrCreateAsync(target, It.IsAny<SessionId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);

        var service = new AgentExchangeService(
            registry.Object,
            supervisor.Object,
            sessionStore,
            Options.Create(new GatewayOptions()),
            NullLogger<AgentExchangeService>.Instance);

        var result = await service.ConverseAsync(new AgentExchangeRequest
        {
            InitiatorId = initiator,
            TargetId = target,
            Message = "hello",
            MaxTurns = 1
        });

        result.Status.ShouldBe("sealed");
    }

    [Fact]
    public async Task ConverseAsync_WithSubAgentRole_NoMatchFails()
    {
        var initiator = AgentId.From("test-agent");
        var target = AgentId.From("researcher-agent");
        var registry = new Mock<IAgentRegistry>();
        registry.Setup(r => r.Get(initiator)).Returns(new AgentDescriptor
        {
            AgentId = initiator,
            DisplayName = "Initiator",
            ModelId = "gpt-5-mini",
            ApiProvider = "copilot",
            SubAgentRoles = ["specialist"]
        });
        registry.Setup(r => r.Get(target)).Returns(new AgentDescriptor
        {
            AgentId = target,
            DisplayName = "Researcher",
            ModelId = "gpt-5-mini",
            ApiProvider = "copilot",
            Metadata = new Dictionary<string, object?> { ["role"] = "researcher" }
        });
        registry.Setup(r => r.Contains(target)).Returns(true);

        var service = new AgentExchangeService(
            registry.Object,
            Mock.Of<IAgentSupervisor>(),
            new InMemorySessionStore(),
            Options.Create(new GatewayOptions()),
            NullLogger<AgentExchangeService>.Instance);

        Func<Task> action = () => service.ConverseAsync(new AgentExchangeRequest
        {
            InitiatorId = initiator,
            TargetId = target,
            Message = "hello"
        });

        await action.ShouldThrowAsync<UnauthorizedAccessException>();
    }

    [Fact]
    public async Task ConverseAsync_WithEmptySubAgentRoles_StillChecksIds()
    {
        var initiator = AgentId.From("test-agent");
        var target = AgentId.From("agent-c");
        var registry = CreateRegistry(initiator, target, ["agent-c"]);
        var sessionStore = new InMemorySessionStore();

        var handle = new Mock<IAgentHandle>();
        handle.Setup(h => h.PromptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentResponse { Content = "OBJECTIVE MET" });
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetOrCreateAsync(target, It.IsAny<SessionId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);

        var service = new AgentExchangeService(
            registry.Object,
            supervisor.Object,
            sessionStore,
            Options.Create(new GatewayOptions()),
            NullLogger<AgentExchangeService>.Instance);

        var result = await service.ConverseAsync(new AgentExchangeRequest
        {
            InitiatorId = initiator,
            TargetId = target,
            Message = "hello",
            MaxTurns = 1
        });

        result.Status.ShouldBe("sealed");
    }

        private static Mock<IAgentRegistry> CreateRegistry(AgentId initiator, AgentId target, IReadOnlyList<string> allowedTargets)
    {
        var registry = new Mock<IAgentRegistry>();
        registry.Setup(r => r.Get(initiator)).Returns(new AgentDescriptor
        {
            AgentId = initiator,
            DisplayName = "Initiator",
            ModelId = "gpt-5-mini",
            ApiProvider = "copilot",
            SubAgentIds = allowedTargets
        });
        registry.Setup(r => r.Contains(target)).Returns(true);
        return registry;
    }

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => responder(request, cancellationToken);
    }
}
