using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace BotNexus.Gateway.Telemetry;

/// <summary>
/// Centralised recorder for the BotNexus platform hot-path metrics (PBI3, issue #1851).
/// Owns every instrument for the turn / tool / provider / cron / channel / session seams
/// and exposes small, allocation-light <c>Record*</c> methods that the hot paths call.
/// </summary>
/// <remarks>
/// <para>
/// Instruments are created once (at construction) over the canonical <see cref="BotNexusMeters"/>
/// scope via the injected <see cref="IMetrics"/> facade, so this type carries no OpenTelemetry
/// SDK dependency and can be referenced from <c>BotNexus.Gateway</c> and other hot-path
/// projects without dragging the OTel transitive graph (the NU1605 cause the earlier attempt
/// hit). The OTel SDK wiring stays isolated in the composition root.
/// </para>
/// <para>
/// <strong>Cardinality guard:</strong> all tag values are bounded, low-cardinality dimensions
/// (agent id, channel type, provider, model, tool name, cron job id, coarse outcome/status
/// strings). No free-text user content and no unbounded ids (session ids, message ids,
/// correlation ids) are ever attached as tag values. Blank/null values are normalised to
/// <c>"unknown"</c> so a mis-wired call site cannot explode cardinality or throw on the hot path.
/// </para>
/// </remarks>
public sealed class HotPathMetrics
{
    /// <summary>Fallback tag value for blank/null dimensions, keeping cardinality bounded.</summary>
    public const string Unknown = "unknown";

    private readonly Counter<long> _turnsTotal;
    private readonly Histogram<double> _turnDuration;

    private readonly Counter<long> _toolCalls;
    private readonly Histogram<double> _toolDuration;

    private readonly Counter<long> _providerRequests;
    private readonly Histogram<double> _providerDuration;
    private readonly Counter<long> _providerTokens;

    private readonly Counter<long> _cronRuns;
    private readonly Counter<long> _channelMessages;

    private readonly IMetrics _metrics;
    private int _activeSessionsGaugeRegistered;

    /// <summary>
    /// Creates the hot-path recorder and eagerly constructs every instrument over the
    /// canonical meter exposed by <paramref name="metrics"/>.
    /// </summary>
    /// <param name="metrics">The platform metrics facade (canonical BotNexus meter).</param>
    public HotPathMetrics(IMetrics metrics)
    {
        ArgumentNullException.ThrowIfNull(metrics);
        _metrics = metrics;

        _turnsTotal = metrics.CreateCounter<long>(
            BotNexusMeters.InstrumentName("turns", "total"),
            unit: "{turn}",
            description: "Total agent turns processed, tagged by agent, channel, and outcome.");
        _turnDuration = metrics.CreateHistogram<double>(
            BotNexusMeters.InstrumentName("turn", "duration"),
            unit: "ms",
            description: "Wall-clock duration of an agent turn in milliseconds.");

        _toolCalls = metrics.CreateCounter<long>(
            BotNexusMeters.InstrumentName("tool", "calls"),
            unit: "{call}",
            description: "Total tool invocations, tagged by tool and outcome.");
        _toolDuration = metrics.CreateHistogram<double>(
            BotNexusMeters.InstrumentName("tool", "duration"),
            unit: "ms",
            description: "Wall-clock duration of a tool invocation in milliseconds.");

        _providerRequests = metrics.CreateCounter<long>(
            BotNexusMeters.InstrumentName("provider", "requests"),
            unit: "{request}",
            description: "Total provider (LLM) requests, tagged by provider, model, and outcome.");
        _providerDuration = metrics.CreateHistogram<double>(
            BotNexusMeters.InstrumentName("provider", "duration"),
            unit: "ms",
            description: "Wall-clock duration of a provider (LLM) request in milliseconds.");
        _providerTokens = metrics.CreateCounter<long>(
            BotNexusMeters.InstrumentName("provider", "tokens"),
            unit: "{token}",
            description: "Provider token counts, tagged by provider, model, and direction (input|output).");

        _cronRuns = metrics.CreateCounter<long>(
            BotNexusMeters.InstrumentName("cron", "runs"),
            unit: "{run}",
            description: "Total cron job runs, tagged by job and status.");

        _channelMessages = metrics.CreateCounter<long>(
            BotNexusMeters.InstrumentName("channel", "messages"),
            unit: "{message}",
            description: "Total channel messages, tagged by channel and direction (inbound|outbound).");
    }

    /// <summary>
    /// Records a completed agent turn: increments <c>botnexus.turns.total</c> and records
    /// <c>botnexus.turn.duration</c>.
    /// </summary>
    /// <param name="agent">The agent id (bounded).</param>
    /// <param name="channel">The originating channel type (bounded).</param>
    /// <param name="outcome">Coarse outcome, e.g. <c>success</c>/<c>error</c>/<c>aborted</c>.</param>
    /// <param name="durationMs">Turn wall-clock duration in milliseconds.</param>
    public void RecordTurn(string agent, string channel, string outcome, double durationMs)
    {
        var tags = new TagList
        {
            { "agent", Bound(agent) },
            { "channel", Bound(channel) },
            { "outcome", Bound(outcome) },
        };
        _turnsTotal.Add(1, tags);
        _turnDuration.Record(durationMs, tags);
    }

    /// <summary>
    /// Records a completed tool invocation: increments <c>botnexus.tool.calls</c> and records
    /// <c>botnexus.tool.duration</c>.
    /// </summary>
    /// <param name="tool">The tool name (bounded).</param>
    /// <param name="outcome">Coarse outcome, e.g. <c>success</c>/<c>error</c>.</param>
    /// <param name="durationMs">Tool wall-clock duration in milliseconds.</param>
    public void RecordToolCall(string tool, string outcome, double durationMs)
    {
        var tags = new TagList
        {
            { "tool", Bound(tool) },
            { "outcome", Bound(outcome) },
        };
        _toolCalls.Add(1, tags);
        _toolDuration.Record(durationMs, tags);
    }

    /// <summary>
    /// Records a completed provider (LLM) request: increments <c>botnexus.provider.requests</c>,
    /// records <c>botnexus.provider.duration</c>, and adds input/output token counts to
    /// <c>botnexus.provider.tokens</c> (only non-zero directions are emitted).
    /// </summary>
    /// <param name="provider">The provider name (bounded).</param>
    /// <param name="model">The model id (bounded).</param>
    /// <param name="outcome">Coarse outcome, e.g. <c>success</c>/<c>error</c>.</param>
    /// <param name="durationMs">Request wall-clock duration in milliseconds.</param>
    /// <param name="inputTokens">Input (prompt) tokens reported by the provider.</param>
    /// <param name="outputTokens">Output (completion) tokens reported by the provider.</param>
    public void RecordProviderRequest(
        string provider,
        string model,
        string outcome,
        double durationMs,
        long inputTokens,
        long outputTokens)
    {
        var boundProvider = Bound(provider);
        var boundModel = Bound(model);

        var tags = new TagList
        {
            { "provider", boundProvider },
            { "model", boundModel },
            { "outcome", Bound(outcome) },
        };
        _providerRequests.Add(1, tags);
        _providerDuration.Record(durationMs, tags);

        if (inputTokens > 0)
        {
            _providerTokens.Add(inputTokens, new TagList
            {
                { "provider", boundProvider },
                { "model", boundModel },
                { "direction", "input" },
            });
        }

        if (outputTokens > 0)
        {
            _providerTokens.Add(outputTokens, new TagList
            {
                { "provider", boundProvider },
                { "model", boundModel },
                { "direction", "output" },
            });
        }
    }

    /// <summary>
    /// Records a cron job run: increments <c>botnexus.cron.runs</c>.
    /// </summary>
    /// <param name="job">The cron job id (bounded).</param>
    /// <param name="status">Coarse run status, e.g. <c>success</c>/<c>failed</c>/<c>skipped</c>.</param>
    public void RecordCronRun(string job, string status)
    {
        _cronRuns.Add(1, new TagList
        {
            { "job", Bound(job) },
            { "status", Bound(status) },
        });
    }

    /// <summary>
    /// Records a channel message crossing the gateway boundary: increments
    /// <c>botnexus.channel.messages</c>.
    /// </summary>
    /// <param name="channel">The channel type (bounded).</param>
    /// <param name="direction">Message direction: <c>inbound</c> or <c>outbound</c>.</param>
    public void RecordChannelMessage(string channel, string direction)
    {
        _channelMessages.Add(1, new TagList
        {
            { "channel", Bound(channel) },
            { "direction", Bound(direction) },
        });
    }

    /// <summary>
    /// Registers the observable gauge <c>botnexus.sessions.active</c> sampled from
    /// <paramref name="observeActiveCount"/> at collection time. Idempotent: only the first
    /// call registers the gauge; subsequent calls are no-ops so repeated host wiring cannot
    /// register duplicate instruments.
    /// </summary>
    /// <param name="observeActiveCount">Callback returning the current active-session count.</param>
    public void RegisterActiveSessionsGauge(Func<long> observeActiveCount)
    {
        ArgumentNullException.ThrowIfNull(observeActiveCount);
        if (Interlocked.Exchange(ref _activeSessionsGaugeRegistered, 1) != 0)
        {
            return;
        }

        _ = _metrics.CreateObservableGauge(
            BotNexusMeters.InstrumentName("sessions", "active"),
            observeActiveCount,
            unit: "{session}",
            description: "Currently active agent sessions.");
    }

    private static string Bound(string? value)
        => string.IsNullOrWhiteSpace(value) ? Unknown : value;
}
