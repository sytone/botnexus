using System.Net.Http.Json;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Contracts.Webhooks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotNexus.Extensions.TaskNexus;

/// <summary>
/// Delivers BotNexus agent webhook bindings to a TaskNexus instance.
/// </summary>
/// <remarks>
/// <para>
/// #3523: this replaces the hand-run setup script that created a BotNexus registration and then
/// POSTed the secret, inbound URL and display name into TaskNexus. The script never re-ran on
/// create, rename or delete, so the two systems drifted silently.
/// </para>
/// <para>
/// This lives under <c>src/extensions/</c> on purpose. The generic
/// <see cref="IAgentWebhookTargetNotifier"/> contract is core; the knowledge that a product called
/// TaskNexus exists is not. <c>BotNexus.Gateway.Webhooks</c> has no reference to this assembly.
/// </para>
/// <para>
/// <b>Inert when unconfigured.</b> With no base URL configured, every method returns without
/// touching the <see cref="HttpClient"/>. A gateway with no TaskNexus deployment starts clean and
/// makes zero outbound attempts.
/// </para>
/// <para>
/// <b>No retry, no outbox.</b> A failed push is logged and dropped. The provisioner's startup
/// reconciliation pass re-sends every binding, and that is deliberately the only recovery path.
/// </para>
/// </remarks>
public sealed class TaskNexusWebhookTargetNotifier : IAgentWebhookTargetNotifier
{
    /// <summary>Configuration key holding the TaskNexus base URL. Absent means disabled.</summary>
    public const string BaseUrlKey = "extensions:tasknexus:baseUrl";

    /// <summary>Configuration key holding the gateway origin TaskNexus should call back to.</summary>
    public const string CallbackOriginKey = "extensions:tasknexus:callbackOrigin";

    private readonly HttpClient _httpClient;
    private readonly string? _baseUrl;
    private readonly string? _callbackOrigin;
    private readonly ILogger<TaskNexusWebhookTargetNotifier> _logger;

    public TaskNexusWebhookTargetNotifier(
        HttpClient httpClient,
        IConfiguration? configuration = null,
        ILogger<TaskNexusWebhookTargetNotifier>? logger = null)
    {
        _httpClient = httpClient;
        _baseUrl = Normalize(configuration?[BaseUrlKey]);
        _callbackOrigin = Normalize(configuration?[CallbackOriginKey]);
        _logger = logger ?? NullLogger<TaskNexusWebhookTargetNotifier>.Instance;
    }

    /// <summary>Whether a target URL is configured. When false this notifier is a no-op.</summary>
    public bool IsConfigured => _baseUrl is not null;

    /// <inheritdoc/>
    public async Task NotifyAsync(AgentWebhookBinding binding, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(binding);
        if (_baseUrl is null)
            return;

        var payload = new
        {
            agentId = binding.AgentId.Value,
            displayName = binding.DisplayName,
            webhookId = binding.WebhookId,
            url = _callbackOrigin is null ? binding.InboundPath : _callbackOrigin + binding.InboundPath,
            secret = binding.Secret
        };

        try
        {
            using var response = await _httpClient
                .PostAsJsonAsync($"{_baseUrl}/api/botnexus/agents", payload, cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "TaskNexus rejected the webhook binding for agent '{AgentId}' with status {StatusCode}.",
                    binding.AgentId, (int)response.StatusCode);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning(ex,
                "Failed to deliver the webhook binding for agent '{AgentId}' to TaskNexus.",
                binding.AgentId);
        }
    }

    /// <inheritdoc/>
    public async Task NotifyRemovedAsync(AgentId agentId, CancellationToken cancellationToken)
    {
        if (_baseUrl is null)
            return;

        try
        {
            using var response = await _httpClient
                .DeleteAsync($"{_baseUrl}/api/botnexus/agents/{agentId.Value}", cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "TaskNexus rejected the removal of agent '{AgentId}' with status {StatusCode}.",
                    agentId, (int)response.StatusCode);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning(ex, "Failed to notify TaskNexus that agent '{AgentId}' was removed.", agentId);
        }
    }

    private static string? Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.TrimEnd('/');
}
