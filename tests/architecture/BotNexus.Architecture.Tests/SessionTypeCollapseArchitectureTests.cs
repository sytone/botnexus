using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using BotNexus.Domain.Primitives;
using Shouldly;

namespace BotNexus.Architecture.Tests;

/// <summary>
/// Architecture fitness functions enforcing the Phase 9 / P9-E contract: <c>SessionType</c>
/// describes only the long-lived classification of who is talking to whom
/// (UserAgent / AgentSelf / AgentAgent / AgentSubAgent). The pre-P9-E proxy-trigger
/// values — <c>SessionType.Soul</c>, <c>SessionType.Cron</c>, and
/// <c>SessionType.Heartbeat</c> — must never reappear in production code, and the
/// substring predicate <c>SessionId.IsSoul</c> (G-4) must stay deleted.
/// </summary>
/// <remarks>
/// <para>
/// Before P9-E, the "kind of trigger" that produced a session leaked into its
/// classification: SoulTrigger stamped <c>Soul</c>, CronTrigger stamped <c>Cron</c>,
/// HeartbeatTrigger stamped <c>Heartbeat</c>. That conflated two orthogonal axes
/// (classification vs. trigger) and meant every consumer that cared about
/// interactivity had to enumerate every trigger value. P9-E collapses the
/// classification axis onto its natural shape — Soul/Heartbeat → <c>AgentSelf</c>
/// (the agent talking to itself), Cron → <c>UserAgent</c> (proxy for the citizen
/// who scheduled the job, directive W-2) — and moves the trigger kind onto a new
/// nullable <c>SessionEntry.Trigger</c> field (a <see cref="TriggerType"/>) so the
/// origin is recorded per-entry without polluting the session shape.
/// </para>
/// <para>
/// Two source fences (grep-based, lexer-stripped) and one reflection pin defend the
/// invariant. Each fence ships a vacuity-guard self-test (per the stored memory
/// "every regex-based architecture fence must include a self-test asserting it matches
/// its target methods/symbols").
/// </para>
/// </remarks>
public sealed class SessionTypeCollapseArchitectureTests
{
    [Fact]
    public void SessionId_IsSoul_PredicateIsDeleted()
    {
        // G-4: soul-session classification no longer rides on a substring of the
        // session id. SoulTrigger stamps SessionType.AgentSelf and writes
        // Metadata["soulDate"]; discovery queries the metadata key directly.
        var idType = typeof(SessionId);
        var member = idType.GetMember(
            "IsSoul",
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static);

        member.ShouldBeEmpty(
            "P9-E (#645) / G-4: SessionId.IsSoul must NOT exist. Soul-session discovery " +
            "queries Session.Metadata.ContainsKey(\"soulDate\") (the canonical signal set " +
            "by SoulTrigger.InitializeSoulSession). If this fails: a previously-deleted " +
            "substring predicate has been re-added — remove it and route the caller " +
            "through the metadata probe instead.");
    }

    [Fact]
    public void SessionType_Soul_Cron_Heartbeat_RegistryFieldsAreDeleted()
    {
        // Reflection pin on the registry — complements the source-fence below.
        // The registry layer is the canonical surface for new SessionType values;
        // their absence here proves the type was actually collapsed, not just
        // shadowed.
        var sessionTypeType = typeof(SessionType);
        foreach (var name in new[] { "Soul", "Cron", "Heartbeat" })
        {
            var member = sessionTypeType.GetMember(
                name,
                BindingFlags.Public | BindingFlags.Static | BindingFlags.GetField | BindingFlags.GetProperty);
            member.ShouldBeEmpty(
                $"P9-E (#645): SessionType.{name} was a proxy-trigger value and is deleted. " +
                "Soul/Heartbeat collapse onto AgentSelf; Cron collapses onto UserAgent " +
                "(proxy for the citizen who scheduled the job — directive W-2). The trigger " +
                "kind moved to SessionEntry.Trigger (TriggerType?). If you need to record " +
                "the trigger origin, set Trigger on the user entry; do NOT reintroduce the " +
                "discriminator.");
        }
    }

    [Fact]
    public void NoProductionSourceFile_References_DeletedSessionTypeValues()
    {
        var srcRoot = FindSourceRoot();
        var violations = new List<string>();

        foreach (var path in EnumerateProductionCsFiles(srcRoot))
        {
            var relative = ToRelative(srcRoot, path);
            var stripped = StripComments(File.ReadAllText(path));
            if (s_deletedSessionTypePattern.IsMatch(stripped))
                violations.Add(relative);
        }

        violations.ShouldBeEmpty(
            "P9-E (#645): production code must not reference the deleted member-access " +
            "patterns SessionType.Soul, SessionType.Cron, or SessionType.Heartbeat. The " +
            "classification axis was collapsed (Soul/Heartbeat → AgentSelf, Cron → " +
            "UserAgent) and the trigger axis moved to SessionEntry.Trigger (TriggerType?). " +
            "If you need a trigger-origin signal, stamp Trigger on the user entry; if you " +
            "need a non-interactive signal, drive it off Session.IsInteractive (which " +
            "already excludes UserAgent + ChannelType==\"cron\" and the AgentSelf shape).\n" +
            "Violations:\n  " + string.Join("\n  ", violations));
    }

    [Fact]
    public void NoProductionSourceFile_References_DeletedSessionIdIsSoul()
    {
        var srcRoot = FindSourceRoot();
        var violations = new List<string>();

        foreach (var path in EnumerateProductionCsFiles(srcRoot))
        {
            var relative = ToRelative(srcRoot, path);
            var stripped = StripComments(File.ReadAllText(path));
            if (s_isSoulPattern.IsMatch(stripped))
                violations.Add(relative);
        }

        violations.ShouldBeEmpty(
            "P9-E (#645) / G-4: production code must not call any `.IsSoul` predicate on a " +
            "session id. Route soul-session discovery through Session.Metadata.ContainsKey" +
            "(\"soulDate\") instead. If a legitimate non-SessionId `.IsSoul` exists in a " +
            "future model, scope this fence to the SessionId receiver explicitly.\n" +
            "Violations:\n  " + string.Join("\n  ", violations));
    }

    [Fact]
    public void Fence_IsNotVacuous_AgainstSyntheticReintroduction()
    {
        // Each of these mirrors the literal we removed in P9-E — if a regression PR
        // added one back, the fence MUST trip. Vacuity guard per the stored memory.
        s_deletedSessionTypePattern.IsMatch(@"session.SessionType = SessionType.Soul;")
            .ShouldBeTrue("Vacuity guard: SessionType.Soul member access must trip the fence.");
        s_deletedSessionTypePattern.IsMatch(@"if (session.SessionType == SessionType.Cron) return;")
            .ShouldBeTrue("Vacuity guard: SessionType.Cron member access must trip the fence.");
        s_deletedSessionTypePattern.IsMatch(@"return SessionType.Heartbeat;")
            .ShouldBeTrue("Vacuity guard: SessionType.Heartbeat member access must trip the fence.");

        s_isSoulPattern.IsMatch(@"if (sessionId.IsSoul) return;")
            .ShouldBeTrue("Vacuity guard: sessionId.IsSoul property access must trip the fence.");
        s_isSoulPattern.IsMatch(@"sessions.Where(s => s.SessionId.IsSoul)")
            .ShouldBeTrue("Vacuity guard: chained .SessionId.IsSoul access must trip the fence.");
    }

    [Fact]
    public void Fence_DoesNotFalsePositive_OnUnrelatedTokens()
    {
        // Member-access is the legitimate use site for SessionType.UserAgent/AgentSelf,
        // the TriggerType registry still has Soul/Cron/Heartbeat values, and string
        // literals like "cron" (channel type), "soul" (file path segments), and
        // "heartbeat" (text tokens) must all remain valid.
        s_deletedSessionTypePattern.IsMatch(@"session.SessionType = SessionType.UserAgent;")
            .ShouldBeFalse("False-positive guard: SessionType.UserAgent (live value) must NOT trip.");
        s_deletedSessionTypePattern.IsMatch(@"session.SessionType = SessionType.AgentSelf;")
            .ShouldBeFalse("False-positive guard: SessionType.AgentSelf (live value) must NOT trip.");
        s_deletedSessionTypePattern.IsMatch(@"entry.Trigger = TriggerType.Soul;")
            .ShouldBeFalse("False-positive guard: TriggerType.Soul (live value on TriggerType) must NOT trip.");
        s_deletedSessionTypePattern.IsMatch(@"entry.Trigger = TriggerType.Cron;")
            .ShouldBeFalse("False-positive guard: TriggerType.Cron (live value on TriggerType) must NOT trip.");
        s_deletedSessionTypePattern.IsMatch(@"if (channelType == ""cron"") return;")
            .ShouldBeFalse("False-positive guard: string literal \"cron\" (channel key) must NOT trip.");

        s_isSoulPattern.IsMatch(@"if (session.IsInteractive) return;")
            .ShouldBeFalse("False-positive guard: IsInteractive must NOT match.");
        s_isSoulPattern.IsMatch(@"var nameIsSoulful = true;")
            .ShouldBeFalse("False-positive guard: substring 'IsSoul' inside another identifier must NOT match.");
    }

    [Fact]
    public void Fence_DoesNotFalsePositive_OnCommentMentions()
    {
        // Comments and XML docs explaining the historical shape (e.g. the migration
        // notes in SessionType.cs / GatewayHost.cs / triggers) must not trip — the
        // lexer strips them.
        const string syntheticClean = """
            /// <summary>
            /// Pre-P9-E this branch returned SessionType.Soul; collapsed onto AgentSelf.
            /// </summary>
            // Legacy: SessionType.Cron was a proxy-trigger value, now UserAgent.
            /* Block comment: SessionId.IsSoul predicate was deleted in P9-E. */
            public void Documented() { }
            """;

        s_deletedSessionTypePattern.IsMatch(StripComments(syntheticClean)).ShouldBeFalse(
            "False-positive guard: comment mentions of SessionType.Soul/Cron/Heartbeat for " +
            "historical context must not trip.");
        s_isSoulPattern.IsMatch(StripComments(syntheticClean)).ShouldBeFalse(
            "False-positive guard: comment mentions of SessionId.IsSoul for historical " +
            "context must not trip.");
    }

    // Match the deleted SessionType member-access values only — not string literals
    // ("cron"/"soul"/"heartbeat" remain legitimate channel keys / trigger names) and
    // not TriggerType.Soul/Cron/Heartbeat (those are the live values that replaced
    // the SessionType discriminator).
    private static readonly Regex s_deletedSessionTypePattern = new(
        @"\bSessionType\.(Soul|Cron|Heartbeat)\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // Match any receiver `.IsSoul` token-bounded — the only place this lived was on
    // SessionId; scoping by trailing word-boundary keeps NameIsSoulful etc. clean.
    private static readonly Regex s_isSoulPattern = new(
        @"\.IsSoul\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

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
