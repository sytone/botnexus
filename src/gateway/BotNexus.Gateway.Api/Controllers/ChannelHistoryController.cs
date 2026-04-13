using System.Text.RegularExpressions;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Domain.Primitives;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace BotNexus.Gateway.Api.Controllers;

/// <summary>
/// REST API for loading channel history across multiple sessions.
/// </summary>
/// <summary>
/// Represents channel history controller.
/// </summary>
[ApiController]
[Route("api/channels/{channelType}/agents/{agentId}/history")]
public sealed class ChannelHistoryController : ControllerBase
{
    private static readonly IReadOnlyDictionary<string, object?> EmptyMetadata = new Dictionary<string, object?>();
    private readonly ISessionStore _sessions;

    /// <summary>
    /// Initializes a new instance of the <see cref="ChannelHistoryController"/> class.
    /// </summary>
    public ChannelHistoryController(ISessionStore sessions) => _sessions = sessions;

    /// <summary>
    /// Loads channel history with cursor-based pagination across sessions.
    /// </summary>
    /// <param name="channelType">The channel type.</param>
    /// <param name="agentId">The agent id.</param>
    /// <param name="cursor">The optional pagination cursor in <c>{sessionId}:{messageIndex}</c> format.</param>
    /// <param name="limit">The maximum number of messages to return.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The channel history page.</returns>
    [HttpGet]
    [ProducesResponseType(typeof(ChannelHistoryResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ChannelHistoryResponse>> GetHistory(
        string channelType,
        string agentId,
        [FromQuery] string? cursor = null,
        [FromQuery] int limit = 50,
        CancellationToken cancellationToken = default)
    {
        if (limit <= 0)
            return BadRequest(new { error = "limit must be greater than zero." });

        var boundedLimit = Math.Min(limit, 200);
        var sessions = (await _sessions.ListByChannelAsync(AgentId.From(agentId), ChannelKey.From(channelType), cancellationToken))
            .Where(session => session.History.Count > 0)
            .ToList();

        if (sessions.Count == 0)
            return Ok(new ChannelHistoryResponse([], null, false, []));

        if (!TryResolveCursor(cursor, sessions, out var startSessionIndex, out var startMessageIndex, out var error))
            return BadRequest(new { error });

        var slices = new List<HistorySlice>();
        var remaining = boundedLimit;
        var sessionIndex = startSessionIndex;
        var endExclusive = startMessageIndex;

        while (remaining > 0 && sessionIndex < sessions.Count)
        {
            var session = sessions[sessionIndex];
            if (endExclusive < 0 || endExclusive > session.History.Count)
                return BadRequest(new { error = $"Cursor message index is out of range for session '{session.SessionId}'." });

            if (endExclusive == 0)
            {
                sessionIndex++;
                if (sessionIndex < sessions.Count)
                    endExclusive = sessions[sessionIndex].History.Count;
                continue;
            }

            var take = Math.Min(remaining, endExclusive);
            var offset = endExclusive - take;
            var entries = session.GetHistorySnapshot(offset, take);
            if (entries.Count > 0)
            {
                slices.Add(new HistorySlice(sessionIndex, session, offset, entries));
                remaining -= entries.Count;
            }

            endExclusive = offset;
        }

        if (slices.Count == 0)
            return Ok(new ChannelHistoryResponse([], null, false, []));

        var orderedSlices = slices
            .OrderByDescending(slice => slice.SessionIndex)
            .ToList();

        var messages = new List<ChannelHistoryMessage>();
        var boundaries = new List<ChannelHistorySessionBoundary>();
        foreach (var slice in orderedSlices)
        {
            if (messages.Count > 0)
            {
                boundaries.Add(new ChannelHistorySessionBoundary(
                    messages.Count,
                    slice.Session.SessionId,
                    slice.Session.CreatedAt));
            }

            var messageIndex = slice.StartIndex;
            foreach (var entry in slice.Entries)
            {
                messages.Add(new ChannelHistoryMessage(
                    $"{slice.Session.SessionId}:{messageIndex}",
                    slice.Session.SessionId,
                    entry.Role,
                    StripControlTags(entry.Content),
                    entry.Timestamp,
                    EmptyMetadata,
                    entry.ToolName,
                    entry.ToolCallId));
                messageIndex++;
            }
        }

        var oldestSlice = slices[^1];
        var hasMore = oldestSlice.StartIndex > 0 || oldestSlice.SessionIndex < sessions.Count - 1;
        var nextCursor = hasMore ? $"{oldestSlice.Session.SessionId}:{oldestSlice.StartIndex}" : null;

        return Ok(new ChannelHistoryResponse(messages, nextCursor, hasMore, boundaries));
    }

    private static readonly Regex ControlTagPattern = new(
        @"\[\[\s*reply_to_current\s*\]\]|\[\[\s*reply_to:\s*\S+\s*\]\]",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static string StripControlTags(string content)
        => string.IsNullOrEmpty(content) ? content : ControlTagPattern.Replace(content, "").TrimStart();

    private static bool TryResolveCursor(
        string? cursor,
        IReadOnlyList<GatewaySession> sessions,
        out int sessionIndex,
        out int messageIndex,
        out string? error)
    {
        if (string.IsNullOrWhiteSpace(cursor))
        {
            sessionIndex = 0;
            messageIndex = sessions[0].History.Count;
            error = null;
            return true;
        }

        var separator = cursor.LastIndexOf(':');
        if (separator <= 0 || separator == cursor.Length - 1)
        {
            sessionIndex = 0;
            messageIndex = 0;
            error = "cursor must be in '{sessionId}:{messageIndex}' format.";
            return false;
        }

        var sessionId = cursor[..separator];
        if (!int.TryParse(cursor[(separator + 1)..], out messageIndex) || messageIndex < 0)
        {
            sessionIndex = 0;
            messageIndex = 0;
            error = "cursor message index must be a non-negative integer.";
            return false;
        }

        sessionIndex = sessions
            .Select((session, index) => new { session.SessionId, Index = index })
            .Where(candidate => string.Equals(candidate.SessionId, sessionId, StringComparison.Ordinal))
            .Select(candidate => candidate.Index)
            .DefaultIfEmpty(-1)
            .First();
        if (sessionIndex < 0)
        {
            error = $"cursor session '{sessionId}' was not found.";
            return false;
        }

        error = null;
        return true;
    }

    private sealed record HistorySlice(
        int SessionIndex,
        GatewaySession Session,
        int StartIndex,
        IReadOnlyList<SessionEntry> Entries);
}

/// <summary>
/// Channel history response payload.
/// </summary>
public sealed record ChannelHistoryResponse(
    IReadOnlyList<ChannelHistoryMessage> Messages,
    string? NextCursor,
    bool HasMore,
    IReadOnlyList<ChannelHistorySessionBoundary> SessionBoundaries);

/// <summary>
/// Channel history message payload.
/// </summary>
public sealed record ChannelHistoryMessage(
    string Id,
    string SessionId,
    MessageRole Role,
    string Content,
    DateTimeOffset Timestamp,
    IReadOnlyDictionary<string, object?> Metadata,
    string? ToolName = null,
    string? ToolCallId = null);

/// <summary>
/// Session boundary marker in a history page.
/// </summary>
public sealed record ChannelHistorySessionBoundary(
    int InsertBeforeIndex,
    string SessionId,
    DateTimeOffset StartedAt);
