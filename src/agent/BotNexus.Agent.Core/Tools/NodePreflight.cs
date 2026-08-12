namespace BotNexus.Agent.Core.Tools;

/// <summary>
/// Preflight validator for inline JavaScript passed to <c>node</c>/<c>nodejs</c> via <c>-e</c> /
/// <c>--eval</c>. It is the Node sibling of <see cref="PythonPreflight"/> (issue #2762, AC4): it
/// catches the syntax mistakes agents most commonly emit when a one-liner is squeezed through several
/// quoting layers - unterminated string/template literals and unbalanced brackets - <b>before</b> the
/// command is handed to a process, so the agent gets an immediate, actionable rejection instead of a
/// late runtime <c>SyntaxError</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a hand-rolled scanner?</b> Same posture as <see cref="PythonPreflight"/> and
/// <see cref="PowerShellPreflight"/>: no JS engine, no Jint, and <b>no shelling out to
/// <c>node --check</c></b>. <c>BotNexus.Extensions.ExecTool</c> loads into an isolated
/// <c>AssemblyLoadContext</c> and must ship its whole managed closure (issue #2184), and spawning a
/// process to decide whether to spawn a process defeats the point.
/// </para>
/// <para>
/// The scanner is deliberately conservative: when in doubt it reports <b>valid</b>, because a false
/// rejection breaks a working agent command. It only rejects high-confidence signatures:
/// <list type="bullet">
///   <item>unterminated single-line <c>'</c>/<c>"</c> string literal;</item>
///   <item>unterminated <c>`</c> template literal (which legitimately spans lines, so only EOF ends it);</item>
///   <item><c>'(' was never closed</c> - an unbalanced opening bracket;</item>
///   <item><c>Unexpected token ')'</c> - a closing bracket with no opener.</item>
/// </list>
/// Line (<c>//</c>) and block (<c>/* */</c>) comments and regular-expression literals are understood,
/// so <c>'x'.replace(/['"]/g, '')</c> is scanned with the right rules rather than tripping the string
/// scanner on the quotes inside the character class.
/// </para>
/// </remarks>
public static class NodePreflight
{
    /// <summary>
    /// The remediation hint appended to every rejection. Steers the agent away from fragile inline
    /// <c>-e</c> scripts toward the robust file-based invocation.
    /// </summary>
    public const string RemediationHint =
        "write a tmp/*.js file and invoke node tmp/script.js instead of passing the script inline via -e.";

    /// <summary>
    /// Describes a single syntax problem found in an inline Node script: a human-readable message
    /// mirroring V8, plus the character offset of the offending extent.
    /// </summary>
    /// <param name="Message">V8-style description of the problem.</param>
    /// <param name="Offset">Zero-based character offset of the offending extent within the script.</param>
    public sealed record PreflightError(string Message, int Offset);

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="executable"/> names the Node runtime
    /// (<c>node</c> or <c>nodejs</c>), ignoring any directory, <c>.exe</c> extension, or case.
    /// Deliberately excludes near-neighbours such as <c>nodemon</c> and <c>npx</c>, whose argument
    /// grammar is not Node's.
    /// </summary>
    public static bool IsNodeExecutable(string? executable)
    {
        if (string.IsNullOrWhiteSpace(executable))
        {
            return false;
        }

        // Normalize both Windows and POSIX separators so a Windows-style path classifies correctly
        // even when this runs on a Linux host.
        var trimmed = executable.Trim();
        var lastSeparator = trimmed.LastIndexOfAny(new[] { '/', '\\' });
        var name = lastSeparator >= 0 ? trimmed[(lastSeparator + 1)..] : trimmed;

        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^4];
        }

        return string.Equals(name, "node", StringComparison.OrdinalIgnoreCase)
               || string.Equals(name, "nodejs", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Determines whether an argument array represents an <b>inline</b> Node invocation (<c>-e</c> /
    /// <c>--eval</c> with a script string) rather than a file-based one. When it does, the inline
    /// script text is returned via <paramref name="script"/>.
    /// </summary>
    /// <param name="baseArgs">The runtime arguments <b>excluding</b> the executable itself.</param>
    /// <param name="inlineScript">
    /// The script that will be appended after <paramref name="baseArgs"/> (ShellTool builds args and
    /// command apart), or <see langword="null"/> when the script sits inside
    /// <paramref name="baseArgs"/> (ExecTool style).
    /// </param>
    /// <param name="script">The extracted inline script when the method returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> when an inline <c>-e</c> script was identified.</returns>
    public static bool TryGetInlineScript(
        IReadOnlyList<string> baseArgs,
        string? inlineScript,
        out string script)
    {
        script = string.Empty;

        // Case 1: ShellTool appends the script as a separate trailing element.
        if (inlineScript is not null)
        {
            for (var i = 0; i < baseArgs.Count; i++)
            {
                if (IsInlineCodeFlag(baseArgs[i]))
                {
                    script = inlineScript;
                    return true;
                }
            }

            return false;
        }

        // Case 2: ExecTool packs everything in one array - find -e/--eval and take the payload,
        // which is either the next element or attached with '=' (node accepts --eval=CODE).
        for (var i = 0; i < baseArgs.Count; i++)
        {
            var arg = baseArgs[i];
            if (TryGetAttachedValue(arg, out var attached))
            {
                script = attached;
                return true;
            }

            if (IsInlineCodeFlag(arg) && i + 1 < baseArgs.Count)
            {
                script = baseArgs[i + 1];
                return true;
            }
        }

        return false;
    }

    // Node's inline-code flags. `-p`/`--print` also evaluate, but they are left alone here to keep
    // the high-confidence surface narrow; only the flags the issue names are recognised.
    private static bool IsInlineCodeFlag(string arg) =>
        string.Equals(arg, "-e", StringComparison.Ordinal)
        || string.Equals(arg, "--eval", StringComparison.Ordinal);

    private static bool TryGetAttachedValue(string arg, out string value)
    {
        value = string.Empty;
        const string prefix = "--eval=";
        if (!arg.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        value = arg[prefix.Length..];
        return true;
    }

    /// <summary>
    /// Scans an inline Node script for the high-confidence syntax errors this preflight guards
    /// against. Returns <see langword="null"/> when the script scans cleanly under those rules.
    /// </summary>
    /// <param name="script">The inline script text (the value passed after <c>-e</c>).</param>
    /// <returns>The first <see cref="PreflightError"/> found, or <see langword="null"/> when valid.</returns>
    public static PreflightError? Validate(string? script)
    {
        if (string.IsNullOrWhiteSpace(script))
        {
            return null;
        }

        var s = script;
        var n = s.Length;

        // Stack of open brackets so an unbalanced opener can be reported at its own offset.
        var open = new Stack<(char Bracket, int Offset)>();

        // Tracks the last significant character so a '/' can be classified as division vs the start
        // of a regular-expression literal. Regex may only start where an operand is expected.
        var lastSignificant = '\0';

        for (var i = 0; i < n; i++)
        {
            var c = s[i];

            if (char.IsWhiteSpace(c))
            {
                continue;
            }

            if (c == '/' && i + 1 < n && s[i + 1] == '/')
            {
                while (i < n && s[i] != '\n')
                {
                    i++;
                }

                continue;
            }

            if (c == '/' && i + 1 < n && s[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < n && !(s[i] == '*' && s[i + 1] == '/'))
                {
                    i++;
                }

                i = Math.Min(i + 1, n - 1);
                continue;
            }

            if (c == '/' && RegexCanStartAfter(lastSignificant))
            {
                // A regex literal that does not close on its line is far more likely to be a
                // division expression the scanner misread, so an unclosed scan is treated as valid.
                if (TryScanRegex(s, ref i))
                {
                    lastSignificant = '/';
                    continue;
                }

                return null;
            }

            if (c is '\'' or '"')
            {
                var stringError = ScanString(s, ref i);
                if (stringError is not null)
                {
                    return stringError;
                }

                lastSignificant = '\'';
                continue;
            }

            if (c == '`')
            {
                var templateError = ScanTemplate(s, ref i);
                if (templateError is not null)
                {
                    return templateError;
                }

                lastSignificant = '`';
                continue;
            }

            switch (c)
            {
                case '(':
                case '[':
                case '{':
                    open.Push((c, i));
                    break;
                case ')':
                case ']':
                case '}':
                    var expected = c switch { ')' => '(', ']' => '[', _ => '{' };
                    if (open.Count == 0)
                    {
                        return new PreflightError($"SyntaxError: Unexpected token '{c}'", i);
                    }

                    var top = open.Pop();
                    if (top.Bracket != expected)
                    {
                        return new PreflightError(
                            $"SyntaxError: Unexpected token '{c}' does not match opening '{top.Bracket}'",
                            i);
                    }

                    break;
            }

            lastSignificant = c;
        }

        if (open.Count > 0)
        {
            var unclosed = open.Pop();
            return new PreflightError(
                $"SyntaxError: Unexpected end of input ('{unclosed.Bracket}' was never closed)",
                unclosed.Offset);
        }

        return null;
    }

    /// <summary>
    /// Runs <see cref="Validate"/> and, on failure, throws an <see cref="ArgumentException"/> carrying
    /// the V8-style message, the exact offending extent, and the file-based remediation hint. Does
    /// nothing when the script is valid, so legitimate one-liners execute untouched.
    /// </summary>
    /// <param name="script">The inline script to preflight.</param>
    /// <exception cref="ArgumentException">The script contains a rejected syntax error.</exception>
    public static void ThrowIfInvalid(string? script)
    {
        var error = Validate(script);
        if (error is null)
        {
            return;
        }

        throw new ArgumentException(BuildRejectionMessage(error, script!));
    }

    /// <summary>
    /// Formats the rejection message: the V8-style problem, the offending extent (a short snippet
    /// around the offset), and the remediation hint.
    /// </summary>
    public static string BuildRejectionMessage(PreflightError error, string script)
    {
        var extent = DescribeExtent(script, error.Offset);
        return "Node preflight rejected the inline -e script before execution: "
               + $"{error.Message} (at offset {error.Offset}{extent}) "
               + $"To fix this, {RemediationHint}";
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

    // A '/' begins a regex literal only where an operand is expected - i.e. at the start of input or
    // after an operator/punctuator. After an identifier, number, or closing bracket it is division.
    private static bool RegexCanStartAfter(char lastSignificant) =>
        lastSignificant == '\0'
        || lastSignificant is '(' or ',' or '=' or ':' or '[' or '!' or '&' or '|' or '?'
            or '{' or '}' or ';' or '+' or '-' or '*' or '%' or '^' or '~' or '<' or '>';

    // Scans a regex literal starting at s[i] == '/'. Returns true (and advances i to the closing
    // '/') when one terminates on the same line; false when it does not, which the caller treats as
    // "probably division - stop scanning" rather than as an error.
    private static bool TryScanRegex(string s, ref int i)
    {
        var n = s.Length;
        var j = i + 1;
        var inClass = false;

        while (j < n)
        {
            var c = s[j];
            if (c is '\n' or '\r')
            {
                return false;
            }

            if (c == '\\')
            {
                j += 2;
                continue;
            }

            if (c == '[')
            {
                inClass = true;
            }
            else if (c == ']')
            {
                inClass = false;
            }
            else if (c == '/' && !inClass)
            {
                i = j; // land on the closing '/'; the caller's loop ++ moves past
                return true;
            }

            j++;
        }

        return false;
    }

    // Handles a single-line string literal starting at s[i] (a quote char). Advances i to the
    // closing quote.
    private static PreflightError? ScanString(string s, ref int i)
    {
        var n = s.Length;
        var quote = s[i];
        var start = i;

        i++;
        while (i < n)
        {
            var c = s[i];

            // A backslash escapes the next character, including a line continuation.
            if (c == '\\')
            {
                i += 2;
                continue;
            }

            if (c == quote)
            {
                return null; // closed cleanly; caller's loop ++ moves past
            }

            if (c is '\n' or '\r')
            {
                return new PreflightError(
                    "SyntaxError: Invalid or unexpected token (unterminated string literal)",
                    start);
            }

            i++;
        }

        return new PreflightError(
            "SyntaxError: Invalid or unexpected token (unterminated string literal)",
            start);
    }

    // Handles a template literal starting at s[i] == '`'. Templates legitimately span lines, so only
    // EOF terminates the scan unsuccessfully. Interpolation bodies are skipped wholesale rather than
    // recursed into, keeping the scanner conservative.
    private static PreflightError? ScanTemplate(string s, ref int i)
    {
        var n = s.Length;
        var start = i;

        i++;
        while (i < n)
        {
            var c = s[i];

            if (c == '\\')
            {
                i += 2;
                continue;
            }

            if (c == '`')
            {
                return null;
            }

            if (c == '$' && i + 1 < n && s[i + 1] == '{')
            {
                var depth = 1;
                i += 2;
                while (i < n && depth > 0)
                {
                    if (s[i] == '{')
                    {
                        depth++;
                    }
                    else if (s[i] == '}')
                    {
                        depth--;
                    }

                    i++;
                }

                continue;
            }

            i++;
        }

        return new PreflightError("SyntaxError: Unterminated template literal", start);
    }
}
