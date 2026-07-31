namespace BotNexus.Agent.Core.Tools;

/// <summary>
/// Preflight validator for inline Python scripts passed to <c>python</c>/<c>python3</c>/<c>py</c> via
/// <c>-c</c>. It catches the syntax mistakes that agents most commonly emit when a one-liner is
/// squeezed through several quoting layers - unterminated string literals and unbalanced brackets -
/// <b>before</b> the command is handed to an interpreter process, so the agent gets an immediate,
/// actionable rejection instead of a late runtime <c>SyntaxError</c> (see issue #2417).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a hand-rolled scanner?</b> Same posture as <see cref="PowerShellPreflight"/>: no
/// Python.NET, no embedded interpreter, and <b>no shelling out to <c>python -m py_compile</c></b>.
/// <c>BotNexus.Extensions.ExecTool</c> loads into an isolated <c>AssemblyLoadContext</c> and must
/// ship its whole managed closure (issue #2184), and spawning a process to decide whether to spawn a
/// process defeats the point. A small quote/comment-aware scanner reproduces the observed error
/// signature - <c>SyntaxError: unterminated string literal</c> - at zero dependency cost.
/// </para>
/// <para>
/// The scanner is deliberately conservative: when in doubt it reports <b>valid</b>, because a false
/// rejection breaks a working agent command. It only rejects high-confidence signatures:
/// <list type="bullet">
///   <item><c>unterminated string literal</c> - a single-line quote closed by a newline or EOF.</item>
///   <item><c>unterminated triple-quoted string literal</c> - a <c>'''</c>/<c>"""</c> run to EOF.</item>
///   <item><c>'(' was never closed</c> - an unbalanced opening bracket.</item>
///   <item><c>unmatched ')'</c> - a closing bracket with no opener.</item>
/// </list>
/// Raw (<c>r</c>), formatted (<c>f</c>) and byte (<c>b</c>) string prefixes are understood so that
/// <c>print(r'C:\path\')</c>-style literals are scanned with the right escape rules.
/// </para>
/// </remarks>
public static class PythonPreflight
{
    /// <summary>
    /// The remediation hint appended to every rejection. Steers the agent away from fragile inline
    /// <c>-c</c> scripts toward the robust file-based invocation.
    /// </summary>
    public const string RemediationHint =
        "write a tmp/*.py file and invoke python -X utf8 tmp/script.py instead of passing the script inline via -c.";

    /// <summary>
    /// Describes a single syntax problem found in an inline Python script: a human-readable message
    /// mirroring CPython, plus the character offset of the offending extent.
    /// </summary>
    /// <param name="Message">CPython-style description of the problem.</param>
    /// <param name="Offset">Zero-based character offset of the offending extent within the script.</param>
    public sealed record PreflightError(string Message, int Offset);

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="executable"/> names a Python interpreter
    /// (<c>python</c>, <c>python3</c>, <c>python3.12</c> or the Windows launcher <c>py</c>), ignoring
    /// any directory, <c>.exe</c> extension, or case.
    /// </summary>
    public static bool IsPythonExecutable(string? executable)
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

        // Strip only a trailing ".exe" - stripping any extension would mangle "python3.12".
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^4];
        }

        if (string.Equals(name, "py", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!name.StartsWith("python", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Everything after "python" must be a version suffix ("", "3", "3.12") - never "pythonic".
        var suffix = name["python".Length..];
        return suffix.All(ch => char.IsDigit(ch) || ch == '.');
    }

    /// <summary>
    /// Determines whether an argument array represents an <b>inline</b> Python invocation (<c>-c</c>
    /// with a script string) rather than a file- or module-based one. When it does, the inline script
    /// text is returned via <paramref name="script"/>.
    /// </summary>
    /// <param name="baseArgs">The interpreter arguments <b>excluding</b> the executable itself.</param>
    /// <param name="inlineScript">
    /// The script that will be appended after <paramref name="baseArgs"/> (ShellTool builds args and
    /// command apart), or <see langword="null"/> when the script sits inside
    /// <paramref name="baseArgs"/> (ExecTool style).
    /// </param>
    /// <param name="script">The extracted inline script when the method returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> when an inline <c>-c</c> script was identified.</returns>
    public static bool TryGetInlineScript(
        IReadOnlyList<string> baseArgs,
        string? inlineScript,
        out string script)
    {
        script = string.Empty;

        // -m means the payload is a module name, not inline code - never preflight those.
        for (var i = 0; i < baseArgs.Count; i++)
        {
            if (string.Equals(baseArgs[i], "-m", StringComparison.Ordinal))
            {
                return false;
            }
        }

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

        // Case 2: ExecTool packs everything in one array - find -c and take the next element.
        for (var i = 0; i < baseArgs.Count; i++)
        {
            if (IsInlineCodeFlag(baseArgs[i]) && i + 1 < baseArgs.Count)
            {
                script = baseArgs[i + 1];
                return true;
            }
        }

        return false;
    }

    // Python's inline-code flag is exactly "-c" (case sensitive; "-C" is not a thing).
    private static bool IsInlineCodeFlag(string arg) => string.Equals(arg, "-c", StringComparison.Ordinal);

    /// <summary>
    /// Scans an inline Python script for the high-confidence syntax errors this preflight guards
    /// against. Returns <see langword="null"/> when the script scans cleanly under those rules.
    /// </summary>
    /// <param name="script">The inline script text (the value passed after <c>-c</c>).</param>
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

        for (var i = 0; i < n; i++)
        {
            var c = s[i];

            // Line comment: # ... to end of line. Only reachable outside strings.
            if (c == '#')
            {
                while (i < n && s[i] != '\n')
                {
                    i++;
                }

                continue;
            }

            if (c is '\'' or '"')
            {
                // A quote may carry a string prefix (r/f/b/u, in any order/case) immediately before it.
                var isRaw = HasRawPrefix(s, i);
                var stringError = ScanString(s, ref i, isRaw);
                if (stringError is not null)
                {
                    return stringError;
                }

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
                        return new PreflightError($"SyntaxError: unmatched '{c}'", i);
                    }

                    var top = open.Pop();
                    if (top.Bracket != expected)
                    {
                        return new PreflightError(
                            $"SyntaxError: closing parenthesis '{c}' does not match opening parenthesis '{top.Bracket}'",
                            i);
                    }

                    break;
            }
        }

        if (open.Count > 0)
        {
            var unclosed = open.Pop();
            return new PreflightError($"SyntaxError: '{unclosed.Bracket}' was never closed", unclosed.Offset);
        }

        return null;
    }

    /// <summary>
    /// Runs <see cref="Validate"/> and, on failure, throws an <see cref="ArgumentException"/> carrying
    /// the CPython-style message, the exact offending extent, and the file-based remediation hint. Does
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
    /// Formats the rejection message: the CPython-style problem, the offending extent (a short snippet
    /// around the offset), and the remediation hint.
    /// </summary>
    public static string BuildRejectionMessage(PreflightError error, string script)
    {
        var extent = DescribeExtent(script, error.Offset);
        return "Python preflight rejected the inline -c script before execution: "
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

    // True when the quote at index i is preceded by a raw-string prefix (r/R), possibly combined
    // with f/b/u as in rb'...' or fr'...'.
    private static bool HasRawPrefix(string s, int i)
    {
        var j = i - 1;
        var sawRaw = false;
        var seen = 0;
        while (j >= 0 && seen < 2)
        {
            var p = char.ToLowerInvariant(s[j]);
            if (p is 'r')
            {
                sawRaw = true;
            }
            else if (p is not ('f' or 'b' or 'u'))
            {
                break;
            }

            seen++;
            j--;
        }

        // The prefix must start a token - otherwise "var" followed by a quote is not a prefix.
        if (sawRaw && j >= 0 && (char.IsLetterOrDigit(s[j]) || s[j] == '_'))
        {
            return false;
        }

        return sawRaw;
    }

    // Handles a string literal starting at s[i] (a quote char). Advances i to the closing quote.
    private static PreflightError? ScanString(string s, ref int i, bool isRaw)
    {
        var n = s.Length;
        var quote = s[i];
        var start = i;
        var isTriple = i + 2 < n && s[i + 1] == quote && s[i + 2] == quote;

        if (isTriple)
        {
            i += 3;
            while (i < n)
            {
                if (s[i] == '\\' && !isRaw)
                {
                    i += 2;
                    continue;
                }

                if (s[i] == quote && i + 2 < n + 1 && i + 2 < n && s[i + 1] == quote && s[i + 2] == quote)
                {
                    i += 2; // land on the last quote; the caller's loop ++ moves past
                    return null;
                }

                i++;
            }

            return new PreflightError("SyntaxError: unterminated triple-quoted string literal", start);
        }

        i++;
        while (i < n)
        {
            var c = s[i];

            // A backslash escapes the next character, including a line continuation.
            if (c == '\\' && !isRaw)
            {
                i += 2;
                continue;
            }

            // In a raw string a backslash still prevents the quote from closing the literal, but it
            // remains part of the value. Treat it the same way for termination purposes.
            if (c == '\\' && isRaw)
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
                return new PreflightError("SyntaxError: unterminated string literal", start);
            }

            i++;
        }

        return new PreflightError("SyntaxError: unterminated string literal", start);
    }
}
