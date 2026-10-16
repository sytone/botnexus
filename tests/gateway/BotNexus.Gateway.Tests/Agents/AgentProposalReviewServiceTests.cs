using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Agents;
using BotNexus.Gateway.Agents.Proposals;
using BotNexus.Gateway.Api.Services;
using BotNexus.Gateway.Contracts.Agents;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Conversations;
using BotNexus.Gateway.Sessions;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;
using Moq;

namespace BotNexus.Gateway.Tests.Agents;

public sealed class AgentProposalReviewServiceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "botnexus-proposal-review-tests", Guid.NewGuid().ToString("N"));
    private string DatabasePath => Path.Combine(_directory, "agent-proposals.sqlite");

    [Fact]
    public async Task Rejection_LeavesConfigurationAndRegistryUnchanged()
    {
        await using var store = new SqliteAgentProposalStore(DatabasePath);
        var proposal = Pending(AgentProposalKind.Create);
        await store.CreateAsync(proposal);
        var registry = new DefaultAgentRegistry(NullLogger<DefaultAgentRegistry>.Instance);
        var writer = new Mock<IAgentConfigurationWriter>();
        var conversations = new InMemoryConversationStore();
        var sessions = new InMemorySessionStore(null, conversations);
        var conversationId = ConversationId.From("proposal-conversation");
        var sessionId = SessionId.From("proposal-session");
        await conversations.CreateAsync(new Conversation
        {
            ConversationId = conversationId,
            AgentId = proposal.ProposedBy.AsAgent!.Value,
            Initiator = proposal.ProposedBy,
            ActiveSessionId = sessionId,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await sessions.SaveAsync(new GatewaySession
        {
            SessionId = sessionId,
            AgentId = proposal.ProposedBy.AsAgent.Value,
            ConversationId = conversationId,
        });
        var notifier = new AgentProposalRejectionNotifier(conversations, sessions, TimeProvider.System);
        var suttest = NewSut(store, registry, writer.Object, notifier);

        var result = await suttest.ReviewAsync(
            proposal.ProposalId,
            AgentProposalStatus.Rejected,
            CitizenId.Of(UserId.From("admin")),
            "not needed");

        result.Outcome.ShouldBe(AgentProposalLifecycleOutcome.Rejected);
        registry.Get(proposal.TargetAgentId).ShouldBeNull();
        writer.Verify(w => w.SaveAsync(It.IsAny<AgentDescriptor>(), It.IsAny<CancellationToken>()), Times.Never);
        var persisted = await store.GetAsync(proposal.ProposalId);
        persisted.ShouldNotBeNull();
        persisted.ApplicationStatus.ShouldBe(AgentProposalApplicationStatus.NotRequired);
        var reloaded = await sessions.GetAsync(sessionId);
        reloaded.ShouldNotBeNull();
        var notification = reloaded.History.ShouldHaveSingleItem();
        notification.Role.ShouldBe(MessageRole.Notification);
        notification.Content.ShouldContain(proposal.ProposalId.ToString("D"));
        notification.Content.ShouldContain("not needed");
        notification.SenderId.ShouldBe("agent-proposal-review");
    }

    [Fact]
    public async Task ConcurrentApproval_AppliesTheExactStoredDescriptorOnce()
    {
        await using var store = new SqliteAgentProposalStore(DatabasePath);
        var proposal = Pending(AgentProposalKind.Create);
        await store.CreateAsync(proposal);
        var registry = new DefaultAgentRegistry(NullLogger<DefaultAgentRegistry>.Instance);
        var writer = new Mock<IAgentConfigurationWriter>();
        writer.Setup(w => w.SaveAsync(It.IsAny<AgentDescriptor>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        var suttest = NewSut(store, registry, writer.Object);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<AgentProposalLifecycleResult> ApproveAsync(string reviewer)
        {
            await start.Task;
            return await suttest.ReviewAsync(proposal.ProposalId, AgentProposalStatus.Approved, CitizenId.Of(UserId.From(reviewer)), null);
        }

        var first = ApproveAsync("admin-a");
        var second = ApproveAsync("admin-b");
        start.SetResult();
        var results = await Task.WhenAll(first, second);

        results.Count(result => result.Outcome == AgentProposalLifecycleOutcome.Applied).ShouldBe(1);
        writer.Verify(w => w.SaveAsync(
            It.Is<AgentDescriptor>(d =>
                d.AgentId == proposal.ProposedDescriptor.AgentId
                && d.DisplayName == proposal.ProposedDescriptor.DisplayName
                && d.Description == proposal.ProposedDescriptor.Description
                && d.ModelId == proposal.ProposedDescriptor.ModelId
                && d.ApiProvider == proposal.ProposedDescriptor.ApiProvider
                && d.ToolIds.SequenceEqual(proposal.ProposedDescriptor.ToolIds)),
            It.IsAny<CancellationToken>()), Times.Once);
        var registered = registry.Get(proposal.TargetAgentId);
        registered.ShouldNotBeNull();
        registered.AgentId.ShouldBe(proposal.ProposedDescriptor.AgentId);
        registered.DisplayName.ShouldBe(proposal.ProposedDescriptor.DisplayName);
        registered.Description.ShouldBe(proposal.ProposedDescriptor.Description);
        registered.ModelId.ShouldBe(proposal.ProposedDescriptor.ModelId);
        registered.ApiProvider.ShouldBe(proposal.ProposedDescriptor.ApiProvider);
        registered.ToolIds.ShouldBe(proposal.ProposedDescriptor.ToolIds);
        (await store.GetAsync(proposal.ProposalId)).ShouldNotBeNull().ApplicationStatus.ShouldBe(AgentProposalApplicationStatus.Applied);
    }

    [Fact]
    public async Task ApplicationFailure_IsNotReportedAsSuccessAndPersistsRecoverableEvidence()
    {
        await using var store = new SqliteAgentProposalStore(DatabasePath);
        var proposal = Pending(AgentProposalKind.Create);
        await store.CreateAsync(proposal);
        var registry = new DefaultAgentRegistry(NullLogger<DefaultAgentRegistry>.Instance);
        var writer = new Mock<IAgentConfigurationWriter>();
        writer.Setup(w => w.SaveAsync(It.IsAny<AgentDescriptor>(), It.IsAny<CancellationToken>())).ThrowsAsync(new IOException("read-only config"));
        var suttest = NewSut(store, registry, writer.Object);

        var result = await suttest.ReviewAsync(proposal.ProposalId, AgentProposalStatus.Approved, CitizenId.Of(UserId.From("admin")), null);

        result.Outcome.ShouldBe(AgentProposalLifecycleOutcome.ApplicationFailed);
        registry.Get(proposal.TargetAgentId).ShouldBeNull();
        var persisted = await store.GetAsync(proposal.ProposalId);
        persisted.ShouldNotBeNull();
        persisted.ApplicationStatus.ShouldBe(AgentProposalApplicationStatus.Failed);
        persisted.ApplicationError.ShouldNotBeNull();
        persisted.ApplicationError.ShouldContain("read-only config");
        persisted.ApplicationAttempts.ShouldBe(1);
    }

    [Fact]
    public async Task CompensationFailure_LeavesApplyingAndRequiresReconciliation()
    {
        await using var store = new SqliteAgentProposalStore(DatabasePath);
        var proposal = Pending(AgentProposalKind.Create);
        await store.CreateAsync(proposal);
        var registry = new DefaultAgentRegistry(NullLogger<DefaultAgentRegistry>.Instance);
        var writer = new Mock<IAgentConfigurationWriter>();
        writer.Setup(w => w.SaveAsync(It.IsAny<AgentDescriptor>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        writer.Setup(w => w.DeleteAsync(proposal.TargetAgentId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("compensation unavailable"));
        var provisioner = new Mock<BotNexus.Cron.IHeartbeatProvisioner>();
        provisioner.Setup(p => p.ProvisionAsync(It.IsAny<AgentDescriptor>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("provisioning failed"));
        var lifecycle = new AgentLifecycleService(
            registry, writer.Object, [], provisioner.Object, null, null,
            NullLogger<AgentLifecycleService>.Instance);
        var sut = new AgentProposalReviewService(
            store, lifecycle, TimeProvider.System, NullLogger<AgentProposalReviewService>.Instance);

        var result = await sut.ReviewAsync(
            proposal.ProposalId, AgentProposalStatus.Approved,
            CitizenId.Of(UserId.From("admin")), null);

        result.Outcome.ShouldBe(AgentProposalLifecycleOutcome.ApplicationFailed);
        result.Proposal.ShouldNotBeNull();
        result.Proposal.ApplicationStatus.ShouldBe(AgentProposalApplicationStatus.Applying);
        result.Error.ShouldNotBeNull();
        result.Error.ShouldContain("reconciliation is required");
        (await store.TryBeginApplicationAsync(proposal.ProposalId)).Outcome
            .ShouldBe(AgentProposalApplicationClaimOutcome.NotClaimed);
    }

    private static AgentProposalReviewService NewSut(
        IAgentProposalStore store,
        IAgentRegistry registry,
        IAgentConfigurationWriter writer,
        AgentProposalRejectionNotifier? rejectionNotifier = null)
    {
        var lifecycle = new AgentLifecycleService(
            registry, writer, [], null, null, null, NullLogger<AgentLifecycleService>.Instance);
        return new AgentProposalReviewService(
            store, lifecycle, TimeProvider.System, NullLogger<AgentProposalReviewService>.Instance, rejectionNotifier);
    }

    private static AgentProposal Pending(AgentProposalKind kind)
    {
        var descriptor = new AgentDescriptor
        {
            AgentId = AgentId.From("metrics-analyst"),
            DisplayName = "Metrics Analyst",
            Description = "Exact stored candidate",
            ModelId = "model-a",
            ApiProvider = "provider-a",
            ToolIds = ["memory_save"],
        };

        return new AgentProposal(
            Guid.NewGuid(),
            kind,
            descriptor.AgentId,
            descriptor,
            "Specialist needed",
            CitizenId.Of(AgentId.From("farnsworth")),
            DateTimeOffset.UtcNow,
            AgentProposalStatus.Pending,
            null,
            null,
            null,
            []);
    }

    public void Dispose()
    {
        SqlitePoolCleanup.ClearPoolFor(DatabasePath);
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }
}
