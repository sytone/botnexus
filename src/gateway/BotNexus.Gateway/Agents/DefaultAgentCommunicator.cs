using System.Diagnostics;
using BotNexus.AgentCore.Diagnostics;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BotNexus.Gateway.Agents;

/// <summary>
/// Default implementation of <see cref="IAgentCommunicator"/> for local sub-agent and cross-agent calls.
/// </summary>
public sealed class DefaultAgentCommunicator : IAgentCommunicator
{
    private static readonly AsyncLocal<List<string>?> ActiveCallPath = new();
    private readonly IAgentRegistry _registry;
    private readonly IAgentSupervisor _supervisor;
    private readonly ILogger<DefaultAgentCommunicator> _logger;
    private readonly IOptions<GatewayOptions> _options;

    /// <summary>
    /// Initializes a new instance of the <see cref="DefaultAgentCommunicator"/> class.
    /// </summary>
    /// <param name="registry">Agent registry used to validate cross-agent targets.</param>
    /// <param name="supervisor">Agent supervisor used to get or create child agent handles.</param>
    /// <param name="logger">Logger instance.</param>
    public DefaultAgentCommunicator(
        IAgentRegistry registry,
        IAgentSupervisor supervisor,
        IOptions<GatewayOptions> options,
        ILogger<DefaultAgentCommunicator> logger)
    {
        _registry = registry;
        _supervisor = supervisor;
        _options = options;
        _logger = logger;
    }

    public DefaultAgentCommunicator(
        IAgentRegistry registry,
        IAgentSupervisor supervisor,
        ILogger<DefaultAgentCommunicator> logger)
        : this(registry, supervisor, Options.Create(new GatewayOptions()), logger)
    {
    }

    /// <summary>
    /// Calls a sub-agent in a session scoped to the parent session and returns the sub-agent response.
    /// </summary>
    /// <param name="parentAgentId">ID of the calling parent agent.</param>
    /// <param name="parentSessionId">Session ID of the parent agent run.</param>
    /// <param name="childAgentId">ID of the target sub-agent.</param>
    /// <param name="message">Prompt content to send to the sub-agent.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The sub-agent response.</returns>
    public async Task<AgentResponse> CallSubAgentAsync(
        string parentAgentId,
        string parentSessionId,
        string childAgentId,
        string message,
        CancellationToken cancellationToken = default)
    {
        using var callScope = EnterCallChain(parentAgentId, childAgentId, _options.Value.MaxCallChainDepth);
        using var activity = AgentDiagnostics.Source.StartActivity("agent.cross_call", ActivityKind.Internal);
        activity?.SetTag("botnexus.agent.id", parentAgentId);
        activity?.SetTag("botnexus.agent.target_id", childAgentId);
        activity?.SetTag("botnexus.agent.call_depth", Math.Max((ActiveCallPath.Value?.Count ?? 1) - 1, 0));

        var childSessionId = $"{parentSessionId}::sub::{childAgentId}";
        _logger.LogInformation(
            "Sub-agent call from '{ParentAgentId}' session '{ParentSessionId}' to '{ChildAgentId}' session '{ChildSessionId}'",
            parentAgentId,
            parentSessionId,
            childAgentId,
            childSessionId);

        var childHandle = await _supervisor.GetOrCreateAsync(childAgentId, childSessionId, cancellationToken);
        return await PromptWithTimeoutAsync(childHandle, message, parentAgentId, childAgentId, cancellationToken);
    }

    /// <summary>
    /// Calls another local agent from a source agent and returns the target agent response.
    /// </summary>
    /// <param name="sourceAgentId">ID of the calling source agent.</param>
    /// <param name="targetEndpoint">Optional remote endpoint. Only empty/local is supported.</param>
    /// <param name="targetAgentId">ID of the target agent to call.</param>
    /// <param name="message">Prompt content to send to the target agent.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The target agent response.</returns>
    /// <exception cref="NotSupportedException">Thrown when a remote endpoint is requested.</exception>
    /// <exception cref="KeyNotFoundException">Thrown when the target agent is not registered.</exception>
    /// <exception cref="InvalidOperationException">Thrown when recursive call cycles are detected.</exception>
    public async Task<AgentResponse> CallCrossAgentAsync(
        string sourceAgentId,
        string targetEndpoint,
        string targetAgentId,
        string message,
        CancellationToken cancellationToken = default)
    {
        using var callScope = EnterCallChain(sourceAgentId, targetAgentId, _options.Value.MaxCallChainDepth);
        using var activity = AgentDiagnostics.Source.StartActivity("agent.cross_call", ActivityKind.Internal);
        activity?.SetTag("botnexus.agent.id", sourceAgentId);
        activity?.SetTag("botnexus.agent.target_id", targetAgentId);
        activity?.SetTag("botnexus.agent.call_depth", Math.Max((ActiveCallPath.Value?.Count ?? 1) - 1, 0));

        if (!string.IsNullOrWhiteSpace(targetEndpoint))
        {
            throw new NotSupportedException(
                $"Remote cross-agent calls to '{targetEndpoint}' are not yet supported. " +
                "Use local cross-agent calls by leaving targetEndpoint empty.");
        }

        if (!_registry.Contains(targetAgentId))
            throw new KeyNotFoundException($"Agent '{targetAgentId}' is not registered.");

        var crossSessionId = $"{sourceAgentId}::cross::{targetAgentId}::{Guid.NewGuid():N}";
        _logger.LogInformation(
            "Cross-agent call from '{SourceAgentId}' to '{TargetAgentId}' session '{CrossSessionId}'",
            sourceAgentId,
            targetAgentId,
            crossSessionId);

        var handle = await _supervisor.GetOrCreateAsync(targetAgentId, crossSessionId, cancellationToken);
        return await PromptWithTimeoutAsync(handle, message, sourceAgentId, targetAgentId, cancellationToken);
    }

    private static IDisposable EnterCallChain(string sourceAgentId, string targetAgentId, int maxCallChainDepth)
    {
        if (maxCallChainDepth <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxCallChainDepth), "Max call chain depth must be greater than zero.");

        var path = ActiveCallPath.Value;
        if (path is null)
        {
            path = [];
            ActiveCallPath.Value = path;
        }

        var entryCount = path.Count;
        if (path.Count == 0 || !string.Equals(path[^1], sourceAgentId, StringComparison.OrdinalIgnoreCase))
            path.Add(sourceAgentId);

        if (path.Contains(targetAgentId, StringComparer.OrdinalIgnoreCase))
        {
            var chain = string.Join(" -> ", path.Concat([targetAgentId]));
            ResetPath(path, entryCount);

            throw new InvalidOperationException(
                $"Recursive cross-agent call detected while targeting '{targetAgentId}'. Active chain: {chain}");
        }

        path.Add(targetAgentId);
        var depth = path.Count - 1;
        if (depth > maxCallChainDepth)
        {
            var chain = string.Join(" -> ", path);
            ResetPath(path, entryCount);

            throw new InvalidOperationException(
                $"Cross-agent call chain depth {depth} exceeded maximum configured depth {maxCallChainDepth}. Active chain: {chain}");
        }

        return new CallChainScope(path, entryCount);
    }

    private static void ResetPath(List<string> path, int entryCount)
    {
        if (path.Count > entryCount)
            path.RemoveRange(entryCount, path.Count - entryCount);

        if (path.Count == 0)
            ActiveCallPath.Value = null;
    }

    private async Task<AgentResponse> PromptWithTimeoutAsync(
        IAgentHandle handle,
        string message,
        string sourceAgentId,
        string targetAgentId,
        CancellationToken cancellationToken)
    {
        var timeoutSeconds = _options.Value.CrossAgentTimeoutSeconds;
        if (timeoutSeconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(GatewayOptions.CrossAgentTimeoutSeconds), "Cross-agent timeout must be greater than zero seconds.");

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        try
        {
            return await handle.PromptAsync(message, timeoutCts.Token);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested && timeoutCts.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Cross-agent call timed out after {timeoutSeconds} seconds from '{sourceAgentId}' to '{targetAgentId}'.",
                ex);
        }
    }

    private sealed class CallChainScope(List<string> path, int entryCount) : IDisposable
    {
        public void Dispose()
        {
            ResetPath(path, entryCount);
        }
    }
}
