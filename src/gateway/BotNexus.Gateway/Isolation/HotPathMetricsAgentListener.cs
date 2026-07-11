using System.Collections.Concurrent;
using System.Diagnostics;
using BotNexus.Agent.Core.Types;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Gateway.Telemetry;
using Microsoft.Extensions.Logging;

namespace BotNexus.Gateway.Isolation;

/// <summary>
/// Bridges the agent event stream to the platform hot-path metrics (<see cref="HotPathMetrics"/>,
/// PBI3 #1851). One instance is attached per <see cref="InProcessAgentHandle"/> and subscribes to
/// the underlying agent's <see cref="AgentEvent"/> stream, translating turn/tool lifecycle events
/// into <c>botnexus.turns.*</c>, <c>botnexus.tool.*</c>, and <c>botnexus.provider.*</c> measurements.
/// </summary>
/// <remarks>
/// <para>
/// The handle is the single choke point through which every agent execution flows (interactive
/// StreamAsync turns AND blocking PromptAsync runs from cron / soul / heartbeat / sub-agents), so
/// recording here captures the turn/tool/provider seams once regardless of entry path.
/// </para>
/// <para>
/// Recording is allocation-light and defensive: a failure to record a metric never disrupts the
/// agent turn. Tag values are bounded, low-cardinality dimensions (agent id, channel, provider,
/// model, tool name) — never free-text content or unbounded ids.
/// </para>
/// </remarks>
internal sealed class HotPathMetricsAgentListener : IDisposable
{
    private readonly HotPathMetrics _metrics;
    private readonly ILogger _logger;
    private readonly string _agent;
    private readonly string _channel;
    private readonly string _provider;
    private readonly string _model;
    private readonly IDisposable _subscription;

    // Turn timing: a run may interleave turns, but the agent loop emits TurnStart/TurnEnd in
    // strict nesting per run on a single logical thread, so a simple stopwatch-per-turn suffices.
    private long _turnStartTicks;

    // Tool timing keyed by ToolCallId. Parallel tool execution emits all starts before any end,
    // so a concurrent map keyed by call id is required to attribute durations correctly.
    private readonly ConcurrentDictionary<string, long> _toolStartTicks = new(StringComparer.Ordinal);

    public HotPathMetricsAgentListener(
        BotNexus.Agent.Core.Agent agent,
        HotPathMetrics metrics,
        string agentId,
        string channel,
        string provider,
        string model,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(agent);
        _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
        _logger = logger;
        _agent = agentId;
        _channel = string.IsNullOrWhiteSpace(channel) ? HotPathMetrics.Unknown : channel;
        _provider = string.IsNullOrWhiteSpace(provider) ? HotPathMetrics.Unknown : provider;
        _model = string.IsNullOrWhiteSpace(model) ? HotPathMetrics.Unknown : model;
        _subscription = agent.Subscribe(OnAgentEventAsync);
    }

    private Task OnAgentEventAsync(AgentEvent agentEvent, CancellationToken cancellationToken)
    {
        try
        {
            switch (agentEvent)
            {
                case TurnStartEvent:
                    _turnStartTicks = Stopwatch.GetTimestamp();
                    break;

                case TurnEndEvent turnEnd:
                    RecordTurn(turnEnd);
                    break;

                case ToolExecutionStartEvent toolStart:
                    _toolStartTicks[toolStart.ToolCallId] = Stopwatch.GetTimestamp();
                    break;

                case ToolExecutionEndEvent toolEnd:
                    RecordTool(toolEnd);
                    break;
            }
        }
        catch (Exception ex)
        {
            // Metrics must never disrupt a turn. Swallow and log at debug.
            _logger.LogDebug(ex, "Hot-path metrics recording failed for agent '{AgentId}'.", _agent);
        }

        return Task.CompletedTask;
    }

    private void RecordTurn(TurnEndEvent turnEnd)
    {
        var startTicks = Interlocked.Exchange(ref _turnStartTicks, 0);
        var durationMs = startTicks == 0
            ? 0.0
            : Stopwatch.GetElapsedTime(startTicks).TotalMilliseconds;

        var message = turnEnd.Message;
        var outcome = OutcomeFromFinishReason(message.FinishReason);

        _metrics.RecordTurn(_agent, _channel, outcome, durationMs);

        // The turn's assistant message is the product of exactly one provider (LLM) request, so
        // TurnEnd is the natural seam for provider request/duration/token metrics too. Provider
        // and model are fixed for the life of the handle (resolved at creation).
        var input = message.Usage?.InputTokens ?? 0;
        var output = message.Usage?.OutputTokens ?? 0;
        _metrics.RecordProviderRequest(
            _provider,
            _model,
            outcome,
            durationMs,
            input,
            output);
    }

    private void RecordTool(ToolExecutionEndEvent toolEnd)
    {
        var durationMs = _toolStartTicks.TryRemove(toolEnd.ToolCallId, out var startTicks)
            ? Stopwatch.GetElapsedTime(startTicks).TotalMilliseconds
            : 0.0;

        var outcome = toolEnd.IsError ? "error" : "success";
        _metrics.RecordToolCall(toolEnd.ToolName, outcome, durationMs);
    }

    private static string OutcomeFromFinishReason(StopReason reason) => reason switch
    {
        StopReason.Stop => "success",
        StopReason.ToolUse => "success",
        StopReason.Length => "success",
        StopReason.Aborted => "aborted",
        StopReason.Error => "error",
        StopReason.Refusal => "refusal",
        _ => "error",
    };

    public void Dispose() => _subscription.Dispose();
}
