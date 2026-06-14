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
using BotNexus.Gateway.Conversations;
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
        var conversationStore = new InMemoryConversationStore();
        var sessionStore = new InMemorySessionStore(redactor: null, conversationStore: conversationStore);

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
            conversationStore,
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

        // P9-F (#657): Participants now live on Conversation, not Session — assert via
        // the conversation store using the session's ConversationId.
        var conversation = await conversationStore.GetAsync(session.ConversationId);
        conversation.ShouldNotBeNull();
        conversation!.Participants.Where(p => p.CitizenId == CitizenId.Of(AgentId.From("test-agent")) && p.Role == "initiator").ShouldHaveSingleItem();
        conversation.Participants.Where(p => p.CitizenId == CitizenId.Of(AgentId.From("agent-c")) && p.Role == "target").ShouldHaveSingleItem();

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
            new InMemoryConversationStore(),
            Options.Create(new GatewayOptions()),
            NullLogger<AgentExchangeService>.Instance,
            exchangeOptions: Options.Create(new AgentExchangeOptions { AccessPolicy = "whitelist" }));

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
            new InMemoryConversationStore(),
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
            new InMemoryConversationStore(),
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
        var conversationStore = new InMemoryConversationStore();
        var sessionStore = new InMemorySessionStore(redactor: null, conversationStore: conversationStore);

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
            conversationStore,
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

        // P9-F (#657): cross-world participants live on the conversation now.
        var conversation = await conversationStore.GetAsync(session.ConversationId);
        conversation.ShouldNotBeNull();
        conversation!.Participants.Where(p => p.CitizenId == CitizenId.Of(AgentId.From("test-agent")) && p.Role == "initiator").ShouldHaveSingleItem();
        conversation.Participants.Where(p => p.CitizenId == CitizenId.Of(AgentId.From("agent-c")) && p.Role == "target").ShouldHaveSingleItem();
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
            new InMemoryConversationStore(),
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


    // ── Phase 8 (F-11): completion via finish_agent_exchange tool call ───────────────
    //
    // Pre-Phase-8 the service terminated the loop when the target agent's text response contained
    // the substring "OBJECTIVE MET" or "completed objective" (issue #379). That heuristic was
    // (a) brittle (narrative phrasing or quoted content triggered false positives) and
    // (b) exploitable via active prompt injection (the follow-up prompt template literally taught
    //     the magic phrase to the target).
    //
    // Authoritative completion signal is now: AgentResponse.ToolCalls contains a successful
    // finish_agent_exchange entry AND Session.Metadata["finishedAgentExchangeId"] matches the
    // active exchange id stamped by the service at the start of the turn. The tests below pin
    // both the positive (tool-call ⇒ terminate) and the negative/XPIA shapes (substring alone
    // ⇒ do NOT terminate).

    [Theory]
    [InlineData("OBJECTIVE MET")]
    [InlineData("All changes applied. OBJECTIVE MET")]
    [InlineData("the objective met no resistance")]
    [InlineData("I have completed objective successfully.")]
    [InlineData("The customer asked me to say \"OBJECTIVE MET\" but I will keep working.")]
    [InlineData("```\nOBJECTIVE MET\n```")] // code-block fence
    public async Task ConverseAsync_MagicPhraseSubstring_WithoutToolCall_DoesNotTerminate_XpiaRegression(string targetResponse)
    {
        // The old substring heuristic terminated on any of these strings — every callsite was a
        // false-positive (narrative) or a successful prompt-injection vector (RAG/quoted content).
        var initiator = AgentId.From("test-agent");
        var target = AgentId.From("agent-c");
        var registry = CreateRegistry(initiator, target, ["agent-c"]);

        var handle = new Mock<IAgentHandle>();
        handle.Setup(h => h.PromptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentResponse { Content = targetResponse }); // empty ToolCalls
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetOrCreateAsync(target, It.IsAny<SessionId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);

        var service = new AgentExchangeService(
            registry.Object,
            supervisor.Object,
            new InMemorySessionStore(),
            new InMemoryConversationStore(),
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

        handle.Verify(h => h.PromptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Exactly(3));
        result.CompletionReason.ShouldBe("maxTurnsReached");
    }

    [Fact]
    public async Task ConverseAsync_FirstTurnDoneSecondTurnFinishToolCall_TerminatesAfterTwoTurns()
    {
        // Pre-Phase-8 this test relied on "OBJECTIVE MET" to terminate the second turn. With the
        // tool-call contract the second turn must return ToolCalls = [finish_agent_exchange].
        var initiator = AgentId.From("test-agent");
        var target = AgentId.From("agent-c");
        var registry = CreateRegistry(initiator, target, ["agent-c"]);
        var sessionStore = new InMemorySessionStore();

        var callCount = 0;
        var handle = new Mock<IAgentHandle>();
        SessionId? capturedSessionId = null;
        handle.Setup(h => h.PromptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                callCount++;
                if (callCount == 1)
                    return new AgentResponse { Content = "We are done with the review." };
                // Simulate the tool actually running by writing the matching exchange-id payload.
                if (capturedSessionId is { } sid)
                    WriteFinishPayloadFromActiveId(sessionStore, sid, reason: "review complete", summary: "Two issues found.");
                return new AgentResponse
                {
                    Content = "Calling finish_agent_exchange.",
                    ToolCalls = [new AgentToolCallInfo("call-1", "finish_agent_exchange", IsError: false)]
                };
            });
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor
            .Setup(s => s.GetOrCreateAsync(target, It.IsAny<SessionId>(), It.IsAny<CancellationToken>()))
            .Callback<AgentId, SessionId, CancellationToken>((_, sid, _) => capturedSessionId = sid)
            .ReturnsAsync(handle.Object);

        var service = new AgentExchangeService(
            registry.Object,
            supervisor.Object,
            sessionStore,
            new InMemoryConversationStore(),
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

        callCount.ShouldBe(2);
        result.CompletionReason.ShouldBe("exchangeFinished");
        result.FinishReason.ShouldBe("review complete");
        result.FinishSummary.ShouldBe("Two issues found.");
    }

    [Fact]
    public async Task ConverseAsync_FinishToolCallWithErrorFlag_DoesNotTerminate_SecurityRegression()
    {
        // Defence in depth: an IsError=true finish_agent_exchange tool call (validation failure,
        // exception in the tool body) must not terminate the loop — the service only honours
        // SUCCESSFUL finish signals. Without this guard, a tool-execution failure could
        // accidentally end the exchange.
        var initiator = AgentId.From("test-agent");
        var target = AgentId.From("agent-c");
        var registry = CreateRegistry(initiator, target, ["agent-c"]);

        var handle = new Mock<IAgentHandle>();
        handle.Setup(h => h.PromptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentResponse
            {
                Content = "Tried to finish but the call errored.",
                ToolCalls = [new AgentToolCallInfo("call-1", "finish_agent_exchange", IsError: true)]
            });
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetOrCreateAsync(target, It.IsAny<SessionId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);

        var service = new AgentExchangeService(
            registry.Object,
            supervisor.Object,
            new InMemorySessionStore(),
            new InMemoryConversationStore(),
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
    public async Task ConverseAsync_ToolCallReportedButPayloadMissing_DoesNotTerminate_SecurityRegression()
    {
        // A malicious tool implementation (or a hostile in-process actor) could surface
        // ToolCalls = [finish_agent_exchange { IsError: false }] without writing the matching
        // finishedAgentExchangeId payload. The service must require the payload to match THIS
        // turn's active exchange id; absent payload ⇒ loop continues.
        var initiator = AgentId.From("test-agent");
        var target = AgentId.From("agent-c");
        var registry = CreateRegistry(initiator, target, ["agent-c"]);

        var handle = new Mock<IAgentHandle>();
        handle.Setup(h => h.PromptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentResponse
            {
                Content = "I'm finishing now.",
                // Tool surface but no Session.Metadata side-channel write — should not satisfy
                // the active-exchange-id equality gate.
                ToolCalls = [new AgentToolCallInfo("call-1", "finish_agent_exchange", IsError: false)]
            });
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetOrCreateAsync(target, It.IsAny<SessionId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);

        var service = new AgentExchangeService(
            registry.Object,
            supervisor.Object,
            new InMemorySessionStore(),
            new InMemoryConversationStore(),
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

    /// <summary>
    /// Mirrors what <c>FinishAgentExchangeTool.ExecuteAsync</c> does to the session metadata when
    /// the target agent invokes it. We don't run the real tool in unit tests because the agent
    /// supervisor is mocked — we simulate the tool's side-effect directly so the service's
    /// equality gate in <c>TryConsumeFinishSignal</c> can fire.
    /// </summary>
    private static void WriteFinishPayloadFromActiveId(
        InMemorySessionStore store, SessionId sessionId, string reason, string? summary)
    {
        var s = store.GetAsync(sessionId, CancellationToken.None).GetAwaiter().GetResult();
        if (s is null) return;
        if (!s.Metadata.TryGetValue("activeAgentExchangeId", out var activeRaw) || activeRaw is not string activeId)
            return;
        s.Metadata["finishedAgentExchangeId"] = activeId;
        s.Metadata["finishedAgentExchangeReason"] = reason;
        if (!string.IsNullOrEmpty(summary))
            s.Metadata["finishedAgentExchangeSummary"] = summary;
        store.SaveAsync(s, CancellationToken.None).GetAwaiter().GetResult();
    }

    [Fact]
    public async Task ConverseAsync_FinishToolReportedWithIsErrorAndMatchingPayload_DoesNotTerminate_MutationGuard()
    {
        // Mutation guard (bug-hunt PR #553 missing-test #4): the gate is "tool-call AND payload-id-
        // match AND not-IsError". A mutation that drops the IsError check would accept this case
        // because the payload matches the active id. Both gates MUST be required.
        var initiator = AgentId.From("test-agent");
        var target = AgentId.From("agent-c");
        var registry = CreateRegistry(initiator, target, ["agent-c"]);
        var sessionStore = new InMemorySessionStore();

        SessionId? capturedSessionId = null;
        var handle = new Mock<IAgentHandle>();
        handle.Setup(h => h.PromptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                // Tool writes the matching payload AND surfaces a tool-call entry — but with
                // IsError=true. Without the AND-IsError-check the gate would falsely fire.
                if (capturedSessionId is { } sid)
                    WriteFinishPayloadFromActiveId(sessionStore, sid, reason: "should not be honoured", summary: null);
                return new AgentResponse
                {
                    Content = "Tool errored but wrote payload anyway.",
                    ToolCalls = [new AgentToolCallInfo("call-1", "finish_agent_exchange", IsError: true)]
                };
            });
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor
            .Setup(s => s.GetOrCreateAsync(target, It.IsAny<SessionId>(), It.IsAny<CancellationToken>()))
            .Callback<AgentId, SessionId, CancellationToken>((_, sid, _) => capturedSessionId = sid)
            .ReturnsAsync(handle.Object);

        var service = new AgentExchangeService(
            registry.Object,
            supervisor.Object,
            sessionStore,
            new InMemoryConversationStore(),
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
        result.CompletionReason.ShouldBe("maxTurnsReached",
            "IsError=true MUST short-circuit even when a payload matching the active id has been " +
            "persisted. The two gates (tool-call success AND payload match) are AND, not OR.");
        result.FinishReason.ShouldBeNull();
    }

    [Fact]
    public async Task ConverseAsync_StaleFinishPayloadFromPriorTurn_DoesNotTerminate_DefenceInDepthRegression()
    {
        // Defence-in-depth regression (plan-vs-impl PR #553 suggestion S-1): each turn's
        // PrepareTurn must clear the previous turn's finishedAgentExchangeId so it cannot satisfy
        // the equality gate on the current turn. Without this clear, a tool that fired once on
        // turn 1 (but whose tool-call info wasn't reported, e.g. dropped by the provider) would
        // leave a payload in metadata that could be "replayed" on turn 2 even if turn 2 produces
        // no tool call at all.
        //
        // Scenario: turn 1 writes a payload via the helper (simulating a tool that wrote but
        // failed to surface its tool-call entry); turn 1's response has NO ToolCalls. Turn 2
        // returns plain content with NO ToolCalls. Loop MUST run all MaxTurns turns — neither
        // turn satisfies the gate (turn 1 has payload but no tool call; turn 2 has no tool call
        // AND PrepareTurn cleared the stale payload).
        var initiator = AgentId.From("test-agent");
        var target = AgentId.From("agent-c");
        var registry = CreateRegistry(initiator, target, ["agent-c"]);
        var sessionStore = new InMemorySessionStore();

        var callCount = 0;
        SessionId? capturedSessionId = null;
        var handle = new Mock<IAgentHandle>();
        handle.Setup(h => h.PromptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                callCount++;
                if (callCount == 1 && capturedSessionId is { } sid)
                {
                    // Tool wrote the payload (matching turn 1's activeAgentExchangeId) but did
                    // NOT surface a ToolCalls entry — gate fails on turn 1 due to missing tool
                    // call.
                    WriteFinishPayloadFromActiveId(sessionStore, sid, reason: "stale", summary: null);
                }
                return new AgentResponse { Content = $"Turn {callCount} response, no tool call." };
            });
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor
            .Setup(s => s.GetOrCreateAsync(target, It.IsAny<SessionId>(), It.IsAny<CancellationToken>()))
            .Callback<AgentId, SessionId, CancellationToken>((_, sid, _) => capturedSessionId = sid)
            .ReturnsAsync(handle.Object);

        var service = new AgentExchangeService(
            registry.Object,
            supervisor.Object,
            sessionStore,
            new InMemoryConversationStore(),
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

        handle.Verify(h => h.PromptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Exactly(3),
            "Loop must run all MaxTurns turns. If PrepareTurn fails to clear the stale " +
            "finishedAgentExchangeId from turn 1, turn 2's gate could accept it (turn 2's tool " +
            "call missing, but stale payload present) — terminating early.");
        result.CompletionReason.ShouldBe("maxTurnsReached");
        result.FinishReason.ShouldBeNull();
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
            new InMemoryConversationStore(),
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
    public async Task ConverseAsync_NoObjectiveSet_SingleShotReasonReturned()
    {
        // Wire-value pin (closes #552). When no objective is set, the loop runs exactly one
        // turn (single-shot) and CompletionReason is "singleShot" — renamed from the historical
        // "objectiveMet" because no objective was ever provided in this code path. The rename
        // is a deliberate wire change; the architecture fence in
        // SingleShotWireValueArchitectureTests bans the legacy literal from production source.
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
            new InMemoryConversationStore(),
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
        result.CompletionReason.ShouldBe("singleShot",
            "When no Objective is set, the exchange runs exactly one prompt and CompletionReason " +
            "must report \"singleShot\" — not \"objectiveMet\" (renamed in #552). If this fails " +
            "with the legacy value, the rename has been reverted.");
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
            new InMemoryConversationStore(),
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
            new InMemoryConversationStore(),
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
            new InMemoryConversationStore(),
            Options.Create(new GatewayOptions()),
            NullLogger<AgentExchangeService>.Instance,
            exchangeOptions: Options.Create(new AgentExchangeOptions { AccessPolicy = "whitelist" }));

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
            new InMemoryConversationStore(),
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
    public async Task ConcurrentConverseAsync_OnSameSourceTarget_ProducesDistinctSessionIds_AndIsolatedTranscripts()
    {
        // #551 AC #2: AgentExchangeService.ConverseAsync mints a fresh SessionId.Create() per call
        // (see AgentExchangeService.cs:96), so two concurrent calls on the same initiator/target
        // pair produce DIFFERENT session ids by construction and cannot interleave. This makes
        // the per-session write lock that protects CrossWorldFederationController.RelayAsync
        // intentionally absent here — the per-call sessionId freshness IS the isolation guarantee.
        //
        // This test pins that invariant. A regression that introduced a shared session for
        // concurrent calls (e.g. "reuse the active session for this pair") would cause:
        //   (1) overlapping session ids — caught by the distinct-ids assertion below, OR
        //   (2) interleaved transcripts — caught by the per-session content assertions.
        //
        // Either symptom would invalidate the "no lock needed" reasoning and require either
        // adding the lock here OR reverting the regression. The barrier in the handle ensures
        // both calls are genuinely in flight simultaneously rather than serialised by chance.

        var initiator = AgentId.From("test-agent");
        var target = AgentId.From("agent-c");
        var registry = CreateRegistry(initiator, target, ["agent-c"]);
        var sessionStore = new InMemorySessionStore();

        var bothEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseBoth = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = 0;

        var handle = new Mock<IAgentHandle>();
        handle.Setup(h => h.PromptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(async (string msg, CancellationToken ct) =>
            {
                if (Interlocked.Increment(ref entered) == 2)
                    bothEntered.SetResult();
                // Both callers must reach this point before either proceeds, proving they
                // were ACTUALLY concurrent (not sequenced by Task.Run scheduling order).
                await releaseBoth.Task.WaitAsync(ct);
                return new AgentResponse { Content = $"reply:{msg}" };
            });

        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetOrCreateAsync(target, It.IsAny<SessionId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);

        var service = new AgentExchangeService(
            registry.Object,
            supervisor.Object,
            sessionStore,
            new InMemoryConversationStore(),
            Options.Create(new GatewayOptions()),
            NullLogger<AgentExchangeService>.Instance);

        var taskA = Task.Run(() => service.ConverseAsync(new AgentExchangeRequest
        {
            InitiatorId = initiator,
            TargetId = target,
            Message = "msg-A",
            MaxTurns = 1
        }));
        var taskB = Task.Run(() => service.ConverseAsync(new AgentExchangeRequest
        {
            InitiatorId = initiator,
            TargetId = target,
            Message = "msg-B",
            MaxTurns = 1
        }));

        // Wait until both are genuinely parked inside PromptAsync — proves they're concurrent.
        await bothEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        releaseBoth.SetResult();
        var resultA = await taskA.WaitAsync(TimeSpan.FromSeconds(5));
        var resultB = await taskB.WaitAsync(TimeSpan.FromSeconds(5));

        // Primary invariant: per-call sessionId freshness. If this fails, ConverseAsync has
        // started reusing sessions across concurrent calls and the no-lock assumption breaks.
        resultA.SessionId.ShouldNotBe(resultB.SessionId,
            "Concurrent ConverseAsync calls must produce distinct SessionIds — that's the " +
            "per-call freshness guarantee that justifies skipping the per-session write lock in " +
            "this service. A regression here would re-open the #551 race class in AgentExchangeService.");

        // Secondary invariant: each session's transcript contains ONLY its own turns. Interleaving
        // would manifest as msg-A appearing in session B's transcript or vice versa.
        var sessionA = await sessionStore.GetAsync(resultA.SessionId);
        var sessionB = await sessionStore.GetAsync(resultB.SessionId);
        sessionA.ShouldNotBeNull();
        sessionB.ShouldNotBeNull();

        // Full-shape assertion (bug-hunt LOW #4 on PR #551 critique sweep): a regression that
        // leaks assistant-side content (e.g. shared response buffer, swapped reply queues) would
        // not surface in a user-only filter. Assert each session's entire history shape — counts,
        // ordering, and both user + assistant content — to catch corruption on either side.
        sessionA!.History.Count.ShouldBe(2,
            "Session A must contain exactly its own user + assistant pair (no leaked entries).");
        sessionA.History[0].Role.ShouldBe(MessageRole.User);
        sessionA.History[0].Content.ShouldBe("msg-A");
        sessionA.History[1].Role.ShouldBe(MessageRole.Assistant);
        sessionA.History[1].Content.ShouldBe("reply:msg-A",
            "Caller A's assistant turn is the response keyed to A's user message. A leaked " +
            "'reply:msg-B' here would prove cross-session response cross-attribution.");

        sessionB!.History.Count.ShouldBe(2,
            "Session B must contain exactly its own user + assistant pair (no leaked entries).");
        sessionB.History[0].Role.ShouldBe(MessageRole.User);
        sessionB.History[0].Content.ShouldBe("msg-B");
        sessionB.History[1].Role.ShouldBe(MessageRole.Assistant);
        sessionB.History[1].Content.ShouldBe("reply:msg-B",
            "Caller B's assistant turn is the response keyed to B's user message. A leaked " +
            "'reply:msg-A' here would prove cross-session response cross-attribution.");
    }

    [Fact]
    public async Task ConverseAsync_LocalPath_WhenPromptThrowsNonCancellation_SealsSession_RecordsError_AndClearsActiveExchangeId()
    {
        // Pins the shared RunExchangeLoopAsync error arm (#1384): a non-cancellation exception
        // raised inside a turn must seal the session, stamp Metadata["error"], remove the active
        // exchange id (the local `beforeSeal` cleanup), and rethrow. This was previously the
        // duplicated `catch (Exception ex)` block in ConverseAsync; the refactor single-sources it.
        var initiator = AgentId.From("test-agent");
        var target = AgentId.From("agent-c");
        var registry = CreateRegistry(initiator, target, ["agent-c"]);
        var conversationStore = new InMemoryConversationStore();
        var sessionStore = new InMemorySessionStore(redactor: null, conversationStore: conversationStore);

        var handle = new Mock<IAgentHandle>();
        handle.Setup(h => h.PromptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetOrCreateAsync(target, It.IsAny<SessionId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);

        var service = new AgentExchangeService(
            registry.Object,
            supervisor.Object,
            sessionStore,
            conversationStore,
            Options.Create(new GatewayOptions()),
            NullLogger<AgentExchangeService>.Instance);

        var act = async () => await service.ConverseAsync(new AgentExchangeRequest
        {
            InitiatorId = initiator,
            TargetId = target,
            Message = "hello",
            MaxTurns = 3
        });

        var ex = await act.ShouldThrowAsync<InvalidOperationException>();
        ex.Message.ShouldBe("boom");

        var sessions = await sessionStore.GetExistenceAsync(initiator, new ExistenceQuery());
        sessions.Count.ShouldBe(1, "exactly one session should have been created before the failure");
        var session = await sessionStore.GetAsync(sessions[0].SessionId);
        session.ShouldNotBeNull();

        // A genuine (non-caller-cancellation) failure seals the session and records the error.
        session!.Status.ShouldBe(GatewaySessionStatus.Sealed);
        session.Metadata.ShouldContainKey("error");
        session.Metadata["error"].ShouldBe("boom");

        // beforeSeal removed the active-exchange id so a stale gate cannot be replayed.
        session.Metadata.ShouldNotContainKey(BotNexus.Gateway.Tools.FinishAgentExchangeTool.ActiveExchangeIdKey);

        // The conversation is archived on exchange end (any reason except caller cancellation).
        var conversation = await conversationStore.GetAsync(session.ConversationId);
        conversation.ShouldNotBeNull();
        conversation!.Status.ShouldBe(ConversationStatus.Archived);
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
