using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Gateway.Abstractions.Sessions;

/// <summary>
/// Per-session disk accounting used by the session-directory disk budget (issue #2848).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Bytes"/> is an <em>accounting</em> figure, not a <c>stat()</c> of the session
/// directory: it is what the store can report cheaply about the payload it persists for the
/// session (transcript content plus metadata). The budget only needs a monotonic, comparable
/// measure to rank sessions and decide when pressure exists, and walking the filesystem on every
/// cleanup tick would cost far more than it buys.
/// </para>
/// </remarks>
/// <param name="SessionId">The session this row accounts for.</param>
/// <param name="AgentId">The owning agent; the budget is applied per agent, matching the on-disk layout.</param>
/// <param name="Status">The session's lifecycle status, which decides its eviction tier.</param>
/// <param name="UpdatedAt">Last activity, used for oldest-first ordering within a tier.</param>
/// <param name="Bytes">Approximate bytes attributable to this session. Never negative.</param>
public sealed record SessionDiskUsage(
    string SessionId,
    string AgentId,
    SessionStatus Status,
    DateTimeOffset UpdatedAt,
    long Bytes);
