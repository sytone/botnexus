using System.Globalization;
using System.Text.Json;
using BotNexus.Agent.Core.Tools;
using BotNexus.Agent.Core.Types;
using BotNexus.Domain.AgentExchange;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Configuration;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Gateway.Abstractions.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Diagnostics;

namespace BotNexus.Gateway.Tools;

public sealed class AgentConverseTool(
    IAgentExchangeService conversationService,
    ISessionStore sessionStore,
    AgentId initiatorAgentId,
    SessionId sessionId,
    AgentExchangeOptions? exchangeOptions = null,
    IAgentRegistry? agentRegistry = null,
    IAgentSupervisor? agentSupervisor = null,
    ILogger? logger = null) : IAgentTool
{
    private const int DefaultTimeoutSeconds = 600;
    private const int MaxTimeoutSeconds = 1800;

    private readonly AgentExchangeOptions _exchangeOptions = exchangeOptions ?? new AgentExchangeOptions();
    private readonly ILogger _logger = logger ?? NullLogger.Instance;

    public string Name => "agent_converse";
    public string Label => "Agent Converse";

    /// <summary>
    /// Reserves enough executor time for substantive peer work while individual calls may request a shorter bounded budget.
    /// </summary>
    public TimeSpan? DefaultTimeout => TimeSpan.FromSeconds(DefaultTimeoutSeconds);

    /// <summary>
    /// The per-call <c>timeoutSeconds</c> argument is seconds. Declared explicitly because the
    /// executor no longer infers a unit from the argument name (issue #2955).
    /// </summary>
    public ToolTimeoutArgument? TimeoutArgument => new("timeoutSeconds", ToolTimeoutUnit.Seconds);

    public Tool Definition => new(
        Name,
        "Start a conversation with another registered agent. Not every agent is reachable: converse is governed by policy. Call list_agents first and only target an agent whose 'canConverse' is true -- targeting an agent with canConverse=false is a deterministic policy denial that wastes the turn and will never succeed on retry.",
        JsonDocument.Parse($$"""
            {
              "type": "object",
              "properties": {
                "agentId": { "type": "string", "description": "The target agent's ID, or its display name when exactly one registered agent has that name (case-insensitive). An exact ID always wins over a display-name match, and a display name shared by two or more agents is rejected as ambiguous rather than guessed. Must be an agent whose 'canConverse' is true in list_agents output; otherwise the call is denied by policy." },
                "message": { "type": "string", "description": "Opening message to send." },
                "objective": { "type": "string", "description": "What you want to achieve." },
                "timeoutSeconds": {
                  "type": "integer",
                  "minimum": 1,
                  "maximum": {{MaxTimeoutSeconds}},
                  "default": {{DefaultTimeoutSeconds}},
                  "description": "Wall-clock budget in seconds for this exchange (default 10 minutes, maximum 30 minutes). The 30-minute hard maximum prevents abandoned peer exchanges from consuming executor capacity indefinitely."
                },
                "maxTurns": {
                  "type": "integer",
                  "minimum": 1,
                  "maximum": {{_exchangeOptions.EffectiveMaxTurnsCeiling}},
                  "default": 1,
                  "description": "Maximum number of turns."
                }
              },
              "required": ["agentId", "message"]
            }
            """).RootElement.Clone());

    public Task<IReadOnlyDictionary<string, object?>> PrepareArgumentsAsync(
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(ReadString(arguments, "agentId")))
            throw new ArgumentException("Missing required argument: agentId.");
        if (string.IsNullOrWhiteSpace(ReadString(arguments, "message")))
            throw new ArgumentException("Missing required argument: message.");

        var prepared = new Dictionary<string, object?>(arguments, StringComparer.OrdinalIgnoreCase);
        var timeoutSeconds = ReadTimeoutSeconds(arguments);
        prepared["timeoutSeconds"] = timeoutSeconds;
        // ToolExecutor recognises `timeout` as seconds. Keeping the public schema name explicit avoids
        // colliding with tools whose timeout unit is implicit while still enforcing this call budget.
        prepared["timeout"] = timeoutSeconds;
        return Task.FromResult<IReadOnlyDictionary<string, object?>>(prepared);
    }

    public async Task<AgentToolResult> ExecuteAsync(
        string toolCallId,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default,
        AgentToolUpdateCallback? onUpdate = null)
    {
        var targetAgentId = ReadString(arguments, "agentId")
            ?? throw new ArgumentException("Missing required argument: agentId.");
        var message = ReadString(arguments, "message")
            ?? throw new ArgumentException("Missing required argument: message.");

        var timeoutSeconds = ReadTimeoutSeconds(arguments);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        var startedAt = Stopwatch.GetTimestamp();

        try
        {
            var result = await conversationService.ConverseAsync(
                new AgentExchangeRequest
                {
                    InitiatorId = initiatorAgentId,
                    TargetId = AgentId.From(targetAgentId),
                    Message = message,
                    Objective = ReadString(arguments, "objective"),
                    MaxTurns = Math.Clamp(ReadInt(arguments, "maxTurns", 1), 1, _exchangeOptions.EffectiveMaxTurnsCeiling),
                    CallChain = await ResolveCallChainAsync(timeoutCts.Token).ConfigureAwait(false),
                    // #3176: hand the exchange the delegating thread's address so handoff milestones
                    // can be reported back into it. Resolution failures are non-fatal - progress is
                    // observability, and losing it must never cost the caller the exchange itself.
                    InitiatorSessionId = sessionId,
                    InitiatorConversationId = await ResolveInitiatorConversationIdAsync(timeoutCts.Token).ConfigureAwait(false)
                },
                timeoutCts.Token).ConfigureAwait(false);

            return new AgentToolResult(
                [
                    new AgentToolContent(AgentToolContentType.Text, JsonSerializer.Serialize(result, JsonOptions))
                ]);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // #3577: a cancellation that the CALLER's ambient token did not cause must never reach
            // ToolExecutor's generic `catch (Exception ex) => ex.Message` handler, because the .NET
            // default text there is the bare "A task was canceled." - the exact opaque result this
            // issue was filed for. Everything the caller needs to choose between retrying, waiting
            // and giving up is knowable here and nowhere further up the stack: the budget, the
            // elapsed time against it, and which token actually fired.
            var elapsed = Stopwatch.GetElapsedTime(startedAt);
            var report = timeoutCts.IsCancellationRequested
                ? BuildTimeoutReport(targetAgentId, timeoutSeconds, elapsed)
                : BuildTargetUnavailableReport(targetAgentId, timeoutSeconds, elapsed);

            LogCancellation(toolCallId, targetAgentId, report);
            return new AgentToolResult(
                [
                    new AgentToolContent(AgentToolContentType.Text, JsonSerializer.Serialize(report, JsonOptions))
                ]);
        }
    }

    /// <summary>
    /// Builds the #3577 AC3 report for the caller's own wall-clock budget being exhausted. This is
    /// the one cancellation cause the caller can fix directly, so it is worded distinctly from every
    /// other cause and always advises a retry with a larger budget.
    /// </summary>
    private static AgentConverseCancellationReport BuildTimeoutReport(
        string targetAgentId,
        int timeoutSeconds,
        TimeSpan elapsed)
        => new()
        {
            CancellationCause = "timeout",
            CancelledBy = "caller",
            TargetAgentId = targetAgentId,
            TargetState = "unknown",
            TimeoutSeconds = timeoutSeconds,
            ElapsedSeconds = Math.Round(elapsed.TotalSeconds, 3),
            RetryAdvised = true,
            Message =
                $"The exchange with agent '{targetAgentId}' timed out: the caller's {timeoutSeconds}s " +
                $"timeoutSeconds budget was exhausted after {elapsed.TotalSeconds:0.###}s and this side " +
                "cancelled the call. The target may still be working. Retry with a larger timeoutSeconds " +
                "if the work genuinely needs longer."
        };

    /// <summary>
    /// Builds the #3577 AC4 report for a cancellation the caller's budget did NOT cause, naming the
    /// target's observable state. The distinction matters operationally: <c>unregistered</c> is
    /// deterministic and will never succeed on retry, whereas <c>busy</c> is transient and will.
    /// State is resolved best-effort - an absent registry or supervisor yields <c>unknown</c>, which
    /// is still strictly more actionable than the bare cancellation text it replaces.
    /// </summary>
    private AgentConverseCancellationReport BuildTargetUnavailableReport(
        string targetAgentId,
        int timeoutSeconds,
        TimeSpan elapsed)
    {
        var targetState = ResolveTargetState(targetAgentId);
        var (retryAdvised, explanation) = targetState switch
        {
            "unregistered" => (false,
                $"agent '{targetAgentId}' is unregistered, so this call is a deterministic failure and " +
                "will not succeed on retry"),
            "busy" => (true,
                $"agent '{targetAgentId}' is busy processing another turn and could not accept the " +
                "exchange; retrying once it is idle is likely to succeed"),
            "idle" => (true,
                $"agent '{targetAgentId}' is registered and idle, so the exchange was cancelled by the " +
                "target side or the runtime rather than by this call's budget; a retry may succeed"),
            _ => (true,
                $"agent '{targetAgentId}' is unreachable and its state could not be established; the " +
                "cancellation did not come from this call's timeoutSeconds budget")
        };

        return new AgentConverseCancellationReport
        {
            CancellationCause = "targetUnavailable",
            CancelledBy = "target",
            TargetAgentId = targetAgentId,
            TargetState = targetState,
            TimeoutSeconds = timeoutSeconds,
            ElapsedSeconds = Math.Round(elapsed.TotalSeconds, 3),
            RetryAdvised = retryAdvised,
            Message =
                $"The exchange with agent '{targetAgentId}' was cancelled after " +
                $"{elapsed.TotalSeconds:0.###}s of its {timeoutSeconds}s budget, which was NOT exhausted: " +
                explanation + "."
        };
    }

    /// <summary>
    /// Resolves the target's observable state for #3577 AC4. Registration is authoritative and is
    /// checked first, because an unregistered target is the one non-retryable case. Beyond that the
    /// supervisor's live instances distinguish a busy peer from an idle one.
    /// </summary>
    /// <remarks>
    /// Returns <c>unknown</c> rather than guessing when no registry or supervisor is wired: a
    /// fabricated state would be indistinguishable from a measured one at the call site, and this
    /// report exists precisely to stop callers acting on information that was never established.
    /// </remarks>
    private string ResolveTargetState(string targetAgentId)
    {
        AgentId target;
        try
        {
            target = AgentId.From(targetAgentId);
        }
        catch (ArgumentException)
        {
            return "unregistered";
        }

        if (agentRegistry is not null && !agentRegistry.Contains(target))
            return "unregistered";

        if (agentSupervisor is null)
            return agentRegistry is null ? "unknown" : "unreachable";

        var instances = agentSupervisor.GetAllInstances()
            .Where(instance => instance.AgentId == target)
            .ToArray();

        if (instances.Length == 0)
            return "unreachable";

        return instances.Any(instance => instance.Status is AgentInstanceStatus.Running or AgentInstanceStatus.Starting)
            ? "busy"
            : "idle";
    }

    /// <summary>
    /// Emits the #3577 AC5 correlation record. One occurrence must be enough to diagnose the
    /// trigger, so the caller session id, the target agent id and the tool call id that ties this
    /// line to its transcript row all appear in a single entry.
    /// </summary>
    private void LogCancellation(string toolCallId, string targetAgentId, AgentConverseCancellationReport report)
        => _logger.LogWarning(
            "agent_converse cancelled: cause={CancellationCause} cancelledBy={CancelledBy} " +
            "targetState={TargetState} elapsed={ElapsedSeconds}s of budget={TimeoutSeconds}s " +
            "callerAgentId={CallerAgentId} callerSessionId={CallerSessionId} " +
            "targetAgentId={TargetAgentId} toolCallId={ToolCallId}",
            report.CancellationCause,
            report.CancelledBy,
            report.TargetState,
            report.ElapsedSeconds,
            report.TimeoutSeconds,
            initiatorAgentId.Value,
            sessionId.Value,
            targetAgentId,
            toolCallId);

    /// <summary>
    /// Reads the conversation the calling session is pinned to, for progress delivery (#3176).
    /// </summary>
    /// <remarks>
    /// Returns <c>null</c> rather than throwing when the session is missing or unpinned: the only
    /// consequence is that a stale-binding self-heal cannot name the conversation, and that is not
    /// worth failing a handoff over.
    /// </remarks>
    private async Task<ConversationId?> ResolveInitiatorConversationIdAsync(CancellationToken cancellationToken)
    {
        try
        {
            var currentSession = await sessionStore.GetAsync(sessionId, cancellationToken).ConfigureAwait(false);
            // Read through the GatewaySession proxy, not the inner record (F-9 / Phase 7 fence).
            return currentSession is { } s && s.ConversationId.IsInitialized() ? s.ConversationId : null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private async Task<IReadOnlyList<AgentId>> ResolveCallChainAsync(CancellationToken cancellationToken)
    {
        var currentSession = await sessionStore.GetAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (currentSession is null || !currentSession.Metadata.TryGetValue("callChain", out var raw) || raw is null)
            return [initiatorAgentId];

        var parsed = raw switch
        {
            JsonElement { ValueKind: JsonValueKind.Array } element =>
                element.EnumerateArray()
                    .Where(item => item.ValueKind == JsonValueKind.String)
                    .Select(item => item.GetString())
                    .Where(item => !string.IsNullOrWhiteSpace(item))
                    .Select(item => AgentId.From(item!))
                    .ToArray(),
            IEnumerable<string> values =>
                values.Where(item => !string.IsNullOrWhiteSpace(item))
                    .Select(AgentId.From)
                    .ToArray(),
            _ => []
        };

        return parsed.Length == 0 ? [initiatorAgentId] : parsed;
    }

    private static string? ReadString(IReadOnlyDictionary<string, object?> args, string key)
    {
        if (!args.TryGetValue(key, out var value) || value is null)
            return null;

        return value switch
        {
            JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
            JsonElement element => element.ToString(),
            _ => value.ToString()
        };
    }

    private static int ReadTimeoutSeconds(IReadOnlyDictionary<string, object?> arguments)
    {
        if (!arguments.TryGetValue("timeoutSeconds", out var rawTimeout) || rawTimeout is null)
            return DefaultTimeoutSeconds;

        if (!TryReadInt32(rawTimeout, out var parsed))
            throw new ArgumentException("timeoutSeconds must be an integer.", nameof(arguments));

        if (parsed < 1)
            throw new ArgumentOutOfRangeException(nameof(arguments), "timeoutSeconds must be at least 1 second.");

        return Math.Min(parsed, MaxTimeoutSeconds);
    }

    private static int ReadInt(IReadOnlyDictionary<string, object?> args, string key, int defaultValue)
        => args.TryGetValue(key, out var value) && value is not null && TryReadInt32(value, out var parsed)
            ? parsed
            : defaultValue;

    /// <summary>
    /// Reads a losslessly-safe <see cref="int"/> from a tool argument value regardless of how the
    /// provider boxed the underlying JSON number. Streaming tool-call parsing boxes JSON integers as
    /// CLR <see cref="long"/> and non-integers as <see cref="double"/>, so a switch that only handled
    /// <see cref="JsonElement"/>/<see cref="int"/>/<see cref="string"/> rejected a valid boxed
    /// <c>timeoutSeconds</c> (issue #2415). A value is accepted only when it round-trips to
    /// <see cref="int"/> without loss.
    /// </summary>
    private static bool TryReadInt32(object value, out int result)
    {
        switch (value)
        {
            case int i:
                result = i;
                return true;
            case long l when l is >= int.MinValue and <= int.MaxValue:
                result = (int)l;
                return true;
            case double d when IsIntegralInt32(d):
                result = (int)d;
                return true;
            case JsonElement { ValueKind: JsonValueKind.Number } element:
                return TryReadJsonNumber(element, out result);
            case JsonElement { ValueKind: JsonValueKind.String } element:
                return TryParseInt32(element.GetString(), out result);
            case string text:
                return TryParseInt32(text, out result);
            default:
                result = 0;
                return false;
        }
    }

    private static bool TryReadJsonNumber(JsonElement element, out int result)
    {
        if (element.TryGetInt32(out result))
            return true;

        if (element.TryGetInt64(out var l) && l is >= int.MinValue and <= int.MaxValue)
        {
            result = (int)l;
            return true;
        }

        if (element.TryGetDouble(out var d) && IsIntegralInt32(d))
        {
            result = (int)d;
            return true;
        }

        result = 0;
        return false;
    }

    private static bool IsIntegralInt32(double value)
        => double.IsFinite(value)
           && value % 1d == 0d
           && value is >= int.MinValue and <= int.MaxValue;

    private static bool TryParseInt32(string? text, out int result)
        => int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out result);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
}

/// <summary>
/// The structured result an <c>agent_converse</c> cancellation returns instead of the bare .NET
/// <c>A task was canceled.</c> text (issue #3577).
/// </summary>
/// <remarks>
/// Every field exists to answer one question the old message could not: which side gave up, how much
/// of the budget was actually used, what state the peer was in, and whether retrying is worth the
/// turn. <see cref="Cancelled"/> is always <c>true</c> and is deliberately redundant so a consumer
/// can discriminate this payload from a successful <c>AgentExchangeResult</c> on a single field
/// without parsing prose.
/// </remarks>
public sealed record AgentConverseCancellationReport
{
    /// <summary>Always <c>true</c>; the discriminator against a successful exchange result.</summary>
    public bool Cancelled { get; init; } = true;

    /// <summary><c>timeout</c> when the caller's budget was exhausted; otherwise <c>targetUnavailable</c>.</summary>
    public required string CancellationCause { get; init; }

    /// <summary>Which side cancelled: <c>caller</c> or <c>target</c>.</summary>
    public required string CancelledBy { get; init; }

    /// <summary>The agent id this exchange was addressed to.</summary>
    public required string TargetAgentId { get; init; }

    /// <summary><c>idle</c>, <c>busy</c>, <c>unreachable</c>, <c>unregistered</c>, or <c>unknown</c>.</summary>
    public required string TargetState { get; init; }

    /// <summary>The wall-clock budget this call was given, in seconds.</summary>
    public required int TimeoutSeconds { get; init; }

    /// <summary>How much of <see cref="TimeoutSeconds"/> was consumed before cancellation.</summary>
    public required double ElapsedSeconds { get; init; }

    /// <summary>Whether a retry has any prospect of succeeding.</summary>
    public required bool RetryAdvised { get; init; }

    /// <summary>Human-readable explanation naming the cause, the side and the budget.</summary>
    public required string Message { get; init; }
}
