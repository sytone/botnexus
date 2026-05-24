using Vogen;

namespace BotNexus.Domain.Primitives;

/// <summary>
/// Identifies a single execution (run) of a cron <see cref="JobId"/>. Construct via
/// <see cref="From(string)"/> for existing values or <see cref="Create"/> for a new
/// run; the value must be non-null, non-empty, non-whitespace and is stored trimmed.
/// </summary>
/// <remarks>
/// Each invocation of a cron job — whether scheduled or triggered via "run now" —
/// produces a fresh <see cref="RunId"/>. The id is persisted in the cron store
/// alongside the originating <see cref="JobId"/>, run status, and the
/// <see cref="SessionId"/> opened by the action. Part of the Phase 2 Vogen rollout
/// (#501).
/// </remarks>
[ValueObject<string>(conversions: Conversions.SystemTextJson)]
public readonly partial struct RunId
{
    /// <summary>
    /// Creates a new unique <see cref="RunId"/> using a 32-character N-formatted GUID.
    /// Matches the format that <c>SqliteCronStore.RecordRunStartAsync</c> has
    /// historically generated, so existing rows are still round-trippable.
    /// </summary>
    public static RunId Create() => From(Guid.NewGuid().ToString("N"));

    private static Validation Validate(string value) =>
        string.IsNullOrWhiteSpace(value)
            ? Validation.Invalid("RunId cannot be null, empty, or whitespace.")
            : Validation.Ok;

    private static string NormalizeInput(string input) =>
        input is null ? input! : input.Trim();
}
