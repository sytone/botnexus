namespace BotNexus.Agent.Core.Tools;

/// <summary>
/// Preflight for file-based script invocations (<c>pwsh -File &lt;path&gt;</c>) that turns a missing
/// skill wrapper into an actionable diagnosis instead of <c>pwsh</c>'s bare usage banner (issue #2758).
/// </summary>
/// <remarks>
/// <para>
/// <b>The defect.</b> <c>pwsh -File</c> reports a non-existent script as an <i>argument-parsing</i>
/// error, not a path error, so the agent receives
/// <c>"... is not recognized as the name of a script file"</c> followed by the generic usage banner.
/// That message names neither the skill nor any candidate, so an agent that guessed
/// <c>ListMessages.ps1</c> (real: <c>ListChatMessages.ps1</c>) has no signal to correct with and
/// retries the identical call - 72 failures/week across 9 agents in the forensics window, 28 of them
/// the same file that has never existed.
/// </para>
/// <para>
/// <b>Why enumeration, not an alias table.</b> AC2 requires that a wrapper added later appears in the
/// hint automatically. The candidate set is therefore produced by listing the skill's <c>scripts/</c>
/// directory at failure time. A hand-maintained "commonly guessed wrong names" table would be a second
/// source of truth that drifts the moment the generator emits a new wrapper - the same
/// exemplar-never-propagated failure that produced this issue's sibling defects.
/// </para>
/// <para>
/// <b>Never silently substitute.</b> A near match is <i>reported</i>, never executed. Running
/// <c>ListChatMessages.ps1</c> because the agent asked for <c>ListMessages.ps1</c> would make a
/// guess indistinguishable from a correct call and could invoke an operation the caller never
/// requested. The message says so explicitly.
/// </para>
/// <para>
/// <b>Dependency posture.</b> Delegates for existence and enumeration are injected rather than an
/// <c>IFileSystem</c>, matching <see cref="PowerShellPreflight"/>: this type is consumed by
/// <c>BotNexus.Extensions.ExecTool</c>, which loads into an isolated <c>AssemblyLoadContext</c> and
/// must ship its whole managed closure (issue #2184).
/// </para>
/// </remarks>
public static class SkillScriptPreflight
{
    /// <summary>Maximum number of near matches surfaced in a rejection message.</summary>
    public const int MaxSuggestions = 5;

    /// <summary>Maximum number of available wrapper names listed before the list is elided.</summary>
    private const int MaxListedScripts = 40;

    /// <summary>
    /// Identifies a script path that lives under a skill's <c>scripts/</c> directory.
    /// </summary>
    /// <param name="SkillName">The skill directory name (e.g. <c>teams</c>).</param>
    /// <param name="ScriptsDirectory">The absolute-or-relative <c>scripts/</c> directory path.</param>
    /// <param name="ScriptFileName">The requested wrapper file name (e.g. <c>ListMessages.ps1</c>).</param>
    public sealed record SkillScriptContext(string SkillName, string ScriptsDirectory, string ScriptFileName);

    /// <summary>
    /// Classifies <paramref name="path"/> as a skill wrapper when it matches
    /// <c>.../skills/&lt;skill&gt;/scripts/&lt;file&gt;</c>, ignoring separator style. Returns
    /// <see langword="null"/> for every other path so the generic case keeps its plain
    /// path-not-found treatment (AC5).
    /// </summary>
    public static SkillScriptContext? DescribeSkillScript(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var normalized = path.Replace('\\', '/').Trim();
        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);

        // Need at least: skills / <skill> / scripts / <file>
        if (segments.Length < 4)
        {
            return null;
        }

        var fileIndex = segments.Length - 1;
        var scriptsIndex = fileIndex - 1;
        var skillIndex = fileIndex - 2;
        var skillsRootIndex = fileIndex - 3;

        if (!string.Equals(segments[scriptsIndex], "scripts", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(segments[skillsRootIndex], "skills", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var scriptsDirectory = normalized[..normalized.LastIndexOf('/')];
        return new SkillScriptContext(segments[skillIndex], scriptsDirectory, segments[fileIndex]);
    }

    /// <summary>
    /// Extracts the <c>-File</c> target from a pre-split argument array. Returns
    /// <see langword="false"/> when the invocation is not file-based or the flag has no operand.
    /// </summary>
    public static bool TryGetFileTarget(IReadOnlyList<string> args, out string path)
    {
        path = string.Empty;
        for (var i = 0; i < args.Count; i++)
        {
            if (!IsFileFlag(args[i]))
            {
                continue;
            }

            if (i + 1 >= args.Count)
            {
                return false;
            }

            path = args[i + 1];
            return !string.IsNullOrWhiteSpace(path);
        }

        return false;
    }

    /// <summary>
    /// Extracts the <c>-File</c> target from a raw shell command line. Quote-aware: a <c>-File</c>
    /// that appears <i>inside</i> a quoted argument (e.g. <c>echo '-File x.ps1'</c>) is text, not a
    /// flag, and is ignored.
    /// </summary>
    public static bool TryGetFileTargetFromCommandLine(string? command, out string path)
    {
        path = string.Empty;
        if (string.IsNullOrWhiteSpace(command))
        {
            return false;
        }

        var tokens = Tokenize(command);
        for (var i = 0; i < tokens.Count; i++)
        {
            var (text, quoted) = tokens[i];
            if (quoted || !IsFileFlag(text))
            {
                continue;
            }

            if (i + 1 >= tokens.Count)
            {
                return false;
            }

            path = tokens[i + 1].Text;
            return !string.IsNullOrWhiteSpace(path);
        }

        return false;
    }

    /// <summary>
    /// Returns the closest existing script names to <paramref name="requested"/>, nearest first,
    /// capped at <paramref name="max"/>. Ties break on ordinal name order so the emitted message is
    /// deterministic regardless of directory enumeration order.
    /// </summary>
    /// <remarks>
    /// Two signals are combined because pure edit distance is too blunt here: the real-world misses
    /// are <i>omitted infixes</i> (<c>ListMessages</c> -> <c>List<b>Chat</b>Messages</c>), which are
    /// four to seven edits away yet obviously the intended target. A candidate qualifies when the
    /// requested stem is a subsequence of it (or vice versa), or when the edit distance is within a
    /// length-scaled threshold. Ranking is always by edit distance so the nearest wins.
    /// </remarks>
    public static IReadOnlyList<string> FindClosestScripts(
        string requested,
        IReadOnlyList<string> available,
        int max = MaxSuggestions)
    {
        if (string.IsNullOrWhiteSpace(requested) || available.Count == 0 || max <= 0)
        {
            return Array.Empty<string>();
        }

        var requestedStem = StripExtension(requested);
        if (requestedStem.Length == 0)
        {
            return Array.Empty<string>();
        }

        var scored = new List<(string Name, int Distance)>();
        foreach (var candidate in available)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            var candidateStem = StripExtension(candidate);
            var distance = LevenshteinDistance(requestedStem, candidateStem);
            var threshold = Math.Max(2, (int)(Math.Max(requestedStem.Length, candidateStem.Length) * 0.34));

            var qualifies = distance <= threshold
                            || IsSubsequence(requestedStem, candidateStem)
                            || IsSubsequence(candidateStem, requestedStem);

            if (qualifies)
            {
                scored.Add((candidate, distance));
            }
        }

        return scored
            .OrderBy(entry => entry.Distance)
            .ThenBy(entry => entry.Name, StringComparer.Ordinal)
            .Take(max)
            .Select(entry => entry.Name)
            .ToArray();
    }

    /// <summary>
    /// Validates a <c>-File</c> target. Returns <see langword="null"/> when the script exists, when
    /// no path was supplied, or when the path cannot be resolved at preflight time (a variable or a
    /// wildcard could still expand to a real file, so the preflight stays silent rather than refuse a
    /// legitimate command). Otherwise returns the diagnostic message.
    /// </summary>
    /// <param name="path">The script path the caller asked <c>pwsh -File</c> to run.</param>
    /// <param name="exists">Existence probe for a file path.</param>
    /// <param name="listScripts">Enumerates the wrapper file names in a skill's scripts directory.</param>
    public static string? Validate(
        string? path,
        Func<string, bool> exists,
        Func<string, IReadOnlyList<string>> listScripts)
    {
        ArgumentNullException.ThrowIfNull(exists);
        ArgumentNullException.ThrowIfNull(listScripts);

        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var target = path.Trim().Trim('\'', '"');
        if (target.Length == 0 || ContainsUnresolvableToken(target))
        {
            return null;
        }

        if (SafeExists(exists, target))
        {
            return null;
        }

        var context = DescribeSkillScript(target);
        if (context is null)
        {
            // AC5: outside a skill directory report a plain path-not-found and invent no candidates.
            return $"Script not found: {target}. Nothing exists at that path - verify the path, "
                   + "or create the file before invoking it.";
        }

        var available = SafeList(listScripts, context.ScriptsDirectory);
        var suggestions = FindClosestScripts(context.ScriptFileName, available);

        return BuildSkillMessage(context, available, suggestions);
    }

    /// <summary>
    /// Runs <see cref="Validate"/> and throws an <see cref="ArgumentException"/> carrying the
    /// diagnostic when the target is missing. Does nothing when the script exists.
    /// </summary>
    /// <exception cref="ArgumentException">The requested script does not exist.</exception>
    public static void ThrowIfMissing(
        string? path,
        Func<string, bool> exists,
        Func<string, IReadOnlyList<string>> listScripts)
    {
        var message = Validate(path, exists, listScripts);
        if (message is not null)
        {
            throw new ArgumentException(message);
        }
    }

    /// <summary>
    /// Filesystem-backed overload used by the shell/exec tools. Enumeration failures degrade to an
    /// empty candidate list rather than masking the original not-found diagnosis.
    /// </summary>
    public static void ThrowIfMissing(string? path) =>
        ThrowIfMissing(path, File.Exists, EnumerateScripts);

    /// <summary>
    /// Lists the script file names in <paramref name="directory"/>, returning an empty list when the
    /// directory is absent or unreadable.
    /// </summary>
    public static IReadOnlyList<string> EnumerateScripts(string directory)
    {
        try
        {
            if (!Directory.Exists(directory))
            {
                return Array.Empty<string>();
            }

            return Directory.EnumerateFiles(directory)
                .Select(Path.GetFileName)
                .Where(name => !string.IsNullOrEmpty(name))
                .Select(name => name!)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }

    private static string BuildSkillMessage(
        SkillScriptContext context,
        IReadOnlyList<string> available,
        IReadOnlyList<string> suggestions)
    {
        var message =
            $"Skill wrapper not found: '{context.ScriptFileName}' does not exist in the "
            + $"'{context.SkillName}' skill's scripts directory ({context.ScriptsDirectory}).";

        if (suggestions.Count > 0)
        {
            message += $" Closest matches: {string.Join(", ", suggestions)}."
                       + " These were NOT executed - re-issue the call with the exact name you intend.";
        }

        if (available.Count > 0)
        {
            var listed = available.Count > MaxListedScripts
                ? string.Join(", ", available.Take(MaxListedScripts)) + $", \u2026 (+{available.Count - MaxListedScripts} more)"
                : string.Join(", ", available);
            message += $" Available wrappers: {listed}.";
        }
        else
        {
            message += " No wrappers were found in that directory - verify the skill name and its"
                       + " installation before retrying.";
        }

        return message;
    }

    private static bool SafeExists(Func<string, bool> exists, string path)
    {
        try
        {
            return exists(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // An unprobeable path is not evidence of absence - stay silent.
            return true;
        }
    }

    private static IReadOnlyList<string> SafeList(Func<string, IReadOnlyList<string>> listScripts, string directory)
    {
        try
        {
            return listScripts(directory) ?? Array.Empty<string>();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return Array.Empty<string>();
        }
    }

    // A path carrying a variable, a subexpression or a glob may still resolve to a real file at
    // execution time, so the preflight must not claim it is missing.
    private static bool ContainsUnresolvableToken(string path) =>
        path.IndexOfAny(['$', '*', '?', '`']) >= 0
        || path.Contains("%", StringComparison.Ordinal);

    private static bool IsFileFlag(string arg) =>
        string.Equals(arg, "-File", StringComparison.OrdinalIgnoreCase)
        || string.Equals(arg, "-f", StringComparison.OrdinalIgnoreCase);

    private static string StripExtension(string name)
    {
        var dot = name.LastIndexOf('.');
        return dot > 0 ? name[..dot] : name;
    }

    // True when every character of "inner" appears in "outer" in order - the fingerprint of an
    // omitted infix such as ListMessages -> ListChatMessages.
    private static bool IsSubsequence(string inner, string outer)
    {
        if (inner.Length == 0 || inner.Length > outer.Length)
        {
            return false;
        }

        var i = 0;
        foreach (var c in outer)
        {
            if (char.ToLowerInvariant(c) == char.ToLowerInvariant(inner[i]) && ++i == inner.Length)
            {
                return true;
            }
        }

        return false;
    }

    private static List<(string Text, bool Quoted)> Tokenize(string command)
    {
        var tokens = new List<(string, bool)>();
        var current = new System.Text.StringBuilder();
        var quoteChar = '\0';
        var quoted = false;
        var started = false;

        void Flush()
        {
            if (started)
            {
                tokens.Add((current.ToString(), quoted));
                current.Clear();
                quoted = false;
                started = false;
            }
        }

        foreach (var c in command)
        {
            if (quoteChar != '\0')
            {
                if (c == quoteChar)
                {
                    quoteChar = '\0';
                }
                else
                {
                    current.Append(c);
                }

                continue;
            }

            if (c is '\'' or '"')
            {
                quoteChar = c;
                quoted = true;
                started = true;
                continue;
            }

            if (char.IsWhiteSpace(c))
            {
                Flush();
                continue;
            }

            started = true;
            current.Append(c);
        }

        Flush();
        return tokens;
    }

    /// <summary>
    /// Classic iterative two-row Levenshtein edit distance, compared case-insensitively.
    /// Deliberately dependency-free; the inputs are short file names.
    /// </summary>
    private static int LevenshteinDistance(string left, string right)
    {
        left = left.ToLowerInvariant();
        right = right.ToLowerInvariant();

        if (left.Length == 0)
        {
            return right.Length;
        }

        if (right.Length == 0)
        {
            return left.Length;
        }

        var previous = new int[right.Length + 1];
        var current = new int[right.Length + 1];

        for (var j = 0; j <= right.Length; j++)
        {
            previous[j] = j;
        }

        for (var i = 1; i <= left.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= right.Length; j++)
            {
                var substitutionCost = left[i - 1] == right[j - 1] ? 0 : 1;
                current[j] = Math.Min(
                    Math.Min(current[j - 1] + 1, previous[j] + 1),
                    previous[j - 1] + substitutionCost);
            }

            (previous, current) = (current, previous);
        }

        return previous[right.Length];
    }
}
