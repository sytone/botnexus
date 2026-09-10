using System.Security.Cryptography;
using System.Text;

namespace BotNexus.Extensions.Skills.Recording;

/// <summary>
/// One value the agent proposes to turn into a parameter: the literal that actually ran, the slot
/// name that will replace it, and why the agent thinks it varies between runs.
/// </summary>
/// <remarks>
/// <see cref="ObservedValue"/> is the load-bearing field. It is what makes the proposal checkable:
/// a value the agent claims it used, that appears nowhere in the recorded trace, is a claim about a
/// run that did not happen, and <see cref="SkillDraftValidator"/> rejects the proposal rather than
/// writing a skill built on it.
/// </remarks>
public sealed record DraftParameter
{
    /// <summary>Slot name; substituted into the body as <c>{{name}}</c>.</summary>
    public required string Name { get; init; }

    /// <summary>The literal value this run used, which must appear in the recorded trace.</summary>
    public required string ObservedValue { get; init; }

    /// <summary>What a caller should supply here, and why it varies. Shown at confirm time.</summary>
    public required string Description { get; init; }
}

/// <summary>
/// A proposed skill that has been recorded but NOT installed: the staging state between
/// "the run happened" and "there is a skill".
/// </summary>
/// <remarks>
/// <para>
/// A draft lives outside every discovery root, so it is not a skill that happens to be hidden —
/// nothing scans where it is written. That distinction matters: a draft is unreviewed content
/// assembled from a transcript, and the failure mode to design against is one becoming loadable by
/// accident. <c>SkillDraftsAreNotDiscoverableTests</c> holds that line.
/// </para>
/// <para>
/// The draft is also what makes "confirm" mean something. Without it, an agent's proposal and its
/// write are the same act and there is nothing for an operator to read in between.
/// </para>
/// </remarks>
public sealed record SkillDraft
{
    /// <summary>Skill name; also the draft directory name.</summary>
    public required string Name { get; init; }

    /// <summary>Requested install scope: <c>agent</c>, <c>workspace</c> or <c>shared</c>.</summary>
    public required string Scope { get; init; }

    /// <summary>Full SKILL.md content, with <c>{{slots}}</c> in place of the parameterised values.</summary>
    public required string Content { get; init; }

    /// <summary>The values the agent proposes to parameterise.</summary>
    public IReadOnlyList<DraftParameter> Parameters { get; init; } = [];

    /// <summary>
    /// The shape of the run this was recorded from, WITHOUT argument values (see
    /// <see cref="RecordedStepSummary"/> for why the values do not survive to disk).
    /// </summary>
    public IReadOnlyList<RecordedStepSummary> Steps { get; init; } = [];

    /// <summary>
    /// Literals that appear in both the trace and the draft body and were NOT parameterised.
    /// Recorded at propose time so the confirm step can ask the one question that matters:
    /// is each of these meant to be part of the skill, or is it this run's accident?
    /// </summary>
    public IReadOnlyList<string> UnparameterisedLiterals { get; init; } = [];

    /// <summary>Non-blocking observations from validation, shown alongside the draft at review.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>The session the recording came from.</summary>
    public string SessionId { get; init; } = string.Empty;

    /// <summary>The agent that proposed it.</summary>
    public string CreatedBy { get; init; } = string.Empty;

    /// <summary>When the proposal was made.</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Short digest of everything that would be written — name, scope, body, and each parameter.
    /// <c>review</c> issues it and <c>confirm</c> requires it back.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Be precise about what this proves and what it does not. It proves the installed skill is
    /// byte-identical to the one that was displayed: if the draft is re-proposed with different
    /// content between review and confirm, the old token no longer matches and the write is
    /// refused. It does NOT prove a human was present — the agent holds both ends of the exchange.
    /// </para>
    /// <para>
    /// Claiming otherwise would make this the fifth "gate that does not gate" found on this
    /// codebase. The human gate is the operator reading the review output, which is why review
    /// prints the body in full rather than a summary of it, and why the unparameterised literals
    /// are listed rather than merely counted.
    /// </para>
    /// </remarks>
    public string ConfirmationToken => ComputeToken(Name, Scope, Content, Parameters);

    /// <summary>Computes the digest over the exact material that would be installed.</summary>
    public static string ComputeToken(
        string name,
        string scope,
        string content,
        IReadOnlyList<DraftParameter> parameters)
    {
        var canonical = new StringBuilder();
        canonical.Append(name).Append('\n');
        canonical.Append(scope).Append('\n');
        canonical.Append(content).Append('\n');

        // Parameter ORDER must not change the token: two proposals differing only in the order the
        // agent happened to list the same slots are the same skill, and an operator re-reviewing
        // one should not be told it changed.
        foreach (var p in parameters.OrderBy(p => p.Name, StringComparer.Ordinal))
            canonical.Append(p.Name).Append('=').Append(p.ObservedValue).Append('\n');

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString()));
        return Convert.ToHexStringLower(hash)[..12];
    }
}
