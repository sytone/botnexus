using System.Text.Json;
using System.Text.RegularExpressions;
using BotNexus.Domain.Text;

namespace BotNexus.Extensions.Skills.Recording;

/// <summary>The outcome of checking a proposal against the run it claims to describe.</summary>
/// <param name="Errors">Reasons the proposal must not become a draft. Non-empty means rejected.</param>
/// <param name="Warnings">Observations worth showing an operator that do not block the proposal.</param>
/// <param name="UnparameterisedLiterals">
/// Values that occur in BOTH the recorded run and the proposed body and were left literal. These
/// are the candidates an operator is being asked to rule on at confirm time.
/// </param>
public sealed record DraftValidation(
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> UnparameterisedLiterals)
{
    /// <summary>True when nothing blocks the proposal.</summary>
    public bool IsValid => Errors.Count == 0;
}

/// <summary>
/// Checks a proposed skill against the trace it was recorded from.
/// </summary>
/// <remarks>
/// <para>
/// This class is the reason recorded skills are worth building at all. Measurement of 1,702 tool
/// calls on this deployment established that a trace cannot identify parameters by itself: only 27%
/// of calls ever repeat verbatim, and with arguments stripped the commonest three-step sequence is
/// <c>bash → bash → bash</c>. All the signal is in the arguments, and the arguments are what vary.
/// </para>
/// <para>
/// So the trace is not asked to identify parameters. The agent proposes them and the operator rules
/// on them, and the trace does the one job it is actually good at: saying whether a claimed value
/// really occurred. That is what keeps the proposal honest without asking it to be derivable.
/// </para>
/// </remarks>
public static class SkillDraftValidator
{
    private static readonly Regex PlaceholderPattern =
        new(@"\{\{\s*([^}\s]+)\s*\}\}", RegexOptions.Compiled);

    /// <summary>
    /// Slot names a RECORDED proposal may use: lowercase only.
    /// </summary>
    /// <remarks>
    /// Narrower than what the loader accepts, on purpose. The frontmatter parser folds case, so a
    /// file declaring both <c>title</c> and <c>Title</c> keeps one and loses the other; requiring
    /// lowercase here makes that unreachable rather than merely detectable. The LOADER stays
    /// case-insensitive because hand-authored skills predate this tool and are not bound by its
    /// naming rule — the recorder is narrow, the loader is tolerant, and the two agree on what a
    /// slot resolves to.
    /// </remarks>
    private static readonly Regex ValidParameterName =
        new(@"^[a-z0-9][a-z0-9_-]*$", RegexOptions.Compiled);

    /// <summary>
    /// Splits instructions into candidate values. Deliberately does NOT split on <c>:</c>, <c>/</c>
    /// or <c>.</c>, so <c>http://nas:7878/api/v3/queue</c> survives as one token - a URL broken into
    /// fragments matches everywhere and means nothing.
    /// </summary>
    private static readonly Regex ContentTokenPattern =
        new(@"[^\s""'`,;(){}\[\]<>|*#]+", RegexOptions.Compiled);

    /// <summary>Shortest literal considered when suggesting values that stayed hard-coded.</summary>
    private const int MinLiteralLength = 4;

    /// <summary>
    /// Below this length, "does the value appear in the trace" stops being evidence: a two-character
    /// string occurs by chance in almost any JSON. Such parameters are allowed but flagged, because
    /// silently applying a check that cannot fail is worse than saying it did not apply.
    /// </summary>
    private const int MinCheckableValueLength = 3;

    /// <summary>Cap on the suggestion list, which is meant to be read rather than scrolled.</summary>
    private const int MaxSuggestedLiterals = 15;

    /// <summary>
    /// Validates a proposal. <paramref name="steps"/> must be the LIVE trace, not a persisted copy.
    /// </summary>
    public static DraftValidation Validate(
        string content,
        IReadOnlyList<DraftParameter> parameters,
        IReadOnlyList<RecordedStep> steps)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(steps);

        var errors = new List<string>();
        var warnings = new List<string>();

        // OrdinalIgnoreCase throughout, matching SkillDefinition.Parameters: the frontmatter parser
        // folds case, so a proposal declaring both "title" and "Title" cannot survive being written
        // out. Catching that here as a duplicate is better than letting the file lose one silently.
        var declared = new Dictionary<string, DraftParameter>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in parameters)
        {
            if (string.IsNullOrWhiteSpace(p.Name) || !ValidParameterName.IsMatch(p.Name))
            {
                errors.Add(
                    $"Parameter name '{p.Name}' is not usable as a slot. Use lowercase letters, " +
                    "digits, hyphens and underscores, starting with a letter or digit.");
                continue;
            }

            if (!declared.TryAdd(p.Name, p))
            {
                errors.Add($"Parameter '{p.Name}' is declared more than once.");
                continue;
            }

            if (string.IsNullOrWhiteSpace(p.Description))
                errors.Add(
                    $"Parameter '{p.Name}' has no description. The description is what an operator " +
                    "reads when deciding whether this really varies between runs.");

            if (string.IsNullOrEmpty(p.ObservedValue))
            {
                errors.Add(
                    $"Parameter '{p.Name}' has no observed value. Every parameter must record the " +
                    "literal this run actually used, so the proposal can be checked against the trace.");
                continue;
            }

            // A slot declared but never placed replays as whatever the body already says — the
            // parameter looks live in the listing and changes nothing when supplied.
            if (!ContainsPlaceholder(content, p.Name))
                errors.Add(
                    $"Parameter '{p.Name}' is declared but '{{{{{p.Name}}}}}' does not appear in the " +
                    "body, so supplying it would change nothing. Place the slot, or drop the parameter.");

            if (p.ObservedValue.Length < MinCheckableValueLength)
            {
                warnings.Add(
                    $"Parameter '{p.Name}' observed value '{p.ObservedValue}' is too short for the " +
                    "trace check to mean anything — a value this short matches almost any run. " +
                    "It was accepted unverified.");
            }
            else if (!AppearsInTrace(p.ObservedValue, steps))
            {
                errors.Add(
                    $"Parameter '{p.Name}' claims this run used '{Truncate(p.ObservedValue, 80)}', " +
                    "but that value appears in no recorded tool call. A skill must be built from " +
                    "what ran, not from what was remembered.");
            }
        }

        // The mirror check: a slot in the body with no declaration behind it is written into the
        // installed skill verbatim, so a replay reads "{{title}}" as an instruction.
        foreach (Match match in PlaceholderPattern.Matches(content))
        {
            var slot = match.Groups[1].Value;
            if (!declared.ContainsKey(slot))
                errors.Add(
                    $"The body contains '{{{{{slot}}}}}' but no parameter '{slot}' is declared, so a " +
                    "replay would read that placeholder as literal text. Declare it, or remove the slot.");
        }

        if (steps.Count == 0)
            errors.Add(
                "This session recorded no tool calls, so there is nothing to build a skill from. " +
                "A skill proposed here would be prose with no run behind it. If the work just " +
                "happened in THIS turn, its calls are not in session history yet — propose again " +
                "on the next turn.");

        return new DraftValidation(
            errors,
            warnings,
            errors.Count > 0 ? [] : FindUnparameterisedLiterals(content, declared.Values, steps));
    }

    /// <summary>
    /// Finds values in the proposed instructions that also occurred in the run and were left
    /// hard-coded. This is the other half of the confirm question: the agent has said what it thinks
    /// varies, and this says what it has decided is fixed, so the operator rules on both.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The direction matters, and the obvious one does not work. Scanning the TRACE for values and
    /// asking whether each appears in the body finds almost nothing here, because a tool argument is
    /// one long string - <c>curl -s http://nas:7878/api/v3/movie -d '{"title":"Dune"}'</c> - and no
    /// such string is ever a substring of prose. Since the commonest sequence on this deployment is
    /// <c>bash to bash to bash</c>, that would leave the list empty in exactly the case it exists for.
    /// </para>
    /// <para>
    /// So it runs the other way: tokenise the INSTRUCTIONS, and report the tokens that occur
    /// somewhere in what actually ran. A host, path, endpoint or id the agent wrote into the body
    /// that really appeared in the run is a value it has silently decided is part of the skill, and
    /// that is precisely the decision worth surfacing to whoever confirms it.
    /// </para>
    /// <para>
    /// This is a nudge list, not a proof. It will include the occasional ordinary word that happens
    /// to occur in a command, and it will miss a constant the agent paraphrased rather than quoted.
    /// Longest tokens come first, which puts hosts, paths and identifiers above short incidental
    /// words without having to guess which is which.
    /// </para>
    /// </remarks>
    private static IReadOnlyList<string> FindUnparameterisedLiterals(
        string content,
        IEnumerable<DraftParameter> parameters,
        IReadOnlyList<RecordedStep> steps)
    {
        var parameterised = parameters.Select(p => p.ObservedValue).ToList();

        // Placeholders go first: "{{title}}" tokenises to "title", which is a slot name rather than
        // a value the run chose, and offering it back as a fixed literal would be nonsense.
        var prose = PlaceholderPattern.Replace(content, " ");

        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match match in ContentTokenPattern.Matches(prose))
        {
            var token = match.Value.Trim('.', ',', ';', ':', '!', '?', '-', '(', ')');
            if (token.Length < MinLiteralLength)
                continue;
            // Already spoken for: it IS a parameter's value, or it sits inside one.
            if (parameterised.Any(v => v.Contains(token, StringComparison.Ordinal)))
                continue;
            if (!AppearsInTrace(token, steps))
                continue;

            found.Add(token);
        }

        return found
            .OrderByDescending(v => v.Length)
            .ThenBy(v => v, StringComparer.Ordinal)
            .Take(MaxSuggestedLiterals)
            .Select(v => Truncate(v, 120))
            .ToList();
    }

    /// <summary>
    /// True when the value occurs in some recorded call's arguments.
    /// </summary>
    /// <remarks>
    /// Checked against BOTH the raw argument text and its JSON-escaped form. A value containing a
    /// quote, a backslash or a newline — a Windows path, a shell command with an embedded string —
    /// is stored escaped, so a raw comparison alone would reject perfectly honest proposals for
    /// exactly the values most worth parameterising.
    /// </remarks>
    private static bool AppearsInTrace(string value, IReadOnlyList<RecordedStep> steps)
    {
        var escaped = JsonSerializer.Serialize(value);
        escaped = escaped.Length >= 2 ? escaped[1..^1] : escaped;

        foreach (var step in steps)
        {
            if (string.IsNullOrEmpty(step.ArgumentsJson))
                continue;
            if (step.ArgumentsJson.Contains(value, StringComparison.Ordinal))
                return true;
            if (step.ArgumentsJson.Contains(escaped, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    /// <summary>True when <c>{{name}}</c> appears in the body, tolerating inner whitespace.</summary>
    public static bool ContainsPlaceholder(string content, string name)
    {
        foreach (Match match in PlaceholderPattern.Matches(content))
        {
            if (string.Equals(match.Groups[1].Value, name, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>Replaces every declared slot in <paramref name="content"/> with its supplied value.</summary>
    /// <remarks>
    /// Lookups are case-insensitive regardless of the comparer <paramref name="values"/> was built
    /// with. Leaving that to the caller is a footgun: a slot resolved by one comparer and validated
    /// by another produces a skill that validates and then loads with the placeholder still in it.
    /// Declared as a <c>this string</c> extension per #2925, so the transformation is reachable from
    /// any string value rather than only from callers who know this class name.
    /// </remarks>
    public static string Substitute(this string content, IReadOnlyDictionary<string, string> values)
    {
        var lookup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in values)
            lookup[pair.Key] = pair.Value;

        return PlaceholderPattern.Replace(content, match =>
            lookup.TryGetValue(match.Groups[1].Value, out var value) ? value : match.Value);
    }

    /// <summary>Lists the distinct slot names appearing in a body, in first-seen order.</summary>
    public static IReadOnlyList<string> PlaceholdersIn(string content)
    {
        var seen = new List<string>();
        foreach (Match match in PlaceholderPattern.Matches(content))
        {
            var slot = match.Groups[1].Value;
            if (!seen.Contains(slot, StringComparer.OrdinalIgnoreCase))
                seen.Add(slot);
        }

        return seen;
    }

    /// <summary>
    /// Shortens a value for display. Goes through <see cref="StringTextExtensions.SafeTruncate"/>
    /// (#2883) rather than range slicing: these values are model- and command-supplied, so a raw cut
    /// can land inside a surrogate pair and emit a lone half of a character.
    /// </summary>
    private static string Truncate(string value, int max)
        => value.SafeTruncate(max, "…") ?? value;
}
