using BotNexus.Domain.Primitives;

namespace BotNexus.Gateway.Abstractions.Models;

/// <summary>Payload-free observation of one admitted agent run. Null counts mean unmeasured, not zero.</summary>
/// <param name="AgentRunId">Authoritative admission identity, independent of provider call IDs.</param>
/// <param name="StartedAt">Time of the observed admission.</param>
/// <param name="CompletedAt">Observed terminal time; null while running.</param>
/// <param name="Outcome">Running, Completed, Parked, Cancelled, Failed, or Unknown.</param>
/// <param name="CompletedResultCount">Actual non-incomplete tool results for a complete measured run.</param>
/// <param name="GuardObservations">Bounded operational categories and opaque references, never tool payloads.</param>
public sealed record AgentRunEvidence(
    AgentRunId AgentRunId,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    string Outcome,
    int? CompletedResultCount,
    IReadOnlyList<GuardObservation> GuardObservations);

/// <summary>A durable run observation and its monotonic, session-scoped query cursor.</summary>
public sealed record AgentRunEvidenceRow(
    AgentRunId AgentRunId,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    string Outcome,
    int? CompletedResultCount,
    IReadOnlyList<GuardObservation> GuardObservations,
    long Sequence);

/// <summary>Bounded selected-page statistics. Percentiles use nearest rank and are null without measured runs.</summary>
/// <remarks>Legacy coverage is an independent bounded sample, not inferred runs or a full-history total.</remarks>
public sealed record AgentRunEvidencePage(
    IReadOnlyList<AgentRunEvidenceRow> Runs,
    int SampleMeasuredRuns,
    int SemanticStops,
    int FuseStops,
    int UnknownRuns,
    int? ResultCountP50,
    int? ResultCountP95,
    int? ResultCountP99,
    int LegacySampleCount,
    int UnknownLegacyCount,
    bool LegacySampleTruncated,
    bool HasMore,
    long NextCursor);
