using Vogen;

namespace BotNexus.Domain.Primitives;

/// <summary>
/// Identifies a cron job (scheduled task) inside the BotNexus world. Construct via
/// <see cref="From(string)"/>; the value must be non-null, non-empty, non-whitespace
/// and is stored trimmed.
/// </summary>
/// <remarks>
/// Cron job ids are persisted as TEXT in the SQLite cron store and carried as
/// strings over the SignalR/REST wire formats — this value object guarantees a
/// single canonical shape (trimmed, non-empty) without changing the storage or
/// network representation. Part of the Phase 2 Vogen rollout (#501).
/// </remarks>
[ValueObject<string>(conversions: Conversions.SystemTextJson)]
public readonly partial struct JobId
{
    private static Validation Validate(string value) =>
        string.IsNullOrWhiteSpace(value)
            ? Validation.Invalid("JobId cannot be null, empty, or whitespace.")
            : Validation.Ok;

    private static string NormalizeInput(string input) =>
        input is null ? input! : input.Trim();
}
