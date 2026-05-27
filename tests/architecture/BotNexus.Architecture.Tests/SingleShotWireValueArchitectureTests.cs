using System.Text;
using System.Text.RegularExpressions;
using Shouldly;

namespace BotNexus.Architecture.Tests;

/// <summary>
/// Architecture fitness function enforcing the <c>#552</c> wire-value rename: the legacy
/// <c>CompletionReason = "objectiveMet"</c> token MUST NOT appear in any production
/// source file under <c>src/</c>.
/// </summary>
/// <remarks>
/// <para>
/// <c>AgentExchangeResult.CompletionReason</c> reports <c>"singleShot"</c> when the
/// caller did not set an <c>Objective</c> and the exchange ran for exactly one prompt.
/// The old wire value <c>"objectiveMet"</c> was misleading because no objective was ever
/// provided, and was renamed in PR <c>#552</c>. Since BotNexus has no external API consumers
/// (only the in-process <c>AgentConverseTool</c> serialises the result back to its calling
/// LLM), the rename is a strict break — there is no back-compat layer to preserve.
/// </para>
/// <para>
/// This fence catches the regression shape where someone reverts <c>ResolveCompletionReason</c>
/// or adds a new code path emitting the old wire value. The behavioural pin lives in
/// <c>AgentExchangeServiceTests.ConverseAsync_NoObjectiveSet_SingleShotReasonReturned</c>.
/// </para>
/// </remarks>
public sealed class SingleShotWireValueArchitectureTests
{
    [Fact]
    public void NoProductionSourceFile_ContainsLegacyObjectiveMetWireValue()
    {
        var srcRoot = FindSourceRoot();

        var violations = new List<string>();
        foreach (var path in EnumerateProductionCsFiles(srcRoot))
        {
            var stripped = StripComments(File.ReadAllText(path));
            if (ContainsLegacyWireValue(stripped))
            {
                violations.Add(NormalizePath(path));
            }
        }

        violations.ShouldBeEmpty(
            "The wire value `\"objectiveMet\"` was renamed to `\"singleShot\"` in #552. " +
            "Any remaining occurrence in production source (outside C# comments) is a regression — " +
            "the legacy literal must not be re-introduced under any code path. If a deprecation " +
            "shim is genuinely required, document it explicitly in a comment (which the fence " +
            "ignores) and add an allowlist entry here.\n" +
            "Violations:\n  " + string.Join("\n  ", violations));
    }

    [Fact]
    public void Fence_IsNotVacuous_AgainstSyntheticRegression()
    {
        // Synthetic post-revert shape: code emitting the legacy literal MUST trip the fence.
        // If this test fails, the fence has been weakened so far that the original
        // regression class is undetectable.
        const string syntheticViolation = """
            public static string ResolveCompletionReason(bool exchangeFinished, bool singleShot)
            {
                if (exchangeFinished) return "exchangeFinished";
                if (singleShot) return "objectiveMet"; // back-compat
                return "maxTurnsReached";
            }
            """;
        ContainsLegacyWireValue(StripComments(syntheticViolation)).ShouldBeTrue(
            "Vacuity guard: the fence must flag any production code emitting the legacy " +
            "`\"objectiveMet\"` literal — this is the pre-#552 bug shape and the entire point " +
            "of this fitness function. If this assertion fails, the literal check has been " +
            "neutered.");
    }

    [Fact]
    public void Fence_DoesNotFalsePositive_OnCommentMention()
    {
        // Synthetic clean shape: a deprecation comment that MENTIONS the legacy literal for
        // historical reference is fine — the comment stripper removes it before the fence
        // applies. This avoids forcing future authors to delete explanatory comments about
        // the rename when they document the wire-value history.
        const string syntheticClean = """
            public static string ResolveCompletionReason(bool exchangeFinished, bool singleShot)
            {
                // Renamed from "objectiveMet" in #552 because the wire value was misleading
                // when no Objective was set.
                if (exchangeFinished) return "exchangeFinished";
                if (singleShot) return "singleShot";
                return "maxTurnsReached";
            }
            """;
        ContainsLegacyWireValue(StripComments(syntheticClean)).ShouldBeFalse(
            "False-positive guard: the fence must not flag a comment-only mention of the " +
            "legacy literal. If this fails, the comment stripper has regressed and the fence " +
            "will block PRs that legitimately document the rename in comments.");
    }

    [Fact]
    public void Fence_DoesNotFalsePositive_OnXmlDocMention()
    {
        // Synthetic clean shape: an XML doc comment (triple-slash) mentioning the legacy
        // wire value as historical context must NOT trip the fence. The comment stripper
        // handles `///` just like `//`.
        const string syntheticClean = """
            /// <summary>
            /// The completion reason. Possible values include "singleShot" (renamed from
            /// "objectiveMet" in #552), "exchangeFinished", "maxTurnsReached", "error".
            /// </summary>
            public string CompletionReason { get; init; } = "singleShot";
            """;
        ContainsLegacyWireValue(StripComments(syntheticClean)).ShouldBeFalse(
            "False-positive guard: the fence must not flag an XML doc reference to the legacy " +
            "literal. If this fails, the comment stripper is not handling triple-slash comments " +
            "correctly and the fence will block authors from documenting the rename in DocFX/XML " +
            "summaries.");
    }

    [Fact]
    public void Fence_DoesNotFalsePositive_OnUnrelatedToken()
    {
        // Synthetic clean shape: tokens like `objectiveMet` as an identifier (variable name,
        // property name) without the surrounding quotes are NOT the wire value and must be
        // allowed. The fence specifically targets the quoted string literal.
        const string syntheticClean = """
            public bool objectiveMet { get; set; } = false;
            var flagObjectiveMet = isCompleted;
            """;
        ContainsLegacyWireValue(StripComments(syntheticClean)).ShouldBeFalse(
            "False-positive guard: the fence targets the quoted literal `\"objectiveMet\"`. " +
            "Identifier-level uses (variable names, property names) are unrelated to the wire " +
            "value and must not be flagged. If this fails, the fence has been broadened to " +
            "ban a substring rather than a quoted literal.");
    }

    [Fact]
    public void Fence_DetectsLegacyLiteral_OnLineWithUrlString()
    {
        // Critique-sweep fold-in (#552 bug-hunt MEDIUM / security LOW / plan-vs-impl LOW):
        // before the string-aware stripper, the naive regex `//[^\r\n]*` would treat any `//`
        // inside a regular string literal (e.g. a URL) as a line-comment start and strip
        // everything to the next newline — including a real `"objectiveMet"` on the same line.
        // The fence would then silently pass with zero candidates. This test pins the
        // string-aware stripper: `//` inside a `"…"` literal must NOT be treated as a comment.
        const string syntheticViolation = """
            var url = "https://example.com/v1"; var reason = "objectiveMet";
            """;
        ContainsLegacyWireValue(StripComments(syntheticViolation)).ShouldBeTrue(
            "Realistic false-negative pin: the fence must still detect the legacy literal " +
            "when the same line contains a URL string with `//`. If this fails, the " +
            "string-aware comment stripper has regressed to the naive regex form and " +
            "production code with URL literals near the wire value would silently bypass.");
    }

    [Fact]
    public void Fence_DetectsLegacyLiteral_AfterVerbatimStringWithSlashes()
    {
        // Critique-sweep fold-in: verbatim strings (`@"…"`) can contain `//` directly with
        // no escaping (e.g. Windows-style `\\server//share` or doc-string slashes). The
        // string-aware stripper must recognize the `@"…"` form and not strip the `//` inside.
        const string syntheticViolation = """"
            var path = @"C:\foo//bar"; var reason = "objectiveMet";
            """";
        ContainsLegacyWireValue(StripComments(syntheticViolation)).ShouldBeTrue(
            "Verbatim-string false-negative pin: `//` inside `@\"…\"` must not be treated " +
            "as a comment-start. If this fails, the stripper does not handle verbatim " +
            "strings and the fence has a real coverage gap.");
    }

    private static bool ContainsLegacyWireValue(string source)
    {
        // The wire value is the quoted string literal "objectiveMet". Matching the quotes
        // specifically avoids false-positives on unrelated identifiers that happen to
        // contain the same letter sequence. Both regular and verbatim string literals end
        // with a closing double-quote, so `"objectiveMet"` matches both `"objectiveMet"`
        // and `@"objectiveMet"` (the `@` is the only meaningful prefix difference).
        return Regex.IsMatch(source, "\"objectiveMet\"");
    }

    /// <summary>
    /// Removes single-line (<c>//</c>, <c>///</c>) and block (<c>/* … */</c>) C# comments
    /// while preserving the contents of string and char literals. Required to avoid the
    /// false-negative shape <c>var u = "https://x"; var r = "objectiveMet";</c> where a
    /// regex-only stripper would treat the <c>//</c> inside the URL string as a comment
    /// start and silently bypass the fence (see <c>Fence_DetectsLegacyLiteral_OnLineWithUrlString</c>).
    /// Handles regular strings (with <c>\\</c>/<c>\"</c> escapes), verbatim strings
    /// (<c>@"…"</c> with <c>""</c>-escaped quotes), and char literals.
    /// </summary>
    private static string StripComments(string source)
    {
        var sb = new StringBuilder(source.Length);
        var i = 0;
        var n = source.Length;

        while (i < n)
        {
            var c = source[i];

            if (c == '@' && i + 1 < n && source[i + 1] == '"')
            {
                sb.Append(source, i, 2);
                i += 2;
                while (i < n)
                {
                    if (source[i] == '"')
                    {
                        if (i + 1 < n && source[i + 1] == '"')
                        {
                            sb.Append("\"\"");
                            i += 2;
                            continue;
                        }
                        sb.Append('"');
                        i++;
                        break;
                    }
                    sb.Append(source[i++]);
                }
                continue;
            }

            if (c == '"')
            {
                sb.Append('"');
                i++;
                while (i < n)
                {
                    if (source[i] == '\\' && i + 1 < n)
                    {
                        sb.Append(source[i]);
                        sb.Append(source[i + 1]);
                        i += 2;
                        continue;
                    }
                    if (source[i] == '"')
                    {
                        sb.Append('"');
                        i++;
                        break;
                    }
                    sb.Append(source[i++]);
                }
                continue;
            }

            if (c == '\'')
            {
                sb.Append('\'');
                i++;
                while (i < n)
                {
                    if (source[i] == '\\' && i + 1 < n)
                    {
                        sb.Append(source[i]);
                        sb.Append(source[i + 1]);
                        i += 2;
                        continue;
                    }
                    if (source[i] == '\'')
                    {
                        sb.Append('\'');
                        i++;
                        break;
                    }
                    sb.Append(source[i++]);
                }
                continue;
            }

            if (c == '/' && i + 1 < n && source[i + 1] == '/')
            {
                i += 2;
                while (i < n && source[i] != '\n' && source[i] != '\r')
                {
                    i++;
                }
                continue;
            }

            if (c == '/' && i + 1 < n && source[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < n && !(source[i] == '*' && source[i + 1] == '/'))
                {
                    i++;
                }
                if (i + 1 < n)
                {
                    i += 2;
                }
                else
                {
                    i = n;
                }
                continue;
            }

            sb.Append(c);
            i++;
        }

        return sb.ToString();
    }

    private static IEnumerable<string> EnumerateProductionCsFiles(string srcRoot)
    {
        return Directory.EnumerateFiles(srcRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path =>
                !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) &&
                !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizePath(string path)
        => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);

    private static string FindSourceRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "BotNexus.slnx")))
        {
            current = current.Parent;
        }
        current.ShouldNotBeNull("Could not locate repo root from " + AppContext.BaseDirectory);
        var srcRoot = Path.Combine(current.FullName, "src");
        Directory.Exists(srcRoot).ShouldBeTrue("Expected src/ under " + current.FullName);
        return srcRoot;
    }
}
