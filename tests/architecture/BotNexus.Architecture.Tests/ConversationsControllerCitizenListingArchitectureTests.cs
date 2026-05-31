using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using BotNexus.Gateway.Abstractions.Conversations;
using Shouldly;

namespace BotNexus.Architecture.Tests;

/// <summary>
/// Architecture fitness functions enforcing the Phase 9 / P9-G (#661) "responder-side
/// visibility" contract:
/// <list type="bullet">
///   <item>
///     <see cref="IConversationStore.GetSummariesAsync"/> has exactly one parameter
///     (<see cref="CancellationToken"/>). A regression that re-adds an
///     <c>AgentId?</c> overload would silently restore the pre-P9-G owner-only filter
///     and re-hide responder-side conversations from agents that joined but did not
///     initiate them — exactly the bug the phase fixed.
///   </item>
///   <item>
///     Outside the 3 store implementations, the interface contract file, and the one
///     sanctioned controller call site, no production source may call
///     <c>GetSummariesAsync(</c>. All agent-relative listing MUST go through
///     <see cref="IConversationStore.ListForCitizenAsync"/> so the union of (owner +
///     participant) semantics is preserved.
///   </item>
/// </list>
/// </summary>
/// <remarks>
/// <para>
/// Ships vacuity / false-positive / comment-mention self-tests per the
/// "every regex-based architecture fence must include a self-test" memory.
/// </para>
/// </remarks>
public sealed class ConversationsControllerCitizenListingArchitectureTests
{
    [Fact]
    public void GetSummariesAsync_HasOnlyParameterless_Overload()
    {
        var iface = typeof(IConversationStore);
        var overloads = iface.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.Name == nameof(IConversationStore.GetSummariesAsync))
            .ToList();

        overloads.Count.ShouldBe(
            1,
            "P9-G contract: IConversationStore.GetSummariesAsync must have exactly one " +
            "overload. A re-introduced AgentId? overload would silently restore the dead " +
            "owner-only listing path that P9-G deleted.");

        var parameters = overloads[0].GetParameters();
        parameters.Length.ShouldBe(
            1,
            "P9-G contract: GetSummariesAsync must take only CancellationToken — " +
            "agent-relative listing belongs on ListForCitizenAsync.");
        parameters[0].ParameterType.ShouldBe(
            typeof(CancellationToken),
            "P9-G contract: the sole parameter must be CancellationToken.");
    }

    [Fact]
    public void NoProductionSourceFile_CallsGetSummariesAsync_OutsideAllowlist()
    {
        var srcRoot = FindSourceRoot();
        var violations = new List<string>();

        foreach (var path in EnumerateProductionCsFiles(srcRoot))
        {
            var relative = ToRelative(srcRoot, path);
            if (s_callAllowlist.Contains(relative)) continue;

            var stripped = StripComments(File.ReadAllText(path));
            if (s_callPattern.IsMatch(stripped))
                violations.Add(relative);
        }

        violations.ShouldBeEmpty(
            "P9-G: agent-relative conversation listing must route through " +
            "IConversationStore.ListForCitizenAsync so responder-side visibility (W-1) is " +
            "preserved. Direct calls to GetSummariesAsync(...) are only sanctioned in the " +
            "controller (one global-admin path) and the 3 store impls / interface declarations.\n" +
            "Violations:\n  " + string.Join("\n  ", violations));
    }

    [Fact]
    public void CallAllowlist_OnlyContains_FilesThat_StillExist_AndStill_TripTheFence()
    {
        var srcRoot = FindSourceRoot();
        var stale = new List<string>();

        foreach (var relative in s_callAllowlist)
        {
            var full = Path.Combine(srcRoot, relative);
            if (!File.Exists(full))
            {
                stale.Add($"{relative} — file does not exist (delete this allowlist entry)");
                continue;
            }
            if (!s_callPattern.IsMatch(StripComments(File.ReadAllText(full))))
            {
                stale.Add($"{relative} — file no longer trips the fence (delete this allowlist entry)");
            }
        }

        stale.ShouldBeEmpty(
            "Allowlist hygiene: every entry must still match an existing file AND still trip the fence; " +
            "otherwise it grants a permanent exemption to future regressions in the same file.\n" +
            "Stale entries:\n  " + string.Join("\n  ", stale));
    }

    [Fact]
    public void Fence_IsNotVacuous_AgainstSyntheticReintroduction()
    {
        // If a regression PR adds a new caller back, the fence MUST trip. Vacuity guard.
        s_callPattern.IsMatch("var s = await _conversations.GetSummariesAsync(ct);")
            .ShouldBeTrue("Vacuity guard: a real call site must trip the fence.");
        s_callPattern.IsMatch("await store.GetSummariesAsync();")
            .ShouldBeTrue("Vacuity guard: a no-arg call must trip the fence.");
        s_callPattern.IsMatch("GetSummariesAsync (token)")
            .ShouldBeTrue("Vacuity guard: optional whitespace between name and `(` must trip.");
    }

    [Fact]
    public void Fence_DoesNotFalsePositive_OnUnrelatedSymbols()
    {
        // The fence is name-anchored. Reads/declarations/sibling members must NOT match.
        s_callPattern.IsMatch("// see GetSummariesAsync for the listing contract")
            .ShouldBeFalse("False-positive guard: comments are stripped before the regex runs, " +
                "but a synthetic line without `(` must also not match.");
        s_callPattern.IsMatch("public Task XGetSummariesAsync(CancellationToken ct) => Task.CompletedTask;")
            .ShouldBeFalse("False-positive guard: a different method (XGetSummariesAsync) must not match.");
        s_callPattern.IsMatch("nameof(IConversationStore.GetSummariesAsync)")
            .ShouldBeFalse("False-positive guard: `nameof(...)` reference without an open paren on the symbol must not match.");
    }

    [Fact]
    public void Fence_DoesNotFalsePositive_OnCommentMentions()
    {
        const string syntheticClean = """
            /// <summary>
            /// Callers must NOT invoke `_conversations.GetSummariesAsync(ct)` directly for
            /// agent-relative listing — use IConversationStore.ListForCitizenAsync instead.
            /// </summary>
            // Legacy: pre-P9-G code did `store.GetSummariesAsync(agentId)` — that shape is dead.
            /* Block comment: GetSummariesAsync(...) outside the controller is forbidden. */
            public void Documented() { }
            """;

        s_callPattern.IsMatch(StripComments(syntheticClean)).ShouldBeFalse(
            "False-positive guard: comment mentions of the call shape for historical context " +
            "or doc warnings must not trip — the lexer strips them.");
    }

    // Match `GetSummariesAsync\s*\(` anywhere — declarations AND invocations. The lexer
    // strips comments before the regex runs. The allowlist below pins which files are
    // allowed to contain this token at all.
    private static readonly Regex s_callPattern = new(
        @"\bGetSummariesAsync\s*\(",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // Sanctioned files:
    //   - The 3 store implementations (declaration sites for the contract).
    //   - The interface contract file (declaration).
    //   - The single controller call site (the only production invoker; all other
    //     conversation-listing paths must go through ListForCitizenAsync).
    private static readonly HashSet<string> s_callAllowlist = new(StringComparer.OrdinalIgnoreCase)
    {
        Path.Combine("gateway", "BotNexus.Gateway.Conversations", "SqliteConversationStore.cs"),
        Path.Combine("gateway", "BotNexus.Gateway.Conversations", "InMemoryConversationStore.cs"),
        Path.Combine("gateway", "BotNexus.Gateway.Conversations", "FileConversationStore.cs"),
        Path.Combine("gateway", "BotNexus.Gateway.Contracts", "Conversations", "IConversationStore.cs"),
        Path.Combine("gateway", "BotNexus.Gateway.Api", "Controllers", "ConversationsController.cs"),
    };

    private static IEnumerable<string> EnumerateProductionCsFiles(string srcRoot)
    {
        return Directory.EnumerateFiles(srcRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path =>
                !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) &&
                !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase));
    }

    private static string ToRelative(string srcRoot, string fullPath)
    {
        var full = Path.GetFullPath(fullPath);
        var root = Path.GetFullPath(srcRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return full.StartsWith(root, StringComparison.OrdinalIgnoreCase)
            ? full.Substring(root.Length)
            : full;
    }

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

    /// <summary>
    /// Removes single-line (<c>//</c>, <c>///</c>) and block (<c>/* … */</c>) C# comments
    /// while preserving string and char literals. Same lexer pattern used by other
    /// architecture fences (e.g. <see cref="ConversationParticipantsMutationArchitectureTests"/>).
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
                i = Math.Min(n, i + 2);
                continue;
            }

            sb.Append(c);
            i++;
        }

        return sb.ToString();
    }
}
