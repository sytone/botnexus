using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using Microsoft.Extensions.Logging;

namespace BotNexus.Gateway.Agents;

/// <summary>
/// Default implementation of <see cref="IAgentCommunicator"/> for local sub-agent calls.
/// </summary>
public sealed class DefaultAgentCommunicator : IAgentCommunicator
{
    private static readonly AsyncLocal<HashSet<string>?> ActiveCallChain = new();
    private readonly IAgentSupervisor _supervisor;
    private readonly ILogger<DefaultAgentCommunicator> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="DefaultAgentCommunicator"/> class.
    /// </summary>
    /// <param name="supervisor">Agent supervisor used to get or create child agent handles.</param>
    /// <param name="logger">Logger instance.</param>
    public DefaultAgentCommunicator(
        IAgentSupervisor supervisor,
        ILogger<DefaultAgentCommunicator> logger)
    {
        _supervisor = supervisor;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<AgentResponse> CallSubAgentAsync(
        string parentAgentId,
        string parentSessionId,
        string childAgentId,
        string message,
        CancellationToken cancellationToken = default)
    {
        using var callScope = EnterCallChain(parentAgentId, childAgentId);
        var childSessionId = $"{parentSessionId}::sub::{childAgentId}";
        _logger.LogInformation(
            "Sub-agent call from '{ParentAgentId}' session '{ParentSessionId}' to '{ChildAgentId}' session '{ChildSessionId}'",
            parentAgentId,
            parentSessionId,
            childAgentId,
            childSessionId);

        var childHandle = await _supervisor.GetOrCreateAsync(childAgentId, childSessionId, cancellationToken);
        return await childHandle.PromptAsync(message, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<AgentResponse> CallCrossAgentAsync(
        string sourceAgentId,
        string targetEndpoint,
        string targetAgentId,
        string message,
        CancellationToken cancellationToken = default)
    {
        using var callScope = EnterCallChain(sourceAgentId, targetAgentId);
        if (!string.IsNullOrWhiteSpace(targetEndpoint))
        {
            throw new NotSupportedException(
                $"Remote cross-agent calls to '{targetEndpoint}' are not yet supported. " +
                "Use local cross-agent calls by leaving targetEndpoint empty.");
        }

        var crossSessionId = $"cross::{sourceAgentId}::{targetAgentId}::{Guid.NewGuid():N}";
        _logger.LogInformation(
            "Cross-agent call from '{SourceAgentId}' to '{TargetAgentId}' session '{CrossSessionId}'",
            sourceAgentId,
            targetAgentId,
            crossSessionId);

        var handle = await _supervisor.GetOrCreateAsync(targetAgentId, crossSessionId, cancellationToken);
        return await handle.PromptAsync(message, cancellationToken);
    }

    private static IDisposable EnterCallChain(string sourceAgentId, string targetAgentId)
    {
        var chain = ActiveCallChain.Value;
        var createdNewChain = false;
        if (chain is null)
        {
            chain = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            ActiveCallChain.Value = chain;
            createdNewChain = true;
        }

        var addedSource = chain.Add(sourceAgentId);
        if (!chain.Add(targetAgentId))
        {
            if (addedSource)
                chain.Remove(sourceAgentId);

            if (createdNewChain && chain.Count == 0)
                ActiveCallChain.Value = null;

            throw new InvalidOperationException(
                $"Recursive cross-agent call detected while targeting '{targetAgentId}'. Active chain: {string.Join(" -> ", chain)}");
        }

        return new CallChainScope(chain, sourceAgentId, targetAgentId, addedSource, createdNewChain);
    }

    private sealed class CallChainScope(
        HashSet<string> chain,
        string sourceAgentId,
        string targetAgentId,
        bool addedSource,
        bool createdNewChain) : IDisposable
    {
        public void Dispose()
        {
            chain.Remove(targetAgentId);
            if (addedSource)
                chain.Remove(sourceAgentId);

            if (createdNewChain && chain.Count == 0)
                ActiveCallChain.Value = null;
        }
    }
}
