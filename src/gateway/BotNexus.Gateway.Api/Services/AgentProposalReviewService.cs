using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Registry;
using BotNexus.Cron;
using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Extensions;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Contracts.Agents;
using BotNexus.Gateway.Extensions;
using BotNexus.Gateway.Webhooks;
using Microsoft.Extensions.Logging;

namespace BotNexus.Gateway.Api.Services;

/// <summary>Outcome returned by proposal review and application.</summary>
public enum AgentProposalLifecycleOutcome
{
    /// <summary>The proposal was applied.</summary>
    Applied,
    /// <summary>The proposal was rejected.</summary>
    Rejected,
    /// <summary>The proposal already had a terminal decision.</summary>
    AlreadyReviewed,
    /// <summary>The proposal was absent.</summary>
    NotFound,
    /// <summary>Lifecycle application failed.</summary>
    ApplicationFailed
}
/// <summary>Authoritative proposal review result.</summary>
/// <param name="Outcome">Review/application outcome.</param><param name="Proposal">Stored proposal.</param><param name="Error">Failure text.</param>
public sealed record AgentProposalLifecycleResult(AgentProposalLifecycleOutcome Outcome, AgentProposal? Proposal, string? Error = null);

/// <summary>Error category used to preserve existing REST status semantics.</summary>
public enum AgentLifecycleFailureKind
{
    /// <summary>Candidate validation failed.</summary>
    Validation,
    /// <summary>A create collided with an existing agent.</summary>
    Conflict,
    /// <summary>An update target was absent.</summary>
    NotFound,
    /// <summary>Configuration persistence failed.</summary>
    Persistence,
    /// <summary>Downstream provisioning failed and compensation succeeded.</summary>
    Provisioning,
    /// <summary>Compensation failed, so external reconciliation is required before retry.</summary>
    ReconciliationRequired
}
/// <summary>Typed canonical lifecycle failure.</summary>
public sealed class AgentLifecycleException(AgentLifecycleFailureKind kind, string message, Exception? inner = null) : Exception(message, inner)
{
    /// <summary>Failure category.</summary>
    public AgentLifecycleFailureKind Kind { get; } = kind;
}

/// <summary>Canonical create/update implementation shared by REST administration and proposal application.</summary>
public sealed class AgentLifecycleService(
    IAgentRegistry registry,
    IAgentConfigurationWriter configurationWriter,
    IEnumerable<IAgentChangeNotifier> changeNotifiers,
    IHeartbeatProvisioner? heartbeatProvisioner,
    ISkillReviewProvisioner? skillReviewProvisioner,
    IAgentWebhookProvisioner? webhookProvisioner,
    ILogger<AgentLifecycleService> logger,
    ModelRegistry? modelRegistry = null,
    IExtensionLoader? extensionLoader = null)
{
    private readonly IReadOnlyList<IAgentChangeNotifier> _changeNotifiers = changeNotifiers.ToArray();

    /// <summary>Creates or updates an agent with canonical validation, persistence, and compensation.</summary>
    public async Task<AgentDescriptor> ApplyAsync(AgentProposalKind kind, AgentDescriptor submitted, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(submitted);
        var previous = registry.Get(submitted.AgentId);
        if (kind is AgentProposalKind.Create && previous is not null)
            throw new AgentLifecycleException(AgentLifecycleFailureKind.Conflict, $"Agent '{submitted.AgentId.Value}' is already registered.");
        if (kind is AgentProposalKind.Update && previous is null)
            throw new AgentLifecycleException(AgentLifecycleFailureKind.NotFound, $"Agent '{submitted.AgentId.Value}' is not registered.");
        if (submitted.Kind == AgentKind.SubAgent)
            throw new AgentLifecycleException(AgentLifecycleFailureKind.Validation, "Kind = SubAgent is reserved for runtime-spawned sub-agents and may not be set via the REST API.");

        // Server-owned defaults are canonical for every caller, including proposal application.
        var descriptor = submitted with
        {
            DefaultExtensionConfig = previous?.DefaultExtensionConfig
                ?? new Dictionary<string, System.Text.Json.JsonElement>()
        };
        var validationErrors = BotNexus.Gateway.Agents.AgentDescriptorValidator.ValidateForConfig(descriptor, null, modelRegistry);
        if (validationErrors.Count > 0)
            throw new AgentLifecycleException(AgentLifecycleFailureKind.Validation, string.Join(" ", validationErrors));
        if (extensionLoader is not null)
        {
            var extensionErrors = ExtensionConfigurationScopeValidator.ValidateAgentChanges(previous, descriptor, extensionLoader.GetLoaded());
            if (extensionErrors.Count > 0)
                throw new AgentLifecycleException(AgentLifecycleFailureKind.Validation, string.Join(" ", extensionErrors));
        }

        try { await configurationWriter.SaveAsync(descriptor, cancellationToken).ConfigureAwait(false); }
        catch (Exception ex) { throw new AgentLifecycleException(AgentLifecycleFailureKind.Persistence, $"Failed to persist agent configuration: {ex.Message}", ex); }

        try
        {
            if (kind is AgentProposalKind.Create) registry.Register(descriptor);
            else if (!registry.Update(descriptor.AgentId, descriptor))
                throw new AgentLifecycleException(AgentLifecycleFailureKind.NotFound, $"Agent '{descriptor.AgentId.Value}' was removed during application.");
        }
        catch (Exception ex)
        {
            if (kind is AgentProposalKind.Create)
                await CompensateAsync(kind, previous, descriptor.AgentId, restorePrevious: false, runtimeCommitted: false).ConfigureAwait(false);
            else
                await DeletePersistedConfigBestEffortAsync(descriptor.AgentId).ConfigureAwait(false);
            if (ex is AgentLifecycleException lifecycle) throw lifecycle;
            throw new AgentLifecycleException(AgentLifecycleFailureKind.Conflict, ex.Message, ex);
        }

        try
        {
            if (heartbeatProvisioner is not null) await heartbeatProvisioner.ProvisionAsync(descriptor, cancellationToken).ConfigureAwait(false);
            if (skillReviewProvisioner is not null) await skillReviewProvisioner.ProvisionAsync(descriptor, cancellationToken).ConfigureAwait(false);
            if (webhookProvisioner is not null) await webhookProvisioner.ProvisionAsync(descriptor, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await CompensateAsync(kind, previous, descriptor.AgentId, restorePrevious: true, runtimeCommitted: true).ConfigureAwait(false);
            throw new AgentLifecycleException(AgentLifecycleFailureKind.Provisioning, $"Failed to provision agent side effects: {ex.Message}", ex);
        }

        foreach (var notifier in _changeNotifiers)
        {
            try { await notifier.NotifyAgentsChangedAsync(kind is AgentProposalKind.Create ? "added" : "updated", descriptor.AgentId.Value, cancellationToken).ConfigureAwait(false); }
            catch (Exception ex) { logger.LogWarning(ex, "Failed to publish agent change for {AgentId}.", descriptor.AgentId.Value); }
        }
        return descriptor;
    }

    private async Task DeletePersistedConfigBestEffortAsync(AgentId agentId)
    {
        try { await configurationWriter.DeleteAsync(agentId, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to delete persisted config for concurrently removed agent {AgentId}.", agentId.Value);
            throw new AgentLifecycleException(
                AgentLifecycleFailureKind.ReconciliationRequired,
                $"Lifecycle compensation failed for agent '{agentId.Value}'; external reconciliation is required.",
                ex);
        }
    }

    private async Task CompensateAsync(
        AgentProposalKind kind,
        AgentDescriptor? previous,
        AgentId agentId,
        bool restorePrevious,
        bool runtimeCommitted)
    {
        try
        {
            if (kind is AgentProposalKind.Create)
            {
                if (runtimeCommitted)
                    registry.Unregister(agentId);
                await configurationWriter.DeleteAsync(agentId, CancellationToken.None).ConfigureAwait(false);
            }
            else if (restorePrevious && previous is not null)
            {
                registry.Update(agentId, previous);
                await configurationWriter.SaveAsync(previous, CancellationToken.None).ConfigureAwait(false);
            }
            else
            {
                await configurationWriter.DeleteAsync(agentId, CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to compensate lifecycle application for agent {AgentId}.", agentId.Value);
            throw new AgentLifecycleException(
                AgentLifecycleFailureKind.ReconciliationRequired,
                $"Lifecycle compensation failed for agent '{agentId.Value}'; external reconciliation is required.",
                ex);
        }
    }
}

/// <summary>Persists a proposer-visible rejection notice without starting an agent run.</summary>
public sealed class AgentProposalRejectionNotifier(IConversationStore conversations, ISessionStore sessions, TimeProvider timeProvider)
{
    /// <summary>Appends a durable notification row to the proposer's active persisted session.</summary>
    public async Task NotifyAsync(AgentProposal proposal, CancellationToken cancellationToken)
    {
        if (proposal.ProposedBy.AsAgent is not { } proposer) return;
        var candidates = await conversations.ListForCitizenAsync(proposal.ProposedBy, cancellationToken).ConfigureAwait(false);
        var conversation = candidates.Where(c => c.Status == ConversationStatus.Active)
            .OrderByDescending(c => c.UpdatedAt).FirstOrDefault()
            ?? throw new InvalidOperationException($"No active persisted conversation exists for proposer '{proposer.Value}'.");
        var conversationSessions = await sessions.ListByConversationAsync(conversation.ConversationId, agentId: null, cancellationToken).ConfigureAwait(false);
        var session = conversation.ActiveSessionId is { } active
            ? conversationSessions.FirstOrDefault(s => s.SessionId == active)
            : conversationSessions.OrderByDescending(s => s.UpdatedAt).FirstOrDefault();
        if (session is null)
            throw new InvalidOperationException($"No persisted session exists for proposer conversation '{conversation.ConversationId.Value}'.");
        var reason = string.IsNullOrWhiteSpace(proposal.ReviewReason) ? "No reason was supplied." : proposal.ReviewReason;
        var entry = new SessionEntry
        {
            Role = MessageRole.Notification,
            Content = $"Agent proposal {proposal.ProposalId:D} for '{proposal.TargetAgentId.Value}' was rejected. Reason: {reason}",
            Timestamp = timeProvider.GetUtcNow(),
            SenderId = "agent-proposal-review",
            PersistenceKey = $"agent-proposal-rejected:{proposal.ProposalId:D}"
        };
        var result = await sessions.AppendEntriesAsync(session.SessionId, [entry], cancellationToken).ConfigureAwait(false);
        if (result.Outcome is not SessionMutationOutcome.Applied)
            throw new InvalidOperationException($"Could not persist proposer notification: session append returned {result.Outcome}.");
    }
}

/// <summary>Owns the atomic review and application boundary for governed proposals.</summary>
public sealed class AgentProposalReviewService(
    IAgentProposalStore proposals,
    AgentLifecycleService lifecycle,
    TimeProvider timeProvider,
    ILogger<AgentProposalReviewService> logger,
    AgentProposalRejectionNotifier? rejectionNotifier = null)
{
    /// <summary>Records a terminal review and applies an approval at most once.</summary>
    public async Task<AgentProposalLifecycleResult> ReviewAsync(Guid proposalId, AgentProposalStatus decision, BotNexus.Domain.World.CitizenId reviewer, string? reason, CancellationToken cancellationToken = default)
    {
        if (decision is AgentProposalStatus.Pending) throw new ArgumentException("A review decision must be approved or rejected.", nameof(decision));
        var reviewed = await proposals.ReviewAsync(proposalId, decision, reviewer, reason, timeProvider.GetUtcNow(), cancellationToken).ConfigureAwait(false);
        if (reviewed.Outcome is AgentProposalReviewOutcome.NotFound) return new(AgentProposalLifecycleOutcome.NotFound, null);
        var proposal = reviewed.Proposal!;
        if (reviewed.Outcome is AgentProposalReviewOutcome.AlreadyReviewed && proposal.Status != decision) return new(AgentProposalLifecycleOutcome.AlreadyReviewed, proposal);
        if (decision is AgentProposalStatus.Rejected)
        {
            // Same-decision retries also attempt delivery. The transcript persistence key makes
            // this safe after an ambiguous append while allowing recovery from a missing destination.
            if (rejectionNotifier is not null)
                await rejectionNotifier.NotifyAsync(proposal, cancellationToken).ConfigureAwait(false);
            return new(reviewed.Outcome is AgentProposalReviewOutcome.Applied ? AgentProposalLifecycleOutcome.Rejected : AgentProposalLifecycleOutcome.AlreadyReviewed, proposal);
        }
        var claim = await proposals.TryBeginApplicationAsync(proposalId, cancellationToken).ConfigureAwait(false);
        if (claim.Outcome is not AgentProposalApplicationClaimOutcome.Claimed) return new(AgentProposalLifecycleOutcome.AlreadyReviewed, claim.Proposal);
        try { await lifecycle.ApplyAsync(claim.Proposal!.Kind, claim.Proposal.ProposedDescriptor, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            await proposals.CompleteApplicationAsync(proposalId, false, "Application was cancelled before completion.", timeProvider.GetUtcNow(), CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to apply approved agent proposal {ProposalId}.", proposalId);
            if (ex is not AgentLifecycleException { Kind: AgentLifecycleFailureKind.ReconciliationRequired })
            {
                await proposals.CompleteApplicationAsync(
                    proposalId, false, ex.Message, timeProvider.GetUtcNow(), CancellationToken.None).ConfigureAwait(false);
            }
            // A compensation failure deliberately leaves Applying as an ambiguity fence. The
            // administrator must reconcile external state before a retry can become eligible.
            return new(AgentProposalLifecycleOutcome.ApplicationFailed,
                await proposals.GetAsync(proposalId, CancellationToken.None).ConfigureAwait(false), ex.Message);
        }
        await proposals.CompleteApplicationAsync(proposalId, true, null, timeProvider.GetUtcNow(), CancellationToken.None).ConfigureAwait(false);
        return new(AgentProposalLifecycleOutcome.Applied, await proposals.GetAsync(proposalId, cancellationToken).ConfigureAwait(false));
    }
}
