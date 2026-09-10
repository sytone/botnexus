using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Extensions.Skills.Recording;

/// <summary>
/// One tool call that actually happened during a run, projected from persisted session history.
/// </summary>
/// <remarks>
/// <para>
/// This is deliberately read out of the session store rather than supplied by the agent. The whole
/// premise of recorded skills is that the LITERAL values come from what ran and the SEMANTICS come
/// from the agent; letting the agent also supply the literals collapses the two and reintroduces
/// exactly the failure the post-turn claim auditor (#1600) exists to catch — an agent describing a
/// run it did not have. It is also the only source that survives compaction: an agent whose context
/// was compacted mid-run has genuinely lost its earlier steps, and the store has not.
/// </para>
/// </remarks>
public sealed record RecordedStep
{
    /// <summary>1-based position in the run, after start/result rows are collapsed to one step.</summary>
    public required int Ordinal { get; init; }

    /// <summary>The tool that was invoked.</summary>
    public required string ToolName { get; init; }

    /// <summary>
    /// Serialized JSON arguments the model supplied, or <c>null</c> for a legacy row written before
    /// #2906 populated arguments on both rows of a pair. <c>null</c> means "arguments were not
    /// recorded", never "the tool took no arguments" — a call with no arguments records <c>{}</c>.
    /// </summary>
    public string? ArgumentsJson { get; init; }

    /// <summary>True when any row of this call's pair reported an error.</summary>
    public bool IsError { get; init; }

    /// <summary>When the call STARTED (the first row's timestamp), not when it returned.</summary>
    public DateTimeOffset Timestamp { get; init; }
}

/// <summary>
/// A recorded step stripped of its argument VALUES, retaining only the argument key names.
/// This is the shape persisted into a draft on disk.
/// </summary>
/// <remarks>
/// Raw tool arguments routinely carry credentials — an <c>exec</c> invocation substituting an API
/// key inline is the documented pattern on this deployment — so a draft file must not become a
/// second, longer-lived copy of them. Validation that needs the values runs against the LIVE trace
/// at propose time and never reads them back off disk. What survives into the draft is enough to
/// show an operator the shape of the run and nothing more.
/// </remarks>
public sealed record RecordedStepSummary
{
    /// <summary>1-based position in the run.</summary>
    public required int Ordinal { get; init; }

    /// <summary>The tool that was invoked.</summary>
    public required string ToolName { get; init; }

    /// <summary>Argument key names only, in the order the JSON declared them. Never the values.</summary>
    public IReadOnlyList<string> ArgumentKeys { get; init; } = [];

    /// <summary>True when the call reported an error.</summary>
    public bool IsError { get; init; }
}

/// <summary>
/// Supplies the tool calls of ONE session — the session the asking agent is currently running in.
/// </summary>
/// <remarks>
/// The session is bound when the tool is contributed, not passed as a tool argument. An agent
/// therefore cannot aim the recorder at another conversation or another agent's run: there is no
/// argument in which to name one. That is an access-control decision, not an ergonomic one.
/// </remarks>
public interface ISessionTraceSource
{
    /// <summary>The session these steps come from, for display and provenance.</summary>
    SessionId SessionId { get; }

    /// <summary>Reads the current session's tool calls, oldest first.</summary>
    Task<IReadOnlyList<RecordedStep>> GetStepsAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Projects persisted <see cref="SessionEntry"/> rows into the tool calls a run actually made.
/// </summary>
public static class SkillRecorder
{
    /// <summary>
    /// Tool calls excluded from every recording. These are the meta-tools that manage skills; a
    /// recording that included them would make "and then I saved a skill" the last step of the
    /// skill, which on replay saves a skill, which records that it saved a skill.
    /// </summary>
    public static readonly IReadOnlySet<string> ExcludedToolNames =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "skill_record", "skill_manage" };

    /// <summary>
    /// Collapses session history into one <see cref="RecordedStep"/> per tool call.
    /// </summary>
    /// <remarks>
    /// <para>
    /// History carries a tool call in one of two shapes, and both occur on the same deployment
    /// (#78 found them living side by side): a live streaming turn writes ONE row per call and
    /// rewrites it in place, while a reloaded or blocking run writes a start row and a result row.
    /// Grouping on <see cref="SessionEntry.ToolCallId"/> normalises both to one step.
    /// </para>
    /// <para>
    /// Within a group the FIRST row supplies the name, arguments and timestamp — it is the start
    /// row, so its timestamp is when the call began — while the error flag is OR-ed across the
    /// whole group, because on the two-row shape only the RESULT row carries it. Taking the first
    /// row wholesale would report every failed call as a success.
    /// </para>
    /// <para>
    /// Rows with no <see cref="SessionEntry.ToolCallId"/> (legacy history) cannot be correlated and
    /// are each kept as their own step rather than dropped: an uncorrelatable call still happened.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<RecordedStep> FromHistory(IReadOnlyList<SessionEntry> history)
    {
        ArgumentNullException.ThrowIfNull(history);

        var steps = new List<RecordedStep>();
        var seenCallIds = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var entry in history)
        {
            if (entry.Role != MessageRole.Tool)
                continue;
            if (string.IsNullOrWhiteSpace(entry.ToolName))
                continue;
            if (ExcludedToolNames.Contains(entry.ToolName))
                continue;

            // Correlated second row: fold it into the step the start row already created.
            if (!string.IsNullOrWhiteSpace(entry.ToolCallId)
                && seenCallIds.TryGetValue(entry.ToolCallId, out var existingIndex))
            {
                if (entry.ToolIsError)
                    steps[existingIndex] = steps[existingIndex] with { IsError = true };

                // A start row written before #2906 has no arguments; a later result row may. Fill
                // the gap rather than leaving the step permanently argument-less.
                if (steps[existingIndex].ArgumentsJson is null && entry.ToolArgs is not null)
                    steps[existingIndex] = steps[existingIndex] with { ArgumentsJson = entry.ToolArgs };

                continue;
            }

            steps.Add(new RecordedStep
            {
                Ordinal = steps.Count + 1,
                ToolName = entry.ToolName!,
                ArgumentsJson = entry.ToolArgs,
                IsError = entry.ToolIsError,
                Timestamp = entry.Timestamp
            });

            if (!string.IsNullOrWhiteSpace(entry.ToolCallId))
                seenCallIds[entry.ToolCallId] = steps.Count - 1;
        }

        return steps;
    }
}
