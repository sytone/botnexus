using System.Collections.Concurrent;
using System.Text.Json.Serialization;
using BotNexus.Domain.Serialization;

namespace BotNexus.Gateway.Abstractions.Sessions;

/// <summary>
/// #2460/#2489: stable, machine-readable code naming WHY a compaction aborted without mutating
/// history, as a class-based smart enum following the <c>BotNexus.Domain.Primitives</c> convention
/// (<c>MessageKind</c>, <c>SessionType</c>, <c>ExecutionStrategy</c>, ...): private ctor,
/// <c>static readonly</c> members registered via <c>Register</c>, a <see cref="Value"/> wire
/// property, a non-throwing-on-unknown <see cref="FromString"/>, and
/// <see cref="SmartEnumJsonConverter{TSmartEnum}"/> for JSON round-trip.
/// <para>
/// Every <c>Succeeded = false</c> return path in the compactor stamps exactly one of these onto
/// <see cref="CompactionResult.SkipReason"/>, and the coordinator logs it alongside the
/// <c>outcome=Aborted</c> line so a repeating no-op abort loop is diagnosable from logs alone.
/// </para>
/// <para>
/// The <see cref="Value"/> strings are a LOG AND TELEMETRY CONTRACT: they are the tokens production
/// log queries match on. Do not rename an existing code. Unlike the
/// <c>BotNexus.Domain.Primitives</c> siblings this type deliberately does NOT lower-case in
/// <see cref="FromString"/>: the eight codes shipped in PR #2465 are PascalCase on the wire, and
/// canonicalising to lower-case would silently rewrite that contract. Lookup is still
/// case-insensitive, so a differently-cased input resolves to the canonical PascalCase member.
/// </para>
/// </summary>
[JsonConverter(typeof(SmartEnumJsonConverter<CompactionSkipReason>))]
public sealed class CompactionSkipReason : IEquatable<CompactionSkipReason>
{
    private static readonly ConcurrentDictionary<string, CompactionSkipReason> Registry =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The per-session circuit breaker is open and still inside its cooldown window.</summary>
    public static readonly CompactionSkipReason CircuitBreakerOpen = Register("CircuitBreakerOpen");

    /// <summary>The session history snapshot was empty; there is nothing to summarise.</summary>
    public static readonly CompactionSkipReason EmptyHistory = Register("EmptyHistory");

    /// <summary>
    /// The turn split produced no summarizable entries (and the PreservedTurns fallback also found
    /// none). This is the branch behind the observed repeating abort loop: the session keeps
    /// growing while every split remains unsummarizable.
    /// </summary>
    public static readonly CompactionSkipReason NoSummarizableTurns = Register("NoSummarizableTurns");

    /// <summary>The summarization call exceeded the configured compaction timeout.</summary>
    public static readonly CompactionSkipReason SummarizationTimeout = Register("SummarizationTimeout");

    /// <summary>All candidate models returned an empty/unusable summary.</summary>
    public static readonly CompactionSkipReason EmptySummary = Register("EmptySummary");

    /// <summary>The summarization call threw (surfaced by the coordinator, not the compactor).</summary>
    public static readonly CompactionSkipReason SummarizationFailed = Register("SummarizationFailed");

    /// <summary>
    /// The compaction result could not be applied because history was destructively modified
    /// while the summary call was in flight.
    /// </summary>
    public static readonly CompactionSkipReason ConcurrentHistoryChange = Register("ConcurrentHistoryChange");

    /// <summary>The session was deleted, sealed or rebound while compaction was in flight (#1518).</summary>
    public static readonly CompactionSkipReason SessionRebound = Register("SessionRebound");

    /// <summary>Gets the stable wire value of this skip reason.</summary>
    public string Value { get; }

    private CompactionSkipReason(string value) => Value = value;

    /// <summary>
    /// Resolves a <see cref="CompactionSkipReason"/> from its wire value, registering previously
    /// unseen values so a newer gateway emitting a forward code round-trips through an older reader
    /// instead of throwing (matching <c>MessageKind.FromString</c>). Case-insensitive: a
    /// differently-cased spelling of a declared code resolves to that canonical member and keeps its
    /// original PascalCase <see cref="Value"/>.
    /// </summary>
    /// <param name="value">The wire value (e.g. <c>NoSummarizableTurns</c>).</param>
    /// <returns>The corresponding skip reason.</returns>
    public static CompactionSkipReason FromString(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("CompactionSkipReason cannot be empty", nameof(value));

        return Registry.GetOrAdd(value.Trim(), static v => new CompactionSkipReason(v));
    }

    /// <summary>
    /// Resolves a skip reason from an optional wire value, mapping <c>null</c>/blank (a successful
    /// compaction, or a legacy unstamped result) to <c>null</c> rather than to a sentinel code, so
    /// "no reason" stays distinguishable from "some reason".
    /// </summary>
    /// <param name="value">The optional wire value.</param>
    /// <returns>The resolved reason, or <c>null</c> when <paramref name="value"/> is blank.</returns>
    public static CompactionSkipReason? FromNullableString(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : FromString(value);

    /// <summary>Performs the declared conversion or operator operation.</summary>
    public static implicit operator CompactionSkipReason(string value) => FromString(value);

    /// <summary>Performs the declared conversion or operator operation.</summary>
    public static implicit operator string(CompactionSkipReason reason) => reason.Value;

    /// <inheritdoc/>
    public bool Equals(CompactionSkipReason? other) =>
        other is not null && string.Equals(Value, other.Value, StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is CompactionSkipReason other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => StringComparer.OrdinalIgnoreCase.GetHashCode(Value);

    /// <inheritdoc/>
    public override string ToString() => Value;

    private static CompactionSkipReason Register(string value)
    {
        var reason = new CompactionSkipReason(value);
        Registry.TryAdd(value, reason);
        return reason;
    }
}
