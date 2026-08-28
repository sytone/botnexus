namespace BotNexus.Agent.Core.Tools;

using System.Management.Automation.Language;

/// <summary>
/// Preflight validator for inline PowerShell scripts passed to <c>pwsh</c>/<c>powershell</c> via
/// <c>-Command</c>. It catches the syntax mistakes that agents most commonly emit - empty pipe
/// elements (<c>... | Sort-Ob |</c>), malformed <c>${...}</c> variable references, and unbalanced
/// braces - <b>before</b> the command is handed to a shell process, so the agent gets an immediate,
/// actionable rejection instead of a late runtime <c>ParserError</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the real parser (issue #3576).</b> This type began as a hand-rolled scanner specifically to
/// avoid taking a dependency on <c>System.Management.Automation</c>, on the reasoning that a small
/// quote/comment-aware scanner reproducing the observed error signatures was far cheaper. A 7-day
/// forensic replay retired that reasoning: of 102 commands that produced a runtime <c>ParserError</c>,
/// <b>85 failed to parse deterministically</b> and the scanner admitted every one. The gap is not a
/// missing rule or three - it is the entire long tail of a real grammar - so the authoritative parser
/// now runs on every inline command. The dependency is used for <c>Parser.ParseInput</c> only: it
/// builds an AST, starts no runspace and executes nothing.
/// </para>
/// <para>
/// The original hand-rolled rules are RETAINED and run <b>first</b>. They reproduce signatures the real
/// parser also produces, and keeping them ahead of the parse preserves their offsets and their derived
/// cause explanations (e.g. the #2417 "the operand before '|' is empty, so an outer quoting layer
/// probably ate a $variable" diagnosis, which the bare parser message does not give). The parser pass
/// therefore only ever ADDS refusals; it never changes an existing one.
/// </para>
/// <para>
/// The scanner rules are deliberately conservative: when in doubt they report <b>valid</b>. They only
/// reject the specific, high-confidence signatures that the real PowerShell parser also rejects
/// (verified locally):
/// <list type="bullet">
///   <item><c>An empty pipe element is not allowed.</c> - a leading or trailing <c>|</c>.</item>
///   <item><c>Variable reference is not valid. The variable name is missing.</c> - <c>${}</c>, <c>${:}</c>, <c>${x:}</c>.</item>
///   <item><c>Unexpected token ':' ...</c> - a bare <c>${var}:</c> reference.</item>
///   <item><c>Missing closing '}' ...</c> / <c>Unexpected token '}' ...</c> - unbalanced braces.</item>
/// </list>
/// </para>
/// <para>
/// <b>What this preflight deliberately does NOT do (issue #2757).</b> It never refuses a command the
/// real parser would accept. An earlier <c>Nested quoting detected</c> rule did exactly that - it
/// flagged single-quoted argument values containing <c>"</c> or <c>$</c> - and was removed after a
/// corpus replay showed 20 of 20 such rejections parsed cleanly. With the parser now the oracle that
/// property holds by construction, and the gate fails open on anything that is not a definite
/// <c>ParseError</c>.
/// </para>
/// </remarks>
public static class PowerShellPreflight
{
    /// <summary>
    /// The remediation hint appended to every rejection. Steers the agent away from fragile inline
    /// <c>-Command</c> scripts toward the robust file-based invocation.
    /// </summary>
    public const string RemediationHint =
        "write a tmp/*.ps1 file and invoke pwsh -NoProfile -File tmp/script.ps1 instead of passing the script inline via -Command.";

    /// <summary>
    /// Describes a single syntax problem found in an inline PowerShell script: a human-readable
    /// message mirroring the real parser, plus the character offset of the offending extent.
    /// </summary>
    /// <param name="Message">Parser-style description of the problem.</param>
    /// <param name="Offset">Zero-based character offset of the offending extent within the script.</param>
    /// <param name="Remediation">
    /// A shape-specific fix that REPLACES the generic <see cref="RemediationHint"/> when present
    /// (issue #3576). For a syntax error such as <c>foreach(...){...} | cmd</c> the generic "move it
    /// into a tmp/*.ps1 file" advice is actively wrong - relocating invalid syntax does not make it
    /// valid - so a named correction must displace it rather than sit alongside it.
    /// </param>
    public sealed record PreflightError(string Message, int Offset, string? Remediation = null);

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="executable"/> names PowerShell
    /// (<c>pwsh</c> or <c>powershell</c>), ignoring any directory, <c>.exe</c> extension, or case.
    /// </summary>
    public static bool IsPowerShellExecutable(string? executable)
    {
        if (string.IsNullOrWhiteSpace(executable))
        {
            return false;
        }

        // Normalize both Windows and POSIX separators so a Windows-style path such as
        // "C:\Program Files\PowerShell\7\pwsh.exe" classifies correctly even when this runs on
        // a Linux host (where Path.GetFileNameWithoutExtension does not treat '\' as a separator).
        var trimmed = executable.Trim();
        var lastSeparator = trimmed.LastIndexOfAny(new[] { '/', '\\' });
        var fileName = lastSeparator >= 0 ? trimmed[(lastSeparator + 1)..] : trimmed;
        var name = Path.GetFileNameWithoutExtension(fileName);
        return string.Equals(name, "pwsh", StringComparison.OrdinalIgnoreCase)
               || string.Equals(name, "powershell", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Determines whether an argument array represents an <b>inline</b> PowerShell invocation
    /// (<c>-Command</c> with a script string) rather than a file-based one (<c>-File</c>). When it
    /// does, the inline script text is returned via <paramref name="script"/>.
    /// </summary>
    /// <param name="baseArgs">
    /// The shell arguments <b>excluding</b> the executable itself (e.g. <c>-NoProfile -Command</c>).
    /// </param>
    /// <param name="inlineScript">
    /// The script that will be appended after <paramref name="baseArgs"/> (ShellTool builds args and
    /// command apart), or <see langword="null"/> when the script is expected to sit inside
    /// <paramref name="baseArgs"/> (ExecTool style).
    /// </param>
    /// <param name="script">The extracted inline script when the method returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> when an inline <c>-Command</c> script was identified.</returns>
    public static bool TryGetInlineScript(
        IReadOnlyList<string> baseArgs,
        string? inlineScript,
        out string script)
    {
        script = string.Empty;

        // -File means the payload is a script *path*, not inline text - never preflight those.
        for (var i = 0; i < baseArgs.Count; i++)
        {
            if (IsFileFlag(baseArgs[i]))
            {
                return false;
            }
        }

        // Case 1: ShellTool appends the script as a separate trailing element and the base args
        // carry the -Command flag. The caller passes that trailing script via inlineScript.
        if (inlineScript is not null)
        {
            for (var i = 0; i < baseArgs.Count; i++)
            {
                if (IsCommandFlag(baseArgs[i]))
                {
                    script = inlineScript;
                    return true;
                }
            }

            return false;
        }

        // Case 2: ExecTool packs everything in one array - find -Command and take the next element.
        for (var i = 0; i < baseArgs.Count; i++)
        {
            if (IsCommandFlag(baseArgs[i]) && i + 1 < baseArgs.Count)
            {
                script = baseArgs[i + 1];
                return true;
            }
        }

        return false;
    }

    private static bool IsCommandFlag(string arg) =>
        string.Equals(arg, "-Command", StringComparison.OrdinalIgnoreCase)
        || string.Equals(arg, "-c", StringComparison.OrdinalIgnoreCase);

    private static bool IsFileFlag(string arg) =>
        string.Equals(arg, "-File", StringComparison.OrdinalIgnoreCase)
        || string.Equals(arg, "-f", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Scans an inline PowerShell script for the high-confidence syntax errors this preflight guards
    /// against. Returns <see langword="null"/> when the script parses cleanly under those rules.
    /// </summary>
    /// <param name="script">The inline script text (the value passed after <c>-Command</c>).</param>
    /// <returns>The first <see cref="PreflightError"/> found, or <see langword="null"/> when valid.</returns>
    public static PreflightError? Validate(string? script)
    {
        if (string.IsNullOrWhiteSpace(script))
        {
            return null;
        }

        var s = script;
        var n = s.Length;
        var braceDepth = 0;
        // Offset of the most recent unmatched '{' so we can point at an unbalanced-brace error.
        var lastOpenBrace = -1;

        for (var i = 0; i < n; i++)
        {
            var c = s[i];

            // Line comment: # ... to end of line (only when not glued to the previous token).
            if (c == '#' && (i == 0 || IsCommentBoundary(s[i - 1])))
            {
                while (i < n && s[i] != '\n')
                {
                    i++;
                }

                continue;
            }

            // Block comment: <# ... #>
            if (c == '<' && i + 1 < n && s[i + 1] == '#')
            {
                i += 2;
                while (i + 1 < n && !(s[i] == '#' && s[i + 1] == '>'))
                {
                    i++;
                }

                i++; // land on '>' (loop ++ moves past)
                continue;
            }

            // Here-string: @' ... '@ (literal) or @" ... "@ (expandable). Issue #2905: the body of
            // a here-string is ORDINARY TEXT - a lone ' or " inside it is not a delimiter and does
            // not open a string - so the plain quote scanners below must never see it. Modelling
            // the region is what the real parser does; without it, the single most common durable
            // write idiom on the platform (append a multi-line markdown block to a file) was
            // refused with "The string is missing the terminator" 11 times in one week.
            //
            // An opener is only a here-string when the quote is followed by end-of-line; anything
            // else (e.g. @'x') is left to the ordinary scanners, keeping this rule conservative.
            if (c == '@' && i + 1 < n && (s[i + 1] == '\'' || s[i + 1] == '"') && IsHereStringOpener(s, i + 2))
            {
                var hereError = ScanHereString(s, ref i, s[i + 1]);
                if (hereError is not null)
                {
                    return hereError;
                }

                continue;
            }

            // Single-quoted string: literal, '' escapes a quote, no interpolation.
            if (c == '\'')
            {
                i++;
                while (i < n)
                {
                    if (s[i] == '\'')
                    {
                        if (i + 1 < n && s[i + 1] == '\'')
                        {
                            i += 2;
                            continue;
                        }

                        break;
                    }

                    i++;
                }

                if (i >= n)
                {
                    return new PreflightError("The string is missing the terminator: '.", n);
                }

                continue;
            }

            // Double-quoted string: backtick escapes; ${...} still interpolates and is validated.
            if (c == '"')
            {
                var stringError = ScanDoubleQuoted(s, ref i);
                if (stringError is not null)
                {
                    return stringError;
                }

                continue;
            }

            // Bare ${...} variable reference outside any string.
            if (c == '$' && i + 1 < n && s[i + 1] == '{')
            {
                var refError = ValidateBraceVariable(s, ref i, insideString: false);
                if (refError is not null)
                {
                    return refError;
                }

                continue;
            }

            switch (c)
            {
                case '{':
                    lastOpenBrace = i;
                    braceDepth++;
                    break;
                case '}':
                    if (braceDepth == 0)
                    {
                        return new PreflightError("Unexpected token '}' in expression or statement.", i);
                    }

                    braceDepth--;
                    break;
                case '|':
                    // '||' is the pipeline-chain operator, not a pipe - skip both chars.
                    if (i + 1 < n && s[i + 1] == '|')
                    {
                        i++;
                        break;
                    }

                    if (i > 0 && s[i - 1] == '|')
                    {
                        break;
                    }

                    var pipeError = ValidatePipe(s, i);
                    if (pipeError is not null)
                    {
                        return pipeError;
                    }

                    break;
            }
        }

        if (braceDepth > 0)
        {
            return new PreflightError(
                "Missing closing '}' in statement block or type definition.",
                lastOpenBrace < 0 ? n : lastOpenBrace);
        }

        // The script is syntactically clean under the rules above.
        //
        // Issue #2757: this is where a hand-rolled "Nested quoting detected" scan used to run. It
        // flagged any single-quoted value passed to -c/-Command/--jq/-Json that contained a '"' or
        // a '$', on the theory that an outer quoting layer would consume it. That premise is wrong
        // by PowerShell's own language definition: inside a SINGLE-quoted string both characters
        // are literal - no interpolation, no early termination - so nothing can be consumed. A
        // 7-day forensic replay of every distinct command the rule rejected found 20 of 20 parsed
        // with ZERO errors under
        // [System.Management.Automation.Language.Parser]::ParseInput, and the rule was refusing the
        // platform's own documented skill-wrapper convention,
        //     pwsh -NoProfile -File scripts/<tool>.ps1 -Json '{"name":"value"}'
        //
        // The rule was DELETED rather than gated to double-quoted/unquoted contexts. Gating would
        // have kept a second, independent notion of "valid" alongside the parser-backed rules, and
        // that duplicate notion is exactly what drifted. Every surviving rule above reproduces a
        // signature the real parser also produces, so the parser stays the single source of truth
        // for what is refused. The genuinely-stacked-quoting advisory the deleted rule was reaching
        // for belongs alongside an executed result as a warning, never as a pre-execution refusal
        // (issue #2757, clause 3 of the proposed fix).
        //
        // The parser-backed rules - unterminated string, empty pipe element, missing closing brace,
        // malformed ${...} - are NOT affected and continue to refuse; they were measured at
        // ~69-100% true-positive over the same corpus.
        //
        // The script is clean under the legacy hand-rolled rules above - but those rules only ever
        // covered a handful of signatures. Hand it to the AUTHORITATIVE parser (issue #3576).
        return ParseWithRealParser(s);
    }

    /// <summary>
    /// Runs the script through <c>System.Management.Automation.Language.Parser.ParseInput</c> - the
    /// same parser the shell itself uses - and converts the first genuine <c>ParseError</c> into a
    /// <see cref="PreflightError"/>. Returns <see langword="null"/> when the script parses cleanly.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why the real parser (issue #3576).</b> Replaying every command that produced a runtime
    /// <c>ParserError</c> over a 7-day window found 102 commands, of which <b>85 fail to parse</b>
    /// deterministically - every one catchable before a process launch, and every one admitted by the
    /// hand-rolled scanner. 47 were a single idiom (<c>foreach(...){...} | cmd</c>), 20 were an
    /// unclosed <c>(</c>, and the rest spread across here-string headers, Unicode escapes,
    /// <c>foreach</c> without <c>in</c>, redirection without a file spec, array indexing and
    /// subexpressions. That long tail is what a real grammar gets for free and what a scanner can only
    /// chase one rule at a time.
    /// </para>
    /// <para>
    /// <b>Why this cannot re-introduce #2757/#2905/#3566.</b> Those were false positives: heuristics
    /// refusing text the real parser accepts. Here the parser <i>is</i> the oracle, so by construction
    /// nothing that parses can be refused. The gate <b>fails open</b> on everything that is not a
    /// definite <c>ParseError</c> - including any exception from the parser itself, which is treated as
    /// "cannot prove it is broken" and therefore allowed through.
    /// </para>
    /// <para>
    /// The legacy rules deliberately run FIRST and are retained: they reproduce the same messages this
    /// parser produces, and keeping them ahead of the parse preserves their existing offsets and
    /// derived diagnostics (e.g. the #2417 "operand before '|' is empty" cause explanation). This
    /// method only ever ADDS refusals the scanner missed.
    /// </para>
    /// </remarks>
    private static PreflightError? ParseWithRealParser(string script)
    {
        ParseError[] errors;
        try
        {
            Parser.ParseInput(script, out _, out errors);
        }
        catch
        {
            // Fail open: an unexpected parser failure is not evidence that the agent's script is bad.
            return null;
        }

        if (errors is null || errors.Length == 0)
        {
            return null;
        }

        var first = errors[0];
        var offset = first.Extent?.StartOffset ?? 0;
        if (offset < 0 || offset > script.Length)
        {
            offset = 0;
        }

        // Normalise the parser's embedded newlines so the rejection stays a single readable line.
        var message = first.Message.Replace("\r\n", " ").Replace("\n", " ").Trim();

        return new PreflightError(message, offset, DescribeRemediation(script, first));
    }

    /// <summary>
    /// The named correction for the single most common shape in the #3576 corpus - 47 of 85 weekly
    /// failures. <c>foreach</c>, <c>if</c>, <c>switch</c>, <c>while</c>, <c>for</c> and <c>do</c> are
    /// <b>statements</b>, not expressions, so they cannot be a pipeline source; PowerShell reports the
    /// attempt as <c>An empty pipe element is not allowed.</c>, which names the symptom and not the
    /// cause. Wrapping the statement in a subexpression <c>$(...)</c> makes it a pipeline source.
    /// </summary>
    public const string StatementPipeRemediation =
        "foreach/if/switch/while are STATEMENTS and cannot be piped from directly - PowerShell reports "
        + "that as an empty pipe element. Wrap the statement in a subexpression so it becomes a pipeline "
        + "source: $(foreach ($x in $items) { ... }) | <cmd>.";

    // Returns a shape-specific correction when the error matches a named remediation, else null so
    // the generic file-based hint applies.
    private static string? DescribeRemediation(string script, ParseError error)
    {
        if (!string.Equals(error.ErrorId, "EmptyPipeElement", StringComparison.Ordinal))
        {
            return null;
        }

        // Only claim the statement-pipe diagnosis when the text immediately before the offending pipe
        // really is the close of a statement block: '}' possibly preceded by whitespace. Anything else
        // (a genuinely trailing '|', an empty operand) keeps the generic hint.
        var offset = error.Extent?.StartOffset ?? -1;
        if (offset <= 0 || offset > script.Length)
        {
            return null;
        }

        var i = offset - 1;
        while (i >= 0 && char.IsWhiteSpace(script[i]))
        {
            i--;
        }

        return i >= 0 && script[i] == '}' ? StatementPipeRemediation : null;
    }

    /// <summary>
    /// Runs <see cref="Validate"/> and, on failure, throws an <see cref="ArgumentException"/> carrying
    /// the parser-style message, the exact offending extent, and the file-based remediation hint. Does
    /// nothing when the script is valid, so legitimate one-liners execute untouched.
    /// </summary>
    /// <param name="script">The inline script to preflight.</param>
    /// <exception cref="ArgumentException">The script contains a rejected syntax error.</exception>
    public static void ThrowIfInvalid(string? script)
    {
        // Issue #2908: a nested `pwsh -Command "..."` is refused before the syntax rules run,
        // because the resulting parser errors are downstream symptoms of outer interpolation and
        // point the agent at the wrong problem.
        var nested = DetectNestedPowerShellInterpolation(script);
        if (nested is not null)
        {
            throw new ArgumentException(nested.Message);
        }

        var error = Validate(script);
        if (error is null)
        {
            return;
        }

        throw new ArgumentException(BuildRejectionMessage(error, script!));
    }

    /// <summary>
    /// Formats the rejection message: the parser-style problem, the offending extent (a short snippet
    /// around the offset), and the remediation hint.
    /// </summary>
    public static string BuildRejectionMessage(PreflightError error, string script)
    {
        var extent = DescribeExtent(script, error.Offset);
        // A shape-specific correction REPLACES the generic file-based hint (#3576): telling an agent to
        // move `foreach(...){...} | cmd` into a tmp/*.ps1 file does not fix it - the syntax is invalid
        // wherever it lives - and burns another turn discovering that.
        var remediation = error.Remediation ?? $"To fix this, {RemediationHint}";
        return $"PowerShell preflight rejected the inline -Command script before execution: "
               + $"{error.Message} (at offset {error.Offset}{extent}) "
               + remediation;
    }

    /// <summary>
    /// Detects the single most expensive mechanical mistake agents make with this tool (issue #2908):
    /// spawning a <b>second</b> PowerShell from a shell that already runs PowerShell, with the script
    /// passed to <c>-Command</c>/<c>-c</c> inside <b>double</b> quotes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Double quotes make that argument an interpolating string in the <b>outer</b> interpreter, so
    /// every <c>$name</c> is substituted (usually with nothing) and every <c>@{}</c> hashtable literal
    /// is mangled before the residue reaches the child process. The child then reports something like
    /// <c>The term '=@' is not recognized</c> — a message that says nothing about quoting, which is why
    /// agents historically escalated to <i>more</i> escaping and failed again. Measured cost before this
    /// rule: 18 failures per week across 5 agents, none of which needed the nested <c>pwsh</c> at all.
    /// </para>
    /// <para>
    /// The rule is deliberately narrow so it cannot become the kind of over-reaching heuristic that
    /// was deleted in issue #2757. It fires only when <b>all</b> of the following hold:
    /// a top-level token names <c>pwsh</c>/<c>powershell</c>; that invocation uses <c>-Command</c>/<c>-c</c>
    /// rather than <c>-File</c>; the argument that follows is <b>double</b>-quoted; and it actually
    /// contains an interpolation trigger (<c>$</c> or <c>@{</c>). Single-quoted arguments, <c>-File</c>
    /// invocations, literal-only double-quoted arguments, and text that merely mentions the shape
    /// inside a quoted string all pass through untouched.
    /// </para>
    /// </remarks>
    /// <param name="script">The command text submitted to the shell tool.</param>
    /// <returns>A <see cref="PreflightError"/> naming the cause and the fix, or <see langword="null"/>.</returns>
    public static PreflightError? DetectNestedPowerShellInterpolation(string? script)
    {
        if (string.IsNullOrWhiteSpace(script))
        {
            return null;
        }

        var tokens = TokenizeTopLevel(script!);
        for (var i = 0; i < tokens.Count; i++)
        {
            // Only an UNQUOTED token can be an executable being invoked; a quoted one is data.
            if (tokens[i].Quote != '\0' || !IsPowerShellExecutable(tokens[i].Text))
            {
                continue;
            }

            for (var j = i + 1; j < tokens.Count; j++)
            {
                var flag = tokens[j];
                if (flag.Quote != '\0')
                {
                    continue;
                }

                // -File is the documented correct pattern: the payload is a path, never inline
                // text, so no interpolation hazard exists (acceptance criterion 3).
                if (IsFileFlag(flag.Text))
                {
                    break;
                }

                if (!IsCommandFlag(flag.Text) || j + 1 >= tokens.Count)
                {
                    continue;
                }

                var payload = tokens[j + 1];
                // Single-quoted payloads are literal in the outer interpreter — nothing is
                // consumed, so they are correct and must run (acceptance criterion 2).
                if (payload.Quote != '"')
                {
                    break;
                }

                if (!payload.Text.Contains('$') && !payload.Text.Contains("@{", StringComparison.Ordinal))
                {
                    break;
                }

                return new PreflightError(NestedInterpolationMessage, payload.Offset);
            }
        }

        return null;
    }

    /// <summary>
    /// The rejection text for a nested double-quoted <c>-Command</c> invocation. It names the actual
    /// cause (outer interpolation) rather than the downstream parser symptom, and gives both viable
    /// fixes — single-quote the argument, or drop the redundant nesting entirely.
    /// </summary>
    public const string NestedInterpolationMessage =
        "PowerShell preflight rejected a nested pwsh invocation before execution: the -Command argument is "
        + "DOUBLE-quoted and contains '$' or '@{', so the outer shell will expand every $variable and mangle "
        + "every @{} hashtable literal before the child pwsh ever sees the script. The child then fails with a "
        + "misleading message such as \"The term '=@' is not recognized\". To fix this, single-quote the -Command "
        + "argument, or simply drop the nested pwsh entirely - this shell already runs PowerShell, so the script "
        + "can be submitted directly.";

    // A top-level token: its text (quotes stripped), the quote character that delimited it ('\0'
    // when unquoted), and the offset at which it began.
    private readonly record struct ShellToken(string Text, char Quote, int Offset);

    // Splits a command line into whitespace-separated top-level tokens, keeping quoted regions
    // intact so that a mention of `pwsh -Command "..."` INSIDE a string is data, not an invocation.
    private static List<ShellToken> TokenizeTopLevel(string s)
    {
        var tokens = new List<ShellToken>();
        var n = s.Length;
        var i = 0;
        while (i < n)
        {
            while (i < n && char.IsWhiteSpace(s[i]))
            {
                i++;
            }

            if (i >= n)
            {
                break;
            }

            var start = i;
            if (s[i] is '"' or '\'')
            {
                var quote = s[i];
                i++;
                var contentStart = i;
                while (i < n && s[i] != quote)
                {
                    i++;
                }

                tokens.Add(new ShellToken(s[contentStart..Math.Min(i, n)], quote, start));
                i++; // step past the closing quote (or off the end when unterminated)
                continue;
            }

            while (i < n && !char.IsWhiteSpace(s[i]))
            {
                i++;
            }

            tokens.Add(new ShellToken(s[start..i], '\0', start));
        }

        return tokens;
    }

    private static string DescribeExtent(string script, int offset)
    {
        if (offset < 0 || offset >= script.Length)
        {
            return string.Empty;
        }

        var start = Math.Max(0, offset - 12);
        var end = Math.Min(script.Length, offset + 12);
        var snippet = script.Substring(start, end - start).Replace("\r", " ").Replace("\n", " ");
        return $", near: \u2026{snippet}\u2026";
    }

    // A here-string opener requires end-of-line immediately after the quote (trailing horizontal
    // whitespace is tolerated by the real parser, a trailing token is not).
    private static bool IsHereStringOpener(string s, int afterQuote)
    {
        var k = afterQuote;
        while (k < s.Length && (s[k] == ' ' || s[k] == '\t' || s[k] == '\r'))
        {
            k++;
        }

        return k < s.Length && s[k] == '\n';
    }

    // Consumes a here-string beginning at s[i] == '@'. The body ends at the first line whose first
    // two characters are the matching quote followed by '@'. On success i lands on that '@' so the
    // caller's loop increment resumes after the terminator. An unterminated here-string is a real
    // parser error and is still refused, with the parser's own "'@" / '"@' terminator wording.
    private static PreflightError? ScanHereString(string s, ref int i, char quote)
    {
        var n = s.Length;
        var start = i;
        var lineStart = s.IndexOf('\n', i);
        if (lineStart >= 0)
        {
            lineStart++;
            while (lineStart < n)
            {
                if (s[lineStart] == quote && lineStart + 1 < n && s[lineStart + 1] == '@')
                {
                    i = lineStart + 1;
                    return null;
                }

                var next = s.IndexOf('\n', lineStart);
                if (next < 0)
                {
                    break;
                }

                lineStart = next + 1;
            }
        }

        i = n;
        return new PreflightError($"The string is missing the terminator: {quote}@.", start);
    }

    // A '#' only begins a comment when it starts a token (preceded by whitespace, start, or a
    // separator) - otherwise it is part of a name/argument like "Get-Item#name".
    private static bool IsCommentBoundary(char prev) =>
        char.IsWhiteSpace(prev) || prev is '(' or '{' or ';' or '|' or '=' or ',' or '&';

    // Handles a double-quoted string starting at s[i] == '"'. Advances i to the closing quote.
    // Validates any ${...} interpolations; ignores everything else (pipes, braces are literal text).
    private static PreflightError? ScanDoubleQuoted(string s, ref int i)
    {
        var n = s.Length;
        i++; // move past opening quote
        while (i < n)
        {
            var c = s[i];
            if (c == '`')
            {
                i += 2; // backtick escapes the next char
                continue;
            }

            if (c == '"')
            {
                if (i + 1 < n && s[i + 1] == '"')
                {
                    i += 2; // "" escapes a quote inside a double-quoted string
                    continue;
                }

                return null; // closed cleanly; caller's loop ++ moves past
            }

            if (c == '$' && i + 1 < n && s[i + 1] == '{')
            {
                var refError = ValidateBraceVariable(s, ref i, insideString: true);
                if (refError is not null)
                {
                    return refError;
                }

                continue;
            }

            i++;
        }

        return new PreflightError("The string is missing the terminator: \".", n);
    }

    // Validates a ${...} reference beginning at s[i] == '$' (s[i+1] == '{'). On return, i points at
    // the closing '}'. When insideString is false, a ${var} immediately followed by ':' is flagged
    // the way the real parser does ("Unexpected token ':'"); inside a string that ':' is literal text.
    private static PreflightError? ValidateBraceVariable(string s, ref int i, bool insideString)
    {
        var n = s.Length;
        var start = i;
        var contentStart = i + 2;
        var j = contentStart;
        while (j < n && s[j] != '}')
        {
            j++;
        }

        if (j >= n)
        {
            i = n - 1;
            return new PreflightError(
                "Variable reference is not valid. Missing closing '}' in the variable name.",
                start);
        }

        var inner = s.Substring(contentStart, j - contentStart);
        var colon = inner.IndexOf(':');
        var invalid = colon >= 0
            ? colon == 0 || colon == inner.Length - 1 // ${:x} or ${x:} - empty namespace or name
            : inner.Length == 0;                       // ${} - empty reference

        if (invalid)
        {
            i = j;
            return new PreflightError(
                "Variable reference is not valid. The variable name is missing.",
                start);
        }

        // Bare ${var} directly followed by ':' is an unexpected token in expression position.
        if (!insideString && j + 1 < n && s[j + 1] == ':')
        {
            i = j + 1;
            return new PreflightError("Unexpected token ':' in expression or statement.", j + 1);
        }

        i = j;
        return null;
    }

    // Checks a top-level '|' (already known not to be part of '||') for empty pipe elements.
    private static PreflightError? ValidatePipe(string s, int index)
    {
        // Trailing: skip all whitespace after '|'. End-of-input or an immediate terminator means
        // there is no command to pipe into. Whitespace that resolves to a real token (including a
        // multi-line continuation) is valid, so only flag when nothing meaningful follows.
        var after = index + 1;
        while (after < s.Length && char.IsWhiteSpace(s[after]))
        {
            after++;
        }

        if (after >= s.Length || s[after] is '|' or ';' or ')' or '}')
        {
            return new PreflightError("An empty pipe element is not allowed.", index);
        }

        // Leading: skip whitespace before '|'. Start-of-input or a preceding separator means there
        // is nothing feeding the pipe.
        var before = index - 1;
        while (before >= 0 && char.IsWhiteSpace(s[before]))
        {
            before--;
        }

        if (before < 0 || s[before] is '|' or ';' or '{' or '(' or '&' or '=')
        {
            // Derived symptom (issue #2417): an assignment or parameter left with an EMPTY operand
            // right where a $variable should have been is the fingerprint of an outer quoting layer
            // having eaten that variable. Say so, rather than leaving the agent with the bare
            // parser message that points at the pipe instead of the real cause.
            if (before >= 0 && s[before] == '=' && before < index - 1)
            {
                return new PreflightError(
                    "An empty pipe element is not allowed. The operand before '|' is empty, which "
                    + "usually means a $variable was consumed by an outer quoting layer before "
                    + "PowerShell parsed the script.",
                    index);
            }

            return new PreflightError("An empty pipe element is not allowed.", index);
        }

        return null;
    }
}
