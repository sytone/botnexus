using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Contracts.Webhooks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotNexus.Gateway.Webhooks;

/// <summary>
/// Reconciles per-agent outbound webhook registrations from agent lifecycle, and pushes each
/// binding to whatever delivery targets are configured.
/// </summary>
/// <remarks>
/// <para>
/// Startup behaviour mirrors <c>HeartbeatCronProvisioner</c> exactly: initialize the store, then
/// walk <see cref="IAgentRegistry.GetAll"/> calling <see cref="ProvisionAsync"/>. That startup
/// pass is also the recovery path - there is deliberately no retry, backoff or outbox for a
/// failed downstream push, because the next start reconciles everything anyway.
/// </para>
/// <para>
/// <b>Idempotency key.</b> Registrations this provisioner owns carry the deterministic label
/// <c>agent-webhook:&lt;agentId&gt;</c>. The key is the agent ID rather than the display name
/// because an agent ID is immutable in BotNexus - <c>AgentsController.Update</c> rejects a
/// route/payload mismatch with 400 and <c>IAgentRegistry.Update</c> throws on a differing
/// descriptor id - so "rename" means a display-name change only and the key is stable by
/// construction.
/// </para>
/// <para>
/// <b>Secret preservation.</b> When a labelled registration is found, the store is not written to
/// at all and the stored secret is re-sent. That is only possible in-process:
/// <c>SqliteWebhookRegistrationStore</c> persists the plaintext secret by design, while
/// <c>WebhooksController</c> deliberately returns <c>secret: null</c>. A REST-based
/// implementation would be forced to mint a new secret on every pass and would silently break
/// the binding the downstream system already holds.
/// </para>
/// </remarks>
public sealed class AgentWebhookProvisioner : IHostedService, IAgentWebhookProvisioner
{
    /// <summary>Prefix of the deterministic label identifying registrations this type owns.</summary>
    public const string LabelPrefix = "agent-webhook:";

    private readonly IAgentRegistry _registry;
    private readonly IWebhookRegistrationStore _store;
    private readonly IReadOnlyList<IAgentWebhookTargetNotifier> _targets;
    private readonly ILogger<AgentWebhookProvisioner> _logger;

    public AgentWebhookProvisioner(
        IAgentRegistry registry,
        IWebhookRegistrationStore store,
        IEnumerable<IAgentWebhookTargetNotifier>? targets = null,
        ILogger<AgentWebhookProvisioner>? logger = null)
    {
        _registry = registry;
        _store = store;
        _targets = targets?.ToArray() ?? [];
        _logger = logger ?? NullLogger<AgentWebhookProvisioner>.Instance;
    }

    /// <summary>Deterministic, agent-id-keyed label for a provisioner-owned registration.</summary>
    public static string LabelFor(AgentId agentId) => LabelPrefix + agentId.Value;

    /// <summary>Gateway-relative inbound path an external caller POSTs to.</summary>
    public static string InboundPathFor(AgentId agentId, WebhookId webhookId)
        => $"/api/webhooks/{agentId.Value}/{webhookId.Value}";

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _store.InitializeAsync(cancellationToken).ConfigureAwait(false);

        foreach (var descriptor in _registry.GetAll())
        {
            await ProvisionAsync(descriptor, cancellationToken).ConfigureAwait(false);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc/>
    public async Task ProvisionAsync(AgentDescriptor descriptor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        var label = LabelFor(descriptor.AgentId);
        var existing = (await _store.ListAsync(descriptor.AgentId, cancellationToken).ConfigureAwait(false))
            .FirstOrDefault(registration =>
                string.Equals(registration.Label, label, StringComparison.Ordinal));

        WebhookRegistration current;

        if (existing is not null)
        {
            // Found branch: no store write of any kind. Re-sending the STORED secret is what
            // makes a display-name change non-destructive to an already-established binding.
            current = existing;
        }
        else
        {
            current = await _store.CreateAsync(
                new WebhookRegistration
                {
                    Id = WebhookId.Create(),
                    Label = label,
                    AgentId = descriptor.AgentId,
                    Secret = WebhookSecretHelper.GenerateSecret(),
                    Enabled = true,
                    CreatedAt = DateTimeOffset.UtcNow
                },
                cancellationToken).ConfigureAwait(false);

            _logger.LogInformation(
                "Provisioned outbound webhook registration '{WebhookId}' for agent '{AgentId}'.",
                current.Id, descriptor.AgentId);
        }

        var binding = new AgentWebhookBinding(
            descriptor.AgentId,
            descriptor.DisplayName,
            current.Id.Value,
            InboundPathFor(descriptor.AgentId, current.Id),
            current.Secret);

        foreach (var target in _targets)
        {
            await target.NotifyAsync(binding, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc/>
    public async Task DeprovisionAsync(AgentId agentId, CancellationToken cancellationToken)
    {
        var label = LabelFor(agentId);
        var owned = (await _store.ListAsync(agentId, cancellationToken).ConfigureAwait(false))
            .Where(registration => string.Equals(registration.Label, label, StringComparison.Ordinal))
            .ToList();

        foreach (var registration in owned)
        {
            // Only labelled registrations are removed: a webhook an operator created by hand for
            // this agent is not ours to delete, exactly as the cron provisioners leave a
            // non-system job alone (#3524).
            await _store.DeleteAsync(registration.Id, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation(
                "Deprovisioned outbound webhook registration '{WebhookId}' for deleted agent '{AgentId}'.",
                registration.Id, agentId);

            // Notify per REMOVED REGISTRATION, not per agent. The webhook id is the generation
            // token that lets a downstream target delete conditionally (agent AND webhook id) and
            // no-op on mismatch. Keyed on agent id alone, a delayed or retried delete for a
            // since-recreated agent would erase the new agent's newer binding - agent ids are
            // immutable and therefore reusable, so they are not a safe delete key on their own.
            foreach (var target in _targets)
            {
                await target
                    .NotifyRemovedAsync(agentId, registration.Id.Value, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }
}
