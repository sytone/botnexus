namespace BotNexus.Agent.Core.Tools;

using System.Management.Automation.Language;

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
    /// <remarks>
    /// The array form is already tokenised by the caller, so no parsing is required here: an element
    /// is either exactly the flag or it is not. The caller is nevertheless responsible for checking
    /// that the executable really is <c>pwsh</c>/<c>powershell</c> (issue #3566, clause 5) - a
    /// <c>-File</c> element belonging to some other command (<c>Get-ChildItem -File</c>) is a switch,
    /// not a script path.
    /// </remarks>
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
    /// Extracts the <c>-File</c> target from a raw shell command line by <b>parsing</b> it with
    /// PowerShell's own parser, binding the flag only when it is a parameter of a
    /// <c>pwsh</c>/<c>powershell</c> command and its argument is a literal string. Returns
    /// <see langword="false"/> - i.e. <b>fails open</b> - in every other case.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why the real parser (issue #3566).</b> This method used to split the command line on
    /// whitespace and take the token after the literal text <c>-File</c>. That is not PowerShell's
    /// tokenisation, so everything that terminates a command element - <c>;</c>, <c>|</c>, <c>)</c>,
    /// a redirect - was swallowed into the "path", and the preflight then refused the call claiming a
    /// file that plainly exists does not:
    /// <c>Script not found: C:\...\get-farnsworth-token.ps1;</c>. It also matched the <i>text</i>
    /// <c>-File</c> anywhere, so <c>Get-ChildItem &lt;dir&gt; -File | ...</c> bound the pipe character
    /// as a filename. Over a 7-day corpus this produced <b>206 false refusals out of 316</b>, across
    /// 22 agents - each costing a full turn and pointing the agent at the wrong problem.
    /// </para>
    /// <para>
    /// This is the third appearance of the same anti-pattern in the shell preflight (#2757 nested
    /// quoting, #2905 terminators/here-strings, now <c>-File</c> extraction), so the fix is
    /// deliberately <i>not</i> a fourth heuristic: <c>Parser.ParseInput</c> is the oracle. It builds
    /// an AST only - no runspace, nothing executed.
    /// </para>
    /// <para>
    /// <b>Fail open, always.</b> A preflight that blocks execution must be at least as accurate as
    /// the shell it guards, so anything short of a confident binding lets the command run and lets
    /// the shell report its own native error: a command that does not parse, a <c>-File</c> whose
    /// argument is a variable or subexpression rather than a literal, a parser that throws. The only
    /// outcome that can lead to a refusal is a literal path bound to a real <c>pwsh</c> invocation -
    /// and because the value comes from the AST, the reported path can never carry a trailing
    /// separator or redirect character.
    /// </para>
    /// </remarks>
    public static bool TryGetFileTargetFromCommandLine(string? command, out string path)
    {
        path = string.Empty;
        if (string.IsNullOrWhiteSpace(command))
        {
            return false;
        }

        ScriptBlockAst ast;
        ParseError[] errors;
        try
        {
            ast = Parser.ParseInput(command, out _, out errors);
        }
        catch
        {
            // Fail open: an unexpected parser failure is not evidence about the agent's command.
            return false;
        }

        // Clause 7: a command that cannot be parsed is allowed to run. We cannot bind a path with
        // confidence in a broken AST, and guessing is precisely what produced this defect.
        if (ast is null || (errors is not null && errors.Length > 0))
        {
            return false;
        }

        foreach (var node in ast.FindAll(n => n is CommandAst, searchNestedScriptBlocks: true))
        {
            if (node is CommandAst commandAst && TryBindFileArgument(commandAst, out path))
            {
                return true;
            }
        }

        path = string.Empty;
        return false;
    }

    /// <summary>
    /// Binds the <c>-File</c> argument of a single parsed command, or returns <see langword="false"/>
    /// when this command is not a PowerShell invocation, carries no <c>-File</c> parameter, or its
    /// argument is not a literal path.
    /// </summary>
    private static bool TryBindFileArgument(CommandAst commandAst, out string path)
    {
        path = string.Empty;

        var elements = commandAst.CommandElements;
        if (elements.Count == 0)
        {
            return false;
        }

        // Clause 5: -File is a script path ONLY on pwsh/powershell. On any other command it is that
        // command's own switch (Get-ChildItem -File) and means nothing to this preflight.
        if (elements[0] is not StringConstantExpressionAst executable
            || !IsPowerShellExecutable(executable.Value))
        {
            return false;
        }

        for (var i = 1; i < elements.Count; i++)
        {
            if (elements[i] is not CommandParameterAst parameter || !IsFileParameterName(parameter.ParameterName))
            {
                continue;
            }

            // `-File:script.ps1` binds its argument to the parameter itself; `-File script.ps1`
            // leaves it as the following element.
            var argument = parameter.Argument ?? (i + 1 < elements.Count ? elements[i + 1] as ExpressionAst : null);

            // Only a literal string is a path we can probe. A variable, a subexpression or an
            // expandable string could resolve to anything at run time, so fail open.
            if (argument is not StringConstantExpressionAst literal || string.IsNullOrWhiteSpace(literal.Value))
            {
                return false;
            }

            path = literal.Value;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="name"/> is how PowerShell's own parameter
    /// binder would spell <c>-File</c>: the full name or any unambiguous prefix of it (<c>-f</c>,
    /// <c>-fi</c>, …), case-insensitively.
    /// </summary>
    private static bool IsFileParameterName(string? name) =>
        !string.IsNullOrEmpty(name)
        && "file".StartsWith(name, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="executable"/> names PowerShell
    /// (<c>pwsh</c> or <c>powershell</c>), ignoring any directory, extension, or case.
    /// </summary>
    private static bool IsPowerShellExecutable(string? executable) =>
        PowerShellPreflight.IsPowerShellExecutable(executable);

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
