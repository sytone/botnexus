using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using BotNexus.Domain.Primitives;
using Microsoft.Extensions.Logging;

namespace BotNexus.Gateway.Isolation;

/// <summary>
/// Lightweight HTTP listener that provides a callback endpoint for sandboxed agents
/// to reach the gateway for provider (LLM) calls. The sandbox is network-isolated
/// but can reach the host via Docker's <c>host.docker.internal</c> DNS name.
/// </summary>
/// <remarks>
/// <para>
/// The callback server binds to an ephemeral port on localhost. Sandboxed agents
/// are given the callback URL as an environment variable (<c>GATEWAY_CALLBACK_URL</c>)
/// so they can make HTTP requests back to the gateway for LLM completions.
/// </para>
/// <para>
/// Each registered agent gets a unique path prefix (<c>/callback/{agentId}</c>)
/// to prevent cross-sandbox interference.
/// </para>
/// </remarks>
public sealed class GatewayCallbackServer : IAsyncDisposable
{
    private readonly ILogger<GatewayCallbackServer> _logger;
    private readonly ConcurrentDictionary<AgentId, string> _registeredAgents = new();
    private TcpListener? _listener;
    private int _port;
    private volatile bool _isListening;

    public GatewayCallbackServer(ILogger<GatewayCallbackServer> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>The port the callback server is listening on. 0 if not started.</summary>
    public int Port => _port;

    /// <summary>Whether the server is currently listening for connections.</summary>
    public bool IsListening => _isListening;

    /// <summary>
    /// Starts the callback server on an available ephemeral port.
    /// </summary>
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        _port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _isListening = true;

        _logger.LogInformation(
            "Gateway callback server started on port {Port}", _port);

        return Task.CompletedTask;
    }

    /// <summary>
    /// Stops the callback server.
    /// </summary>
    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        _isListening = false;
        _listener?.Stop();
        _listener = null;
        _port = 0;

        _logger.LogInformation("Gateway callback server stopped");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Gets the callback URL that sandboxed agents should use to reach the gateway.
    /// Uses <c>host.docker.internal</c> which Docker maps to the host's localhost.
    /// </summary>
    /// <returns>The full callback base URL (e.g., <c>http://host.docker.internal:12345</c>).</returns>
    /// <exception cref="InvalidOperationException">Thrown when the server is not started.</exception>
    public string GetCallbackUrl()
    {
        if (!_isListening || _port == 0)
            throw new InvalidOperationException(
                "Gateway callback server is not started. Call StartAsync first.");

        return $"http://host.docker.internal:{_port}";
    }

    /// <summary>
    /// Registers an agent as allowed to use the callback server.
    /// The agent gets a unique path prefix for its provider calls.
    /// </summary>
    /// <param name="agentId">The agent ID to register.</param>
    /// <param name="sandboxName">The sandbox name for this agent.</param>
    public void RegisterAgent(AgentId agentId, string sandboxName)
    {
        _registeredAgents[agentId] = sandboxName;
        _logger.LogDebug(
            "Registered agent '{AgentId}' (sandbox: {SandboxName}) for gateway callback",
            agentId, sandboxName);
    }

    /// <summary>
    /// Unregisters an agent from the callback server.
    /// </summary>
    /// <param name="agentId">The agent ID to unregister.</param>
    public void UnregisterAgent(AgentId agentId)
    {
        _registeredAgents.TryRemove(agentId, out _);
        _logger.LogDebug("Unregistered agent '{AgentId}' from gateway callback", agentId);
    }

    /// <summary>
    /// Checks whether an agent is currently registered for callback access.
    /// </summary>
    public bool HasRegisteredAgent(AgentId agentId)
        => _registeredAgents.ContainsKey(agentId);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_isListening)
            await StopAsync(CancellationToken.None);

        _registeredAgents.Clear();
    }
}
