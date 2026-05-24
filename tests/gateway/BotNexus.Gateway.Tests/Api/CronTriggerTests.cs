using BotNexus.Domain.Primitives;
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

public sealed class CronTriggerTests
{
    [Fact]
    public void CronTrigger_ImplementsInternalTrigger_NotChannelAdapter()
    {
        typeof(IInternalTrigger).IsAssignableFrom(typeof(CronTrigger)).ShouldBeTrue();
        typeof(IChannelAdapter).IsAssignableFrom(typeof(CronTrigger)).ShouldBeFalse();
    }

    [Fact]
    public async Task CreateSessionAsync_FirstRun_CreatesPerJobConversation()
    {
        // Arrange: no existing conversations
        var (sessionStore, conversationStore, supervisor) = BuildStandardMocks();
        Conversation? createdConversation = null;

        conversationStore
            .Setup(s => s.ListAsync(It.IsAny<AgentId?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Conversation>());
        conversationStore
            .Setup(s => s.CreateAsync(It.IsAny<Conversation>(), It.IsAny<CancellationToken>()))
            .Callback<Conversation, CancellationToken>((c, _) => createdConversation = c)
            .Returns<Conversation, CancellationToken>((c, _) => Task.FromResult(c));

        var trigger = new CronTrigger(supervisor.Object, conversationStore.Object, sessionStore.Object, NullLogger<CronTrigger>.Instance);

        // Act
        var sessionId = await trigger.CreateSessionAsync(
            AgentId.From("agent-a"),
            "Scheduled task",
            request: new InternalTriggerRequest { CronJobId = JobId.From("job-1"), JobName = "Scheduled Maintenance", ModelOverride = "openai/gpt-4.1" });

        // Assert: conversation created with human-readable job name as title
        createdConversation.ShouldNotBeNull();
        createdConversation!.Title.ShouldBe("Scheduled Maintenance");
        createdConversation.AgentId.ShouldBe(AgentId.From("agent-a"));
        createdConversation.IsDefault.ShouldBeFalse();
        conversationStore.Verify(s => s.CreateAsync(It.IsAny<Conversation>(), It.IsAny<CancellationToken>()), Times.Once);

        // Session ID in expected format
        sessionId.Value.ShouldStartWith("cron:job-1:");

        // Session metadata stamped correctly
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
    public async Task CreateSessionAsync_SecondRun_ReusesExistingJobConversation()
    {
        // Arrange: conversation for this job already exists
        var existingConversation = new Conversation
        {
            ConversationId = ConversationId.From("conv-job-1"),
            AgentId = AgentId.From("agent-a"),
            Title = "Scheduled Maintenance",
            IsDefault = false,
            Status = ConversationStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-1),
            UpdatedAt = DateTimeOffset.UtcNow.AddDays(-1)
        };

        var (sessionStore, conversationStore, supervisor) = BuildStandardMocks();

        conversationStore
            .Setup(s => s.ListAsync(It.IsAny<AgentId?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Conversation> { existingConversation });

        var trigger = new CronTrigger(supervisor.Object, conversationStore.Object, sessionStore.Object, NullLogger<CronTrigger>.Instance);

        // Act
        var sessionId = await trigger.CreateSessionAsync(
            AgentId.From("agent-a"),
            "Scheduled task run 2",
            request: new InternalTriggerRequest { CronJobId = JobId.From("job-1"), JobName = "Scheduled Maintenance" });

        // Assert: no new conversation created — existing one reused
        conversationStore.Verify(s => s.CreateAsync(It.IsAny<Conversation>(), It.IsAny<CancellationToken>()), Times.Never);

        // Existing conversation updated to point to new session
        conversationStore.Verify(s => s.SaveAsync(
            It.Is<Conversation>(c => c.ConversationId == ConversationId.From("conv-job-1")),
            It.IsAny<CancellationToken>()), Times.Once);

        // Fresh session ID per run
        sessionId.Value.ShouldStartWith("cron:job-1:");
    }

    [Fact]
    public async Task CreateSessionAsync_ExplicitConversationId_BypassesJobConversationLookup()
    {
        // Arrange: job pinned to a specific existing conversation
        var pinnedConversation = new Conversation
        {
            ConversationId = ConversationId.From("conv-pinned"),
            AgentId = AgentId.From("agent-a"),
            Title = "My custom conversation",
            IsDefault = false,
            Status = ConversationStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        var (sessionStore, conversationStore, supervisor) = BuildStandardMocks();

        conversationStore
            .Setup(s => s.GetAsync(ConversationId.From("conv-pinned"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(pinnedConversation);

        var trigger = new CronTrigger(supervisor.Object, conversationStore.Object, sessionStore.Object, NullLogger<CronTrigger>.Instance);

        // Act
        await trigger.CreateSessionAsync(
            AgentId.From("agent-a"),
            "Pinned task",
            request: new InternalTriggerRequest { CronJobId = JobId.From("job-2"), ConversationId = ConversationId.From("conv-pinned") });

        // Assert: no ListAsync (bypassed), no CreateAsync
        conversationStore.Verify(s => s.ListAsync(It.IsAny<AgentId?>(), It.IsAny<CancellationToken>()), Times.Never);
        conversationStore.Verify(s => s.CreateAsync(It.IsAny<Conversation>(), It.IsAny<CancellationToken>()), Times.Never);

        // Pinned conversation updated
        conversationStore.Verify(s => s.SaveAsync(
            It.Is<Conversation>(c => c.ConversationId == ConversationId.From("conv-pinned")),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CreateSessionAsync_EachRun_GetsDistinctSessionId()
    {
        var jobConversation = new Conversation
        {
            ConversationId = ConversationId.From("conv-job"),
            AgentId = AgentId.From("agent-a"),
            Title = "Scheduled Maintenance",
            IsDefault = false,
            Status = ConversationStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        var (sessionStore, conversationStore, supervisor) = BuildStandardMocks();
        var listCallCount = 0;

        conversationStore
            .Setup(s => s.ListAsync(It.IsAny<AgentId?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                listCallCount++;
                return listCallCount == 1
                    ? new List<Conversation>()
                    : new List<Conversation> { jobConversation };
            });
        conversationStore
            .Setup(s => s.CreateAsync(It.IsAny<Conversation>(), It.IsAny<CancellationToken>()))
            .Returns<Conversation, CancellationToken>((c, _) =>
            {
                jobConversation = c with { ConversationId = c.ConversationId };
                return Task.FromResult(c);
            });

        var trigger = new CronTrigger(supervisor.Object, conversationStore.Object, sessionStore.Object, NullLogger<CronTrigger>.Instance);

        var first = await trigger.CreateSessionAsync(AgentId.From("agent-a"), "Run 1",
            request: new InternalTriggerRequest { CronJobId = JobId.From("job-1"), JobName = "Scheduled Maintenance" });
        var second = await trigger.CreateSessionAsync(AgentId.From("agent-a"), "Run 2",
            request: new InternalTriggerRequest { CronJobId = JobId.From("job-1"), JobName = "Scheduled Maintenance" });

        // Different session IDs per run
        first.ShouldNotBe(second);
        first.Value.ShouldStartWith("cron:job-1:");
        second.Value.ShouldStartWith("cron:job-1:");

        // First run created the conversation, second reused it
        conversationStore.Verify(s => s.CreateAsync(It.IsAny<Conversation>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CreateSessionAsync_WhenCreateRaces_ReusesConversationCreatedByPeerRun()
    {
        var racedConversation = new Conversation
        {
            ConversationId = ConversationId.From("cronconv:agent-a:job-1"),
            AgentId = AgentId.From("agent-a"),
            Title = "Scheduled Maintenance",
            IsDefault = false,
            Status = ConversationStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        var (sessionStore, conversationStore, supervisor) = BuildStandardMocks();
        conversationStore
            .SetupSequence(s => s.GetAsync(It.IsAny<ConversationId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Conversation?)null)
            .ReturnsAsync(racedConversation);
        conversationStore
            .Setup(s => s.ListAsync(It.IsAny<AgentId?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Conversation>());
        conversationStore
            .Setup(s => s.CreateAsync(It.IsAny<Conversation>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Duplicate id"));

        var trigger = new CronTrigger(supervisor.Object, conversationStore.Object, sessionStore.Object, NullLogger<CronTrigger>.Instance);

        var sessionId = await trigger.CreateSessionAsync(
            AgentId.From("agent-a"),
            "Scheduled task",
            request: new InternalTriggerRequest { CronJobId = JobId.From("job-1"), JobName = "Scheduled Maintenance" });

        sessionId.Value.ShouldStartWith("cron:job-1:");
        conversationStore.Verify(s => s.CreateAsync(It.IsAny<Conversation>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CreateSessionAsync_WhenDuplicateActiveJobConversationsExist_NormalizesToSingleConversation()
    {
        var canonical = new Conversation
        {
            ConversationId = ConversationId.From("conv-job-1-new"),
            AgentId = AgentId.From("agent-a"),
            Title = "Scheduled Maintenance",
            IsDefault = false,
            Status = ConversationStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow.AddHours(-2),
            UpdatedAt = DateTimeOffset.UtcNow.AddHours(-1)
        };

        var duplicate = new Conversation
        {
            ConversationId = ConversationId.From("conv-job-1-old"),
            AgentId = AgentId.From("agent-a"),
            Title = "Scheduled Maintenance",
            IsDefault = false,
            Status = ConversationStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-1),
            UpdatedAt = DateTimeOffset.UtcNow.AddDays(-1)
        };

        var (sessionStore, conversationStore, supervisor) = BuildStandardMocks();
        var legacySession = new GatewaySession
        {
            SessionId = SessionId.From("cron:job-1:legacy"),
            AgentId = AgentId.From("agent-a"),
            UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-10)
        };
        legacySession.Session.ConversationId = duplicate.ConversationId;

        conversationStore
            .Setup(s => s.GetAsync(It.IsAny<ConversationId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Conversation?)null);
        conversationStore
            .Setup(s => s.ListAsync(It.IsAny<AgentId?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Conversation> { canonical, duplicate });
        sessionStore
            .Setup(s => s.ListAsync(It.IsAny<AgentId?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<GatewaySession> { legacySession });

        var trigger = new CronTrigger(supervisor.Object, conversationStore.Object, sessionStore.Object, NullLogger<CronTrigger>.Instance);

        await trigger.CreateSessionAsync(
            AgentId.From("agent-a"),
            "Scheduled task",
            request: new InternalTriggerRequest { CronJobId = JobId.From("job-1"), JobName = "Scheduled Maintenance" });

        conversationStore.Verify(s => s.CreateAsync(It.IsAny<Conversation>(), It.IsAny<CancellationToken>()), Times.Never);
        conversationStore.Verify(s => s.ArchiveAsync(duplicate.ConversationId, It.IsAny<CancellationToken>()), Times.Once);
        sessionStore.Verify(s => s.SaveAsync(
            It.Is<GatewaySession>(gs => gs.SessionId == legacySession.SessionId &&
                                        gs.Session.ConversationId == canonical.ConversationId),
            It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }


    [Fact]
    public async Task CreateSessionAsync_WithJobName_UsesJobNameAsConversationTitle()
    {
        // Arrange: no existing conversations
        var (sessionStore, conversationStore, supervisor) = BuildStandardMocks();
        Conversation? createdConversation = null;

        conversationStore
            .Setup(s => s.ListAsync(It.IsAny<AgentId?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Conversation>());
        conversationStore
            .Setup(s => s.CreateAsync(It.IsAny<Conversation>(), It.IsAny<CancellationToken>()))
            .Callback<Conversation, CancellationToken>((c, _) => createdConversation = c)
            .Returns<Conversation, CancellationToken>((c, _) => Task.FromResult(c));

        var trigger = new CronTrigger(supervisor.Object, conversationStore.Object, sessionStore.Object, NullLogger<CronTrigger>.Instance);

        // Act
        await trigger.CreateSessionAsync(
            AgentId.From("agent-a"),
            "Run task",
            request: new InternalTriggerRequest { CronJobId = JobId.From("job-abc"), JobName = "Autonomous Issue and PR Maintenance" });

        // Assert: conversation title is human-readable job name, not the job-id slug
        createdConversation.ShouldNotBeNull();
        createdConversation!.Title.ShouldBe("Autonomous Issue and PR Maintenance");
    }

    [Fact]
    public async Task CreateSessionAsync_WithoutJobName_FallsBackToJobIdSlugTitle()
    {
        // Arrange: no existing conversations, no job name provided
        var (sessionStore, conversationStore, supervisor) = BuildStandardMocks();
        Conversation? createdConversation = null;

        conversationStore
            .Setup(s => s.ListAsync(It.IsAny<AgentId?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Conversation>());
        conversationStore
            .Setup(s => s.CreateAsync(It.IsAny<Conversation>(), It.IsAny<CancellationToken>()))
            .Callback<Conversation, CancellationToken>((c, _) => createdConversation = c)
            .Returns<Conversation, CancellationToken>((c, _) => Task.FromResult(c));

        var trigger = new CronTrigger(supervisor.Object, conversationStore.Object, sessionStore.Object, NullLogger<CronTrigger>.Instance);

        // Act
        await trigger.CreateSessionAsync(
            AgentId.From("agent-a"),
            "Run task",
            request: new InternalTriggerRequest { CronJobId = JobId.From("job-1") });

        // Assert: falls back to slug-based title
        createdConversation.ShouldNotBeNull();
        createdConversation!.Title.ShouldBe("cron:job-1");
    }

    [Fact]
    public async Task CreateSessionAsync_AfterConversationResolved_SetsResolvedConversationIdOnRequest()
    {
        // Arrange: no existing conversations
        var (sessionStore, conversationStore, supervisor) = BuildStandardMocks();
        Conversation? createdConversation = null;

        conversationStore
            .Setup(s => s.ListAsync(It.IsAny<AgentId?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Conversation>());
        conversationStore
            .Setup(s => s.CreateAsync(It.IsAny<Conversation>(), It.IsAny<CancellationToken>()))
            .Callback<Conversation, CancellationToken>((c, _) => createdConversation = c)
            .Returns<Conversation, CancellationToken>((c, _) => Task.FromResult(c));

        var trigger = new CronTrigger(supervisor.Object, conversationStore.Object, sessionStore.Object, NullLogger<CronTrigger>.Instance);

        var request = new InternalTriggerRequest { CronJobId = JobId.From("job-1"), JobName = "My Job" };

        // Act
        await trigger.CreateSessionAsync(
            AgentId.From("agent-a"),
            "Run task",
            request: request);

        // Assert: ResolvedConversationId is set so the scheduler can pin it back to the job
        request.ResolvedConversationId!.Value.Value.ShouldNotBeNullOrWhiteSpace();
        createdConversation.ShouldNotBeNull();
        request.ResolvedConversationId!.Value.Value.ShouldBe(createdConversation!.ConversationId.Value);
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
