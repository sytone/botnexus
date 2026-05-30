using System.Text;
using System.Text.RegularExpressions;
using Shouldly;

namespace BotNexus.Architecture.Tests;

/// <summary>
/// Architecture fitness functions enforcing the Phase 9 / P9-F atomic-merge contract:
/// <see cref="BotNexus.Gateway.Abstractions.Models.Conversation.Participants"/> may only
/// be mutated by the three persistence stores; every other producer must go through
/// <c>IConversationStore.AddParticipantsAsync</c>.
/// </summary>
/// <remarks>
/// <para>
/// P9-F introduced <c>AddParticipantsAsync</c> as the only atomic-merge entry point for
/// participants. The danger if callers reach past it is the same as before P9-F: a
/// read-modify-write race against <c>SaveAsync</c> clobbers bindings, metadata, or
/// concurrent participant additions. The 3 store implementations are the only places
/// that need direct mutation (initial materialisation + the atomic-merge body itself).
/// </para>
/// <para>
/// The fence ships vacuity / false-positive / comment-mention self-tests per the
/// "every regex-based architecture fence must include a self-test" memory.
/// </para>
/// </remarks>
public sealed class ConversationParticipantsMutationArchitectureTests
{
    [Fact]
    public void NoProductionSourceFile_MutatesParticipantsList_OutsideAllowlist()
    {
        var srcRoot = FindSourceRoot();
        var violations = new List<string>();

        foreach (var path in EnumerateProductionCsFiles(srcRoot))
        {
            var relative = ToRelative(srcRoot, path);
            if (s_mutationAllowlist.Contains(relative)) continue;
            var stripped = StripComments(File.ReadAllText(path));
            if (s_participantsMutationPattern.IsMatch(stripped))
                violations.Add(relative);
        }

        violations.ShouldBeEmpty(
            "P9-F atomic-merge contract: only the conversation stores may mutate " +
            "`.Participants` directly. Producers must call IConversationStore.AddParticipantsAsync " +
            "so the merge is atomic against concurrent SaveAsync / binding writes.\n" +
            "Violations:\n  " + string.Join("\n  ", violations));
    }

    [Fact]
    public void MutationAllowlist_OnlyContains_FilesThat_StillExist_AndStill_TripTheFence()
    {
        var srcRoot = FindSourceRoot();
        var stale = new List<string>();

        foreach (var relative in s_mutationAllowlist)
        {
            var full = Path.Combine(srcRoot, relative);
            if (!File.Exists(full))
            {
                stale.Add($"{relative} — file does not exist (delete this allowlist entry)");
                continue;
            }
            if (!s_participantsMutationPattern.IsMatch(StripComments(File.ReadAllText(full))))
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
        // If a regression PR adds a mutation back, the fence MUST trip. Vacuity guard.
        s_participantsMutationPattern.IsMatch("conversation.Participants.Add(participant);")
            .ShouldBeTrue("Vacuity guard: `.Participants.Add(...)` must trip the fence.");
        s_participantsMutationPattern.IsMatch("conv.Participants.Clear();")
            .ShouldBeTrue("Vacuity guard: `.Participants.Clear()` must trip the fence.");
        s_participantsMutationPattern.IsMatch("conversation.Participants.Remove(p);")
            .ShouldBeTrue("Vacuity guard: `.Participants.Remove(...)` must trip the fence.");
        s_participantsMutationPattern.IsMatch("conversation.Participants.RemoveAll(p => p.CitizenId == citizen);")
            .ShouldBeTrue("Vacuity guard: `.Participants.RemoveAll(...)` must trip the fence.");
        s_participantsMutationPattern.IsMatch("conversation.Participants.Insert(0, participant);")
            .ShouldBeTrue("Vacuity guard: `.Participants.Insert(...)` must trip the fence.");
        s_participantsMutationPattern.IsMatch("conversation.Participants.AddRange(more);")
            .ShouldBeTrue("Vacuity guard: `.Participants.AddRange(...)` must trip the fence.");
    }

    [Fact]
    public void Fence_DoesNotFalsePositive_OnRead()
    {
        // Reads must NOT match — the fence guards mutation only.
        s_participantsMutationPattern.IsMatch("var count = conversation.Participants.Count;")
            .ShouldBeFalse("False-positive guard: reading `.Participants.Count` must NOT match.");
        s_participantsMutationPattern.IsMatch("foreach (var p in conv.Participants) { }")
            .ShouldBeFalse("False-positive guard: iterating `.Participants` must NOT match.");
        s_participantsMutationPattern.IsMatch("var participants = conversation.Participants.ToList();")
            .ShouldBeFalse("False-positive guard: snapshot `.Participants.ToList()` must NOT match.");
        s_participantsMutationPattern.IsMatch("if (conv.Participants.Any()) { }")
            .ShouldBeFalse("False-positive guard: predicate `.Participants.Any()` must NOT match.");
        s_participantsMutationPattern.IsMatch("var matched = conv.Participants.FirstOrDefault(p => p.CitizenId == citizen);")
            .ShouldBeFalse("False-positive guard: LINQ over `.Participants` must NOT match.");
    }

    [Fact]
    public void Fence_DoesNotFalsePositive_OnCommentMentions()
    {
        // Comments referencing the mutation shape must not trip — the lexer strips them.
        const string syntheticClean = """
            /// <summary>
            /// Producers must NOT call `conversation.Participants.Add(...)` directly. Use
            /// IConversationStore.AddParticipantsAsync instead so the merge is atomic.
            /// </summary>
            // Legacy: pre-P9-F, callers did `session.Participants.Add(p)` — that shape is dead.
            /* Block comment: `.Participants.Clear()` is forbidden outside the conversation stores. */
            public void Documented() { }
            """;

        s_participantsMutationPattern.IsMatch(StripComments(syntheticClean)).ShouldBeFalse(
            "False-positive guard: comment mentions of the mutation shape for historical context " +
            "or doc warnings must not trip — the lexer strips them.");
    }

    // Match `.Participants.(Add|Clear|Remove|RemoveAll|Insert|AddRange)\b` — the open paren
    // disambiguates from property access and assignment. The lexer strips comments before
    // the regex runs.
    private static readonly Regex s_participantsMutationPattern = new(
        @"\.Participants\.(?:Add|Clear|Remove|RemoveAll|Insert|AddRange)\s*\(",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // No production code is permitted to mutate the in-place Participants list — the
    // conversation stores themselves don't either: they materialise the list via
    // wholesale assignment (existing.Participants = byCitizen.Values.ToList()) inside
    // AddParticipantsAsync, never `.Add(...)`. If that implementation strategy changes
    // (e.g. a future store decides to use `.Add(...)` instead of wholesale-replace), add
    // that file here and the allowlist-hygiene test will pin the exemption real.
    private static readonly HashSet<string> s_mutationAllowlist = new(StringComparer.OrdinalIgnoreCase);

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
    /// architecture fences (e.g. <see cref="CronConversationOwnershipArchitectureTests"/>).
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
