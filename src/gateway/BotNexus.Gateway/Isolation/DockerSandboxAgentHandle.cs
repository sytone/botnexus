using System.Runtime.CompilerServices;
using BotNexus.Agent.Core.Types;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using Microsoft.Extensions.Logging;
using AgentUserMessage = BotNexus.Agent.Core.Types.UserMessage;

namespace BotNexus.Gateway.Isolation;

/// <summary>
/// Agent handle for Docker sandbox isolation. Dispatches prompts to the sandbox process
/// and relays streaming events back through the gateway's standard event pipeline.
/// </summary>
/// <remarks>
/// This is a Phase 1 handle that validates the lifecycle state machine. Full IPC
/// (gRPC/HTTP communication with the sandboxed agent process) will be implemented in
/// the workspace synchronization (#1071) and tool routing (#1072) sub-issues.
/// </remarks>
internal sealed class DockerSandboxAgentHandle : IAgentHandle
{
    private readonly SandboxState _state;
    private readonly ILogger _logger;
    private volatile bool _isRunning;

    public DockerSandboxAgentHandle(
        AgentId agentId,
        SessionId sessionId,
        string sandboxName,
        SandboxState state,
        ILogger logger)
    {
        AgentId = agentId;
        SessionId = sessionId;
        SandboxName = sandboxName;
        _state = state;
        _logger = logger;
    }

    /// <inheritdoc />
    public AgentId AgentId { get; }

    /// <inheritdoc />
    public SessionId SessionId { get; }

    /// <summary>The name of the Docker sandbox this handle dispatches to.</summary>
    public string SandboxName { get; }

    /// <inheritdoc />
    public bool IsRunning => _isRunning;

    /// <inheritdoc />
    public Task<AgentResponse> PromptAsync(string message, CancellationToken cancellationToken = default)
    {
        _state.RecordActivity();
        _isRunning = true;
        try
        {
            // Phase 1: lifecycle validation only. Full IPC in follow-up issues.
            throw new NotSupportedException(
                $"Docker sandbox agent handle does not yet support prompt dispatch. " +
                $"Sandbox '{SandboxName}' is running (lifecycle validated). " +
                $"IPC implementation is tracked in follow-up issues #1071 and #1072.");
        }
        finally
        {
            _isRunning = false;
        }
    }

    /// <inheritdoc />
    public Task<AgentResponse> PromptAsync(AgentUserMessage message, CancellationToken cancellationToken = default)
        => PromptAsync(message.Content ?? string.Empty, cancellationToken);

    /// <inheritdoc />
    public IAsyncEnumerable<AgentStreamEvent> StreamAsync(string message, CancellationToken cancellationToken = default)
        => ThrowNotSupportedStream(cancellationToken);

    /// <inheritdoc />
    public IAsyncEnumerable<AgentStreamEvent> StreamAsync(AgentUserMessage message, CancellationToken cancellationToken = default)
        => ThrowNotSupportedStream(cancellationToken);

    /// <inheritdoc />
    public Task AbortAsync(CancellationToken cancellationToken = default)
    {
        _isRunning = false;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task SteerAsync(string message, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    /// <inheritdoc />
    public Task InterruptAndSteerAsync(string message, CancellationToken cancellationToken = default)
    {
        _isRunning = false;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task FollowUpAsync(string message, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    /// <inheritdoc />
    public Task FollowUpAsync(AgentMessage message, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        _isRunning = false;
        return ValueTask.CompletedTask;
    }

    private static async IAsyncEnumerable<AgentStreamEvent> ThrowNotSupportedStream(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Yield to satisfy compiler's async iterator requirement before throwing
        await Task.CompletedTask;
        if (cancellationToken.IsCancellationRequested)
            yield break;

        throw new NotSupportedException(
            "Docker sandbox agent handle does not yet support streaming. " +
            "IPC implementation is tracked in follow-up issues #1071 and #1072.");
    }
}
