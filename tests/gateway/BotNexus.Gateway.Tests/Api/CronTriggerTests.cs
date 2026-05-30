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
///    <see cref="CitizenId.TryParse"/> or falling back to the agent itself.
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
                gs.SessionType == SessionType.Cron &&
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
