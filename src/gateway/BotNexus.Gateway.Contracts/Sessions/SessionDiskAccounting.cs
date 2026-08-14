using System.Text.Json;
using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Gateway.Abstractions.Sessions;

/// <summary>
/// Computes the approximate on-disk footprint of a session for the session-directory disk
/// budget (issue #2848).
/// </summary>
/// <remarks>
/// This exists so every store reports the SAME measure. If one store measured UTF-8 transcript
/// bytes and another measured a filesystem <c>Length</c>, the budget would mean something
/// different per deployment and an eviction ordering derived from it would not be comparable.
/// The measure is deliberately approximate and cheap: the budget needs a monotonic ranking and a
/// pressure signal, not an exact byte count.
/// </remarks>
public static class SessionDiskAccounting
{
    /// <summary>Fixed per-entry overhead approximating JSONL framing and column padding.</summary>
    private const int EntryOverheadBytes = 64;

    /// <summary>
    /// Returns the approximate bytes attributable to <paramref name="session"/>. Never negative.
    /// </summary>
    public static long Measure(GatewaySession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        long bytes = 0;
        foreach (var entry in session.History)
        {
            bytes += EntryOverheadBytes;
            bytes += Utf8Length(entry.Content);
            bytes += Utf8Length(entry.ToolName);
            bytes += Utf8Length(entry.ToolCallId);
        }

        if (session.Metadata.Count > 0)
        {
            try
            {
                bytes += Utf8Length(JsonSerializer.Serialize(session.Metadata));
            }
            catch (NotSupportedException)
            {
                // Unserialisable metadata must not break the sweep; it just goes uncounted.
            }
            catch (JsonException)
            {
            }
        }

        return bytes < 0 ? 0 : bytes;
    }

    /// <summary>Projects a session into its disk-usage row.</summary>
    public static SessionDiskUsage ToUsage(GatewaySession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return new SessionDiskUsage(
            session.SessionId.Value,
            session.AgentId.Value,
            session.Status,
            session.UpdatedAt,
            Measure(session));
    }

    private static long Utf8Length(string? value) =>
        string.IsNullOrEmpty(value) ? 0 : System.Text.Encoding.UTF8.GetByteCount(value);
}
