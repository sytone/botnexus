using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Gateway.Abstractions.Sessions;

/// <summary>
/// Persistence interface for gateway sessions. Implementations control where
/// and how session data (conversation history, metadata) is stored.
/// </summary>
/// <remarks>
/// <para>Built-in implementations:</para>
/// <list type="bullet">
///   <item><b>InMemorySessionStore</b> — Fast, non-durable. For development and testing.</item>
///   <item><b>FileSessionStore</b> — JSONL file-backed. For single-instance deployments.
///   Inspired by the archive's <c>SessionManager</c> (JSONL with .meta.json sidecar).</item>
/// </list>
/// <para>
/// Future implementations could use SQLite, Redis, PostgreSQL, etc.
/// All implementations must be thread-safe.
/// </para>
/// </remarks>
public interface ISessionStore
{
    /// <summary>
    /// Gets a session by ID, or <c>null</c> if it doesn't exist.
    /// </summary>
    Task<GatewaySession?> GetAsync(string sessionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets an existing session or creates a new one bound to the specified agent.
    /// </summary>
    /// <param name="sessionId">The session ID.</param>
    /// <param name="agentId">The agent to bind to if creating a new session.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<GatewaySession> GetOrCreateAsync(string sessionId, string agentId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Persists the session state. Creates or updates as needed.
    /// </summary>
    Task SaveAsync(GatewaySession session, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a session and its history.
    /// </summary>
    Task DeleteAsync(string sessionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists sessions, optionally filtered by agent ID.
    /// </summary>
    /// <param name="agentId">If set, only returns sessions for this agent.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<IReadOnlyList<GatewaySession>> ListAsync(string? agentId = null, CancellationToken cancellationToken = default);
}
