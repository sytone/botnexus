using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Abstractions.Triggers;
using BotNexus.Gateway.Api.Triggers;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace BotNexus.Gateway.Tests.Api;

/// <summary>
/// Tests for the P9-D <see cref="CronTrigger"/> contract.
///
/// Under P9-D the cron job owns its conversation (canonical link via
/// <see cref="BotNexus.Cron.CronJob.ConversationId"/>). The trigger has two paths:
///
/// 1. Fast-path: scheduler passes <c>request.ConversationId</c> for a job already pinned →
///    trigger looks up that conversation by id (un-archives if needed) and reuses it.
///    No <see cref="IConversationStore.ListAsync"/> and no <see cref="IConversationStore.CreateAsync"/>.
///
/// 2. Slow-path: no pin (first run, or pinned conversation was hard-deleted) → trigger
///    creates a fresh conversation with a random <c>conv:&lt;guid&gt;</c> id, titled after
///    the job, with the initiator derived from <c>CreatedBy</c> via
///    <see cref="CitizenId.TryParse(string?, out CitizenId)"/> or falling back to the agent itself.
///    Write-back of <c>ResolvedConversationId</c> lets the scheduler perform a CAS pinback.
/// </summary>
public sealed class CronTriggerTests
{
    [Fact]
    public void CronTrigger_ImplementsInternalTrigger_NotChannelAdapter()
    {
        typeof(IInternalTrigger).IsAssignableFrom(typeof(CronTrigger)).ShouldBeTrue();
        typeof(IChannelAdapter).IsAssignableFrom(typeof(CronTrigger)).ShouldBeFalse();
    }

    [Fact]
    public async Task CreateSessionAsync_FirstRun_NoPin_CreatesFreshConversation_WithJobNameTitle()
    {
        var (sessionStore, conversationStore, supervisor) = BuildStandardMocks();
        Conversation? createdConversation = null;

        conversationStore
            .Setup(s => s.CreateAsync(It.IsAny<Conversation>(), It.IsAny<CancellationToken>()))
            .Callback<Conversation, CancellationToken>((c, _) => createdConversation = c)
            .Returns<Conversation, CancellationToken>((c, _) => Task.FromResult(c));

        var trigger = new CronTrigger(supervisor.Object, conversationStore.Object, sessionStore.Object, NullLogger<CronTrigger>.Instance);

        var sessionId = await trigger.CreateSessionAsync(
            AgentId.From("agent-a"),
            "Scheduled task",
            request: new InternalTriggerRequest
            {
                CronJobId = JobId.From("job-1"),
                JobName = "Scheduled Maintenance"
            });

        createdConversation.ShouldNotBeNull();
        createdConversation!.Title.ShouldBe("Scheduled Maintenance");
        createdConversation.AgentId.ShouldBe(AgentId.From("agent-a"));
        createdConversation.IsDefault.ShouldBeFalse();
        createdConversation.ConversationId.Value.ShouldStartWith("conv:");

        // Fast-path is bypassed (no pin given), so no per-agent enumeration.
        conversationStore.Verify(s => s.ListAsync(It.IsAny<AgentId?>(), It.IsAny<CancellationToken>()), Times.Never);
        conversationStore.Verify(s => s.CreateAsync(It.IsAny<Conversation>(), It.IsAny<CancellationToken>()), Times.Once);

        sessionId.Value.ShouldStartWith("cron:job-1:");

        // Cron metadata on the session.
        sessionStore.Verify(s => s.SaveAsync(
            It.Is<GatewaySession>(gs =>
                // P9-E (#645): cron sessions are now SessionType.UserAgent (proxy for the
                // user who scheduled the job); the "cron" ChannelType + per-turn Trigger
                // stamp carry the proxy-origin signal that used to live on SessionType.
                gs.SessionType == SessionType.UserAgent &&
                gs.ChannelType == ChannelKey.From("cron") &&
                gs.Metadata.ContainsKey("cronJobId") &&
                gs.Metadata["cronJobId"] != null &&
                gs.Metadata["cronJobId"]!.ToString() == "job-1"),
            It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }

    [Fact]
    public async Task CreateSessionAsync_FirstRun_WithoutJobName_FallsBackToGenericTitle()
    {
        var (sessionStore, conversationStore, supervisor) = BuildStandardMocks();
        Conversation? createdConversation = null;

        conversationStore
            .Setup(s => s.CreateAsync(It.IsAny<Conversation>(), It.IsAny<CancellationToken>()))
            .Callback<Conversation, CancellationToken>((c, _) => createdConversation = c)
            .Returns<Conversation, CancellationToken>((c, _) => Task.FromResult(c));

        var trigger = new CronTrigger(supervisor.Object, conversationStore.Object, sessionStore.Object, NullLogger<CronTrigger>.Instance);

        await trigger.CreateSessionAsync(
            AgentId.From("agent-a"),
            "Run task",
            request: new InternalTriggerRequest { CronJobId = JobId.From("job-1") });

        createdConversation.ShouldNotBeNull();
        createdConversation!.Title.ShouldBe("Cron");
    }

    [Fact]
    public async Task CreateSessionAsync_PinnedConversation_FastPath_ReusesPin_WithoutListOrCreate()
    {
        var pinnedConversation = new Conversation
        {
            ConversationId = ConversationId.From("conv:pinned-1"),
            AgentId = AgentId.From("agent-a"),
            Title = "My custom conversation",
            IsDefault = false,
            Status = ConversationStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        var (sessionStore, conversationStore, supervisor) = BuildStandardMocks();

        conversationStore
            .Setup(s => s.GetAsync(ConversationId.From("conv:pinned-1"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(pinnedConversation);

        var trigger = new CronTrigger(supervisor.Object, conversationStore.Object, sessionStore.Object, NullLogger<CronTrigger>.Instance);

        await trigger.CreateSessionAsync(
            AgentId.From("agent-a"),
            "Pinned task",
            request: new InternalTriggerRequest
            {
                CronJobId = JobId.From("job-2"),
                ConversationId = ConversationId.From("conv:pinned-1")
            });

        conversationStore.Verify(s => s.ListAsync(It.IsAny<AgentId?>(), It.IsAny<CancellationToken>()), Times.Never);
        conversationStore.Verify(s => s.CreateAsync(It.IsAny<Conversation>(), It.IsAny<CancellationToken>()), Times.Never);
        conversationStore.Verify(s => s.SaveAsync(
            It.Is<Conversation>(c => c.ConversationId == ConversationId.From("conv:pinned-1")),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CreateSessionAsync_PinnedConversation_WhenArchived_ReactivatesItOnReuse()
    {
        var archivedPin = new Conversation
        {
            ConversationId = ConversationId.From("conv:pinned-archived"),
            AgentId = AgentId.From("agent-a"),
            Title = "Archived pin",
            IsDefault = false,
            Status = ConversationStatus.Archived,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        var (sessionStore, conversationStore, supervisor) = BuildStandardMocks();
        conversationStore
            .Setup(s => s.GetAsync(archivedPin.ConversationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(archivedPin);

        var trigger = new CronTrigger(supervisor.Object, conversationStore.Object, sessionStore.Object, NullLogger<CronTrigger>.Instance);

        await trigger.CreateSessionAsync(
            AgentId.From("agent-a"),
            "Run task",
            request: new InternalTriggerRequest
            {
                CronJobId = JobId.From("job-3"),
                ConversationId = archivedPin.ConversationId
            });

        archivedPin.Status.ShouldBe(ConversationStatus.Active);
        conversationStore.Verify(s => s.SaveAsync(
            It.Is<Conversation>(c => c.ConversationId == archivedPin.ConversationId && c.Status == ConversationStatus.Active),
            It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }

    [Fact]
    public async Task CreateSessionAsync_PinnedConversation_WhenMissing_CreatesFreshConversation()
    {
        var (sessionStore, conversationStore, supervisor) = BuildStandardMocks();
        conversationStore
            .Setup(s => s.GetAsync(It.IsAny<ConversationId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Conversation?)null);

        Conversation? createdConversation = null;
        conversationStore
            .Setup(s => s.CreateAsync(It.IsAny<Conversation>(), It.IsAny<CancellationToken>()))
            .Callback<Conversation, CancellationToken>((c, _) => createdConversation = c)
            .Returns<Conversation, CancellationToken>((c, _) => Task.FromResult(c));

        var trigger = new CronTrigger(supervisor.Object, conversationStore.Object, sessionStore.Object, NullLogger<CronTrigger>.Instance);

        await trigger.CreateSessionAsync(
            AgentId.From("agent-a"),
            "Run task",
            request: new InternalTriggerRequest
            {
                CronJobId = JobId.From("job-4"),
                JobName = "Recovered",
                ConversationId = ConversationId.From("conv:gone")
            });

        createdConversation.ShouldNotBeNull();
        createdConversation!.ConversationId.Value.ShouldStartWith("conv:");
        createdConversation.Title.ShouldBe("Recovered");
    }

    [Fact]
    public async Task CreateSessionAsync_NewConversation_WithUserCreatedBy_RecordsUserAsInitiator()
    {
        var (sessionStore, conversationStore, supervisor) = BuildStandardMocks();
        Conversation? createdConversation = null;

        conversationStore
            .Setup(s => s.CreateAsync(It.IsAny<Conversation>(), It.IsAny<CancellationToken>()))
            .Callback<Conversation, CancellationToken>((c, _) => createdConversation = c)
            .Returns<Conversation, CancellationToken>((c, _) => Task.FromResult(c));

        var trigger = new CronTrigger(supervisor.Object, conversationStore.Object, sessionStore.Object, NullLogger<CronTrigger>.Instance);

        await trigger.CreateSessionAsync(
            AgentId.From("agent-a"),
            "Scheduled by alice",
            request: new InternalTriggerRequest
            {
                CronJobId = JobId.From("job-5"),
                JobName = "Alice's task",
                CreatedBy = "user:alice"
            });

        createdConversation.ShouldNotBeNull();
        createdConversation!.Initiator.ShouldBe(CitizenId.Of(UserId.From("alice")));
    }

    [Fact]
    public async Task CreateSessionAsync_NewConversation_WithoutCreatedBy_FallsBackToAgentAsInitiator()
    {
        var (sessionStore, conversationStore, supervisor) = BuildStandardMocks();
        Conversation? createdConversation = null;

        conversationStore
            .Setup(s => s.CreateAsync(It.IsAny<Conversation>(), It.IsAny<CancellationToken>()))
            .Callback<Conversation, CancellationToken>((c, _) => createdConversation = c)
            .Returns<Conversation, CancellationToken>((c, _) => Task.FromResult(c));

        var trigger = new CronTrigger(supervisor.Object, conversationStore.Object, sessionStore.Object, NullLogger<CronTrigger>.Instance);

        await trigger.CreateSessionAsync(
            AgentId.From("agent-a"),
            "System job",
            request: new InternalTriggerRequest
            {
                CronJobId = JobId.From("job-6"),
                JobName = "Heartbeat",
                CreatedBy = null
            });

        createdConversation.ShouldNotBeNull();
        createdConversation!.Initiator.ShouldBe(CitizenId.Of(AgentId.From("agent-a")));
    }

    [Fact]
    public async Task CreateSessionAsync_EachRun_GetsDistinctSessionId()
    {
        var (sessionStore, conversationStore, supervisor) = BuildStandardMocks();

        var trigger = new CronTrigger(supervisor.Object, conversationStore.Object, sessionStore.Object, NullLogger<CronTrigger>.Instance);

        var first = await trigger.CreateSessionAsync(AgentId.From("agent-a"), "Run 1",
            request: new InternalTriggerRequest { CronJobId = JobId.From("job-1"), JobName = "Maintenance" });
        var second = await trigger.CreateSessionAsync(AgentId.From("agent-a"), "Run 2",
            request: new InternalTriggerRequest { CronJobId = JobId.From("job-1"), JobName = "Maintenance" });

        first.ShouldNotBe(second);
        first.Value.ShouldStartWith("cron:job-1:");
        second.Value.ShouldStartWith("cron:job-1:");
    }

    [Fact]
    public async Task CreateSessionAsync_AfterConversationResolved_WritesBackResolvedConversationId()
    {
        var (sessionStore, conversationStore, supervisor) = BuildStandardMocks();
        Conversation? createdConversation = null;

        conversationStore
            .Setup(s => s.CreateAsync(It.IsAny<Conversation>(), It.IsAny<CancellationToken>()))
            .Callback<Conversation, CancellationToken>((c, _) => createdConversation = c)
            .Returns<Conversation, CancellationToken>((c, _) => Task.FromResult(c));

        var trigger = new CronTrigger(supervisor.Object, conversationStore.Object, sessionStore.Object, NullLogger<CronTrigger>.Instance);

        var request = new InternalTriggerRequest { CronJobId = JobId.From("job-1"), JobName = "My Job" };

        await trigger.CreateSessionAsync(AgentId.From("agent-a"), "Run task", request: request);

        createdConversation.ShouldNotBeNull();
        request.ResolvedConversationId.ShouldNotBeNull();
        request.ResolvedConversationId!.Value.ShouldBe(createdConversation!.ConversationId);
    }

    [Fact]
    public async Task CreateSessionAsync_PinnedFastPath_WritesBackPinnedConversationId()
    {
        var pinnedConversation = new Conversation
        {
            ConversationId = ConversationId.From("conv:pinned-write-back"),
            AgentId = AgentId.From("agent-a"),
            Title = "Pinned",
            IsDefault = false,
            Status = ConversationStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        var (sessionStore, conversationStore, supervisor) = BuildStandardMocks();
        conversationStore
            .Setup(s => s.GetAsync(pinnedConversation.ConversationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(pinnedConversation);

        var trigger = new CronTrigger(supervisor.Object, conversationStore.Object, sessionStore.Object, NullLogger<CronTrigger>.Instance);

        var request = new InternalTriggerRequest
        {
            CronJobId = JobId.From("job-7"),
            ConversationId = pinnedConversation.ConversationId
        };

        await trigger.CreateSessionAsync(AgentId.From("agent-a"), "Run", request: request);

        // ResolvedConversationId reflects whatever conversation the trigger landed on. In the
        // fast-path this is the pin itself — the scheduler reads this value and skips the
        // CAS pinback if it already matches the value stored on the cron job.
        request.ResolvedConversationId.ShouldBe(pinnedConversation.ConversationId);
    }

    // ── #656 ordering tests ─────────────────────────────────────────────────

    /// <summary>
    /// Regression test for #656: user entry must NOT be in session history when the
    /// agent handle is created. Adding it before GetOrCreateAsync causes the model to
    /// receive a duplicate user message, which suppresses tool call execution.
    /// The test verifies ordering: metadata-only save → handle creation → transcript save.
    /// </summary>
    [Fact]
    public async Task CreateSessionAsync_AgentHandleCreated_WithNoUserEntryInHistory()
    {
        var sessionStore = new Mock<ISessionStore>(MockBehavior.Strict);
        var conversationStore = new Mock<IConversationStore>();
        var supervisor = new Mock<IAgentSupervisor>();
        var handle = new Mock<IAgentHandle>();

        handle.Setup(h => h.PromptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentResponse { Content = "assistant-response" });

        // Track calls in order: [0] = GetOrCreate, [1] = first Save (metadata), [2] = GetOrCreate via supervisor, [3] = second Save (transcript)
        var callLog = new List<string>();
        GatewaySession? capturedSession = null;

        sessionStore
            .Setup(s => s.GetOrCreateAsync(It.IsAny<SessionId>(), It.IsAny<AgentId>(), It.IsAny<CancellationToken>()))
            .Returns<SessionId, AgentId, CancellationToken>((sid, aid, _) =>
            {
                capturedSession = new GatewaySession { SessionId = sid, AgentId = aid };
                callLog.Add("GetOrCreate");
                return Task.FromResult(capturedSession);
            });
        sessionStore
            .Setup(s => s.SaveAsync(It.IsAny<GatewaySession>(), It.IsAny<CancellationToken>()))
            .Callback<GatewaySession, CancellationToken>((s, _) =>
                callLog.Add($"Save:history={s.History.Count}"))
            .Returns(Task.CompletedTask);

        conversationStore
            .Setup(s => s.CreateAsync(It.IsAny<Conversation>(), It.IsAny<CancellationToken>()))
            .Returns<Conversation, CancellationToken>((c, _) => Task.FromResult(c));
        conversationStore.Setup(s => s.SaveAsync(It.IsAny<Conversation>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        supervisor
            .Setup(s => s.GetOrCreateAsync(It.IsAny<AgentId>(), It.IsAny<SessionId>(), It.IsAny<CancellationToken>()))
            .Callback(() => callLog.Add("Supervisor.GetOrCreate"))
            .ReturnsAsync(handle.Object);

        var trigger = new CronTrigger(supervisor.Object, conversationStore.Object, sessionStore.Object, NullLogger<CronTrigger>.Instance);

        await trigger.CreateSessionAsync(
            AgentId.From("agent-a"),
            "Scheduled prompt",
            request: new InternalTriggerRequest { CronJobId = JobId.From("job-x"), JobName = "Test job" });

        // Expected order: GetOrCreate (session store), Save (metadata only, 0 entries),
        //                 Supervisor.GetOrCreate (handle creation), Save (transcript, 2 entries)
        callLog.ShouldBe(
            ["GetOrCreate", "Save:history=0", "Supervisor.GetOrCreate", "Save:history=2"],
            "User entry must not exist in session at handle-creation time — duplicate user message suppresses tool calls (#656)");
    }

    /// <summary>
    /// Regression test for #656: both user and assistant entries must appear in the final
    /// session save, with the user entry stamped Trigger = Cron.
    /// </summary>
    [Fact]
    public async Task CreateSessionAsync_FinalSessionSave_ContainsBothUserAndAssistantEntries()
    {
        var sessionStore = new Mock<ISessionStore>();
        var conversationStore = new Mock<IConversationStore>();
        var supervisor = new Mock<IAgentSupervisor>();
        var handle = new Mock<IAgentHandle>();

        handle.Setup(h => h.PromptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentResponse { Content = "model-answer" });

        GatewaySession? lastSavedSession = null;

        sessionStore
            .Setup(s => s.GetOrCreateAsync(It.IsAny<SessionId>(), It.IsAny<AgentId>(), It.IsAny<CancellationToken>()))
            .Returns<SessionId, AgentId, CancellationToken>((sid, aid, _) =>
                Task.FromResult(new GatewaySession { SessionId = sid, AgentId = aid }));
        sessionStore
            .Setup(s => s.SaveAsync(It.IsAny<GatewaySession>(), It.IsAny<CancellationToken>()))
            .Callback<GatewaySession, CancellationToken>((s, _) => lastSavedSession = s)
            .Returns(Task.CompletedTask);

        conversationStore
            .Setup(s => s.CreateAsync(It.IsAny<Conversation>(), It.IsAny<CancellationToken>()))
            .Returns<Conversation, CancellationToken>((c, _) => Task.FromResult(c));
        conversationStore.Setup(s => s.SaveAsync(It.IsAny<Conversation>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        supervisor
            .Setup(s => s.GetOrCreateAsync(It.IsAny<AgentId>(), It.IsAny<SessionId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);

        var trigger = new CronTrigger(supervisor.Object, conversationStore.Object, sessionStore.Object, NullLogger<CronTrigger>.Instance);

        await trigger.CreateSessionAsync(
            AgentId.From("agent-b"),
            "Do the thing",
            request: new InternalTriggerRequest { CronJobId = JobId.From("job-y"), JobName = "Nightly task" });

        lastSavedSession.ShouldNotBeNull();
        lastSavedSession!.History.Count.ShouldBe(2, "Final save must contain exactly user + assistant entries");

        var userEntry = lastSavedSession.History.FirstOrDefault(e => e.Role == MessageRole.User);
        var assistantEntry = lastSavedSession.History.FirstOrDefault(e => e.Role == MessageRole.Assistant);

        userEntry.ShouldNotBeNull();
        userEntry!.Content.ShouldBe("Do the thing");
        userEntry.Trigger.ShouldBe(TriggerType.Cron, "User entry must carry Cron trigger stamp (P9-E)");

        assistantEntry.ShouldNotBeNull();
        assistantEntry!.Content.ShouldBe("model-answer");
    }

    // ── #867 session ownership tests ────────────────────────────────────────

    /// <summary>
    /// Regression test for #867: cron session must NOT overwrite ActiveSessionId when
    /// a human (SignalR) session already holds it. The cron messages still flow to the
    /// portal via the ConversationId group — the pointer just stays on the human session.
    /// </summary>
    [Fact]
    public async Task CreateSessionAsync_HumanSessionHoldsActiveSessionId_DoesNotOverwrite()
    {
        var humanSessionId = SessionId.From("signalr:abc123");
        var pinnedConversation = new Conversation
        {
            ConversationId = ConversationId.From("conv:human-conv"),
            AgentId = AgentId.From("agent-a"),
            Title = "Human conversation",
            IsDefault = false,
            Status = ConversationStatus.Active,
            ActiveSessionId = humanSessionId,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        var (sessionStore, conversationStore, supervisor) = BuildStandardMocks();
        conversationStore
            .Setup(s => s.GetAsync(pinnedConversation.ConversationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(pinnedConversation);

        var savedConversations = new List<Conversation>();
        conversationStore
            .Setup(s => s.SaveAsync(It.IsAny<Conversation>(), It.IsAny<CancellationToken>()))
            .Callback<Conversation, CancellationToken>((c, _) => savedConversations.Add(c))
            .Returns(Task.CompletedTask);

        var trigger = new CronTrigger(supervisor.Object, conversationStore.Object, sessionStore.Object, NullLogger<CronTrigger>.Instance);

        await trigger.CreateSessionAsync(
            AgentId.From("agent-a"),
            "Cron task",
            request: new InternalTriggerRequest
            {
                CronJobId = JobId.From("job-x"),
                ConversationId = pinnedConversation.ConversationId
            });

        // ActiveSessionId must remain pointing at the human session — never the cron session
        pinnedConversation.ActiveSessionId.ShouldBe(humanSessionId,
            "Cron session must not overwrite ActiveSessionId when a human session holds it (#867)");
    }

    /// <summary>
    /// #867: cron fires on its own cronconv: conversation — ActiveSessionId MUST be stamped
    /// (no human session is being displaced).
    /// </summary>
    [Fact]
    public async Task CreateSessionAsync_CronOwnsConversation_StampsActiveSessionId()
    {
        var cronConversation = new Conversation
        {
            ConversationId = ConversationId.From("cronconv:farnsworth:job-heartbeat"),
            AgentId = AgentId.From("agent-a"),
            Title = "Heartbeat",
            IsDefault = false,
            Status = ConversationStatus.Active,
            ActiveSessionId = null,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        var (sessionStore, conversationStore, supervisor) = BuildStandardMocks();
        conversationStore
            .Setup(s => s.GetAsync(cronConversation.ConversationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(cronConversation);

        var trigger = new CronTrigger(supervisor.Object, conversationStore.Object, sessionStore.Object, NullLogger<CronTrigger>.Instance);

        await trigger.CreateSessionAsync(
            AgentId.From("agent-a"),
            "heartbeat",
            request: new InternalTriggerRequest
            {
                CronJobId = JobId.From("job-heartbeat"),
                ConversationId = cronConversation.ConversationId
            });

        // ActiveSessionId should now be set to the cron session
        cronConversation.ActiveSessionId.ShouldNotBeNull(
            "Cron session should stamp ActiveSessionId when no human session holds it");
        cronConversation.ActiveSessionId!.Value.Value.ShouldStartWith("cron:");
    }

    /// <summary>
    /// #867: cron fires on a human conversation whose ActiveSessionId is NULL
    /// (no human session is connected) — cron SHOULD claim it.
    /// </summary>
    [Fact]
    public async Task CreateSessionAsync_NullActiveSessionId_CronClaimsIt()
    {
        var humanConversation = new Conversation
        {
            ConversationId = ConversationId.From("conv:unattended-conv"),
            AgentId = AgentId.From("agent-a"),
            Title = "Unattended conversation",
            IsDefault = false,
            Status = ConversationStatus.Active,
            ActiveSessionId = null,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        var (sessionStore, conversationStore, supervisor) = BuildStandardMocks();
        conversationStore
            .Setup(s => s.GetAsync(humanConversation.ConversationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(humanConversation);

        var trigger = new CronTrigger(supervisor.Object, conversationStore.Object, sessionStore.Object, NullLogger<CronTrigger>.Instance);

        await trigger.CreateSessionAsync(
            AgentId.From("agent-a"),
            "post summary",
            request: new InternalTriggerRequest
            {
                CronJobId = JobId.From("job-summary"),
                ConversationId = humanConversation.ConversationId
            });

        humanConversation.ActiveSessionId.ShouldNotBeNull(
            "Cron should claim ActiveSessionId when it is null — no human session to protect");
        humanConversation.ActiveSessionId!.Value.Value.ShouldStartWith("cron:");
    }

    // ── #864 reactivation notify tests ───────────────────────────────────────

    /// <summary>
    /// Regression test for #864: when CronTrigger reactivates an archived pinned conversation,
    /// it must fire a SignalR notification via IConversationChangeNotifier so the portal
    /// sidebar reflects the conversation status change without a page reload.
    /// </summary>
    [Fact]
    public async Task CreateSessionAsync_PinnedArchivedConversation_FiresReactivationNotify()
    {
        var archivedConversation = new Conversation
        {
            ConversationId = ConversationId.From("conv:archived-notify-test"),
            AgentId = AgentId.From("agent-n"),
            Title = "Archived Job",
            IsDefault = false,
            Status = ConversationStatus.Archived,
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-7),
            UpdatedAt = DateTimeOffset.UtcNow.AddDays(-7)
        };

        var (sessionStore, conversationStore, supervisor) = BuildStandardMocks();
        conversationStore
            .Setup(s => s.GetAsync(archivedConversation.ConversationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(archivedConversation);

        var notifier = new Mock<IConversationChangeNotifier>();
        notifier
            .Setup(n => n.NotifyConversationChangedAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var trigger = new CronTrigger(
            supervisor.Object,
            conversationStore.Object,
            sessionStore.Object,
            NullLogger<CronTrigger>.Instance,
            notifier.Object);

        await trigger.CreateSessionAsync(
            AgentId.From("agent-n"),
            "Run archived job",
            request: new InternalTriggerRequest
            {
                CronJobId = JobId.From("job-archived"),
                ConversationId = archivedConversation.ConversationId
            });

        // The archived conversation must be reactivated to Active
        archivedConversation.Status.ShouldBe(ConversationStatus.Active,
            "CronTrigger must reactivate an archived pinned conversation (#864)");

        // The notifier must fire exactly once with the correct parameters
        notifier.Verify(
            n => n.NotifyConversationChangedAsync(
                "updated",
                "agent-n",
                archivedConversation.ConversationId.Value,
                It.IsAny<CancellationToken>()),
            Times.Once,
            "IConversationChangeNotifier must be called once when an archived conversation is reactivated (#864)");
    }

    /// <summary>
    /// When no IConversationChangeNotifier is injected (null), reactivating an archived
    /// conversation must not throw -- the notifier is optional for backward compatibility.
    /// </summary>
    [Fact]
    public async Task CreateSessionAsync_PinnedArchivedConversation_NoNotifier_DoesNotThrow()
    {
        var archivedConversation = new Conversation
        {
            ConversationId = ConversationId.From("conv:archived-no-notifier"),
            AgentId = AgentId.From("agent-nn"),
            Title = "Archived Job No Notifier",
            IsDefault = false,
            Status = ConversationStatus.Archived,
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-7),
            UpdatedAt = DateTimeOffset.UtcNow.AddDays(-7)
        };

        var (sessionStore, conversationStore, supervisor) = BuildStandardMocks();
        conversationStore
            .Setup(s => s.GetAsync(archivedConversation.ConversationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(archivedConversation);

        // No changeNotifier -- constructor omits it (null by default)
        var trigger = new CronTrigger(
            supervisor.Object,
            conversationStore.Object,
            sessionStore.Object,
            NullLogger<CronTrigger>.Instance);

        // Must not throw even without a notifier
        await trigger.CreateSessionAsync(
            AgentId.From("agent-nn"),
            "Run archived job without notifier",
            request: new InternalTriggerRequest
            {
                CronJobId = JobId.From("job-no-notifier"),
                ConversationId = archivedConversation.ConversationId
            });

        archivedConversation.Status.ShouldBe(ConversationStatus.Active,
            "Conversation must still be reactivated even when notifier is absent");
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static (Mock<ISessionStore>, Mock<IConversationStore>, Mock<IAgentSupervisor>) BuildStandardMocks()
    {
        var sessionStore = new Mock<ISessionStore>();
        var conversationStore = new Mock<IConversationStore>();
        var supervisor = new Mock<IAgentSupervisor>();
        var handle = new Mock<IAgentHandle>();

        handle.Setup(h => h.PromptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentResponse { Content = "cron-response" });

        sessionStore
            .Setup(s => s.GetOrCreateAsync(It.IsAny<SessionId>(), It.IsAny<AgentId>(), It.IsAny<CancellationToken>()))
            .Returns<SessionId, AgentId, CancellationToken>((sid, aid, _) =>
                Task.FromResult(new GatewaySession { SessionId = sid, AgentId = aid }));
        sessionStore
            .Setup(s => s.SaveAsync(It.IsAny<GatewaySession>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        conversationStore
            .Setup(s => s.SaveAsync(It.IsAny<Conversation>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        conversationStore
            .Setup(s => s.ArchiveAsync(It.IsAny<ConversationId>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        supervisor
            .Setup(s => s.GetOrCreateAsync(It.IsAny<AgentId>(), It.IsAny<SessionId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);

        return (sessionStore, conversationStore, supervisor);
    }
}
