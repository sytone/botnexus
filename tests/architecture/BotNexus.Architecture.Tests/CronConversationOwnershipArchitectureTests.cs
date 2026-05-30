using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using BotNexus.Domain.Primitives;
using Shouldly;

namespace BotNexus.Architecture.Tests;

/// <summary>
/// Architecture fitness functions enforcing the Phase 9 / P9-D contract: a cron job owns
/// exactly one long-lived <see cref="ConversationId"/>, recorded on
/// <see cref="BotNexus.Cron.CronJob.ConversationId"/>. The pre-P9-D shapes — composite
/// conversation ids (<c>cronconv:&lt;agent&gt;:&lt;job&gt;</c>) and virtual portal
/// conversations (<c>cron-session:&lt;sessionId&gt;</c>) — must never reappear in
/// production code.
/// </summary>
/// <remarks>
/// <para>
/// Before P9-D, every cron run synthesised a deterministic composite conversation id
/// from the agent + job (<c>cronconv:</c>) and the portal projected one virtual
/// conversation per cron session (<c>cron-session:</c>) so users could see runs in the
/// sidebar. The combination produced duplicate cron threads, orphan sessions, and the
/// portal sidebar issue #640. P9-D inverts ownership: the job stamps its
/// <see cref="ConversationId"/> on first run via the scheduler's CAS, and the portal
/// shows the single backend Conversation directly.
/// </para>
/// <para>
/// Two source fences (grep-based, lexer-stripped) and one reflection pin defend the
/// invariant. Each fence ships a vacuity-guard self-test (per the stored memory
/// "every regex-based architecture fence must include a self-test asserting it matches
/// its target methods/symbols").
/// </para>
/// </remarks>
public sealed class CronConversationOwnershipArchitectureTests
{
    [Fact]
    public void CronJob_ConversationId_IsNullableConversationId()
    {
        // The job's ConversationId is genuinely optional: null on creation, stamped on
        // first run. Don't confuse this with Session.ConversationId (non-nullable).
        var jobType = typeof(BotNexus.Cron.CronJob);
        var property = jobType.GetProperty("ConversationId", BindingFlags.Public | BindingFlags.Instance);
        property.ShouldNotBeNull("CronJob.ConversationId must exist as a public instance property.");

        property.PropertyType.ShouldBe(
            typeof(ConversationId?),
            "P9-D contract: CronJob.ConversationId is the canonical link from a cron job to its " +
            "long-lived conversation. It MUST be ConversationId? — genuinely null on creation and " +
            "stamped on first run via the scheduler's CAS. If this fails, the ownership model has " +
            "drifted: either flip it back to nullable, or update this fence and re-design the " +
            "first-run reservation contract.");
    }

    [Fact]
    public void NoProductionSourceFile_Constructs_CronconvCompositeId()
    {
        var srcRoot = FindSourceRoot();
        var violations = new List<string>();

        foreach (var path in EnumerateProductionCsFiles(srcRoot))
        {
            var relative = ToRelative(srcRoot, path);
            if (s_cronconvAllowlist.Contains(relative)) continue;
            var stripped = StripComments(File.ReadAllText(path));
            if (s_cronconvPattern.IsMatch(stripped))
                violations.Add(relative);
        }

        violations.ShouldBeEmpty(
            "P9-D: production code must not construct or look up the legacy `cronconv:` composite " +
            "conversation id. The cron job stamps its ConversationId on first run via " +
            "ICronStore.TrySetConversationIdAsync — all reads happen by id, never by composite key. " +
            "The one-shot legacy migration in CronScheduler.MigrateLegacyCronConversationsAsync " +
            "reconciles old composite-id rows during scheduler startup, but no other production code " +
            "may construct one.\nViolations:\n  " + string.Join("\n  ", violations));
    }

    [Fact]
    public void CronconvAllowlist_OnlyContains_FilesThat_StillExist_AndStill_TripTheFence()
    {
        var srcRoot = FindSourceRoot();
        var stale = new List<string>();

        foreach (var relative in s_cronconvAllowlist)
        {
            var full = Path.Combine(srcRoot, relative);
            if (!File.Exists(full))
            {
                stale.Add($"{relative} — file does not exist (delete this allowlist entry)");
                continue;
            }
            if (!s_cronconvPattern.IsMatch(StripComments(File.ReadAllText(full))))
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
    public void NoProductionSourceFile_References_CronSessionVirtualConversation()
    {
        var srcRoot = FindSourceRoot();
        var violations = new List<string>();

        foreach (var path in EnumerateProductionCsFiles(srcRoot))
        {
            var relative = ToRelative(srcRoot, path);
            var stripped = StripComments(File.ReadAllText(path));
            if (s_cronSessionPattern.IsMatch(stripped))
                violations.Add(relative);
        }

        violations.ShouldBeEmpty(
            "P9-D: production code must not construct or consume the legacy `cron-session:` virtual " +
            "conversation id. The portal now renders the single backend Conversation directly; the " +
            "AgentInteractionService / PortalLoadService virtual-projection helpers and the " +
            "ConversationsController compat handler were removed in P9-D. If a channel needs a synthetic " +
            "per-session conversation in future, model it as a real Conversation in the gateway.\n" +
            "Violations:\n  " + string.Join("\n  ", violations));
    }

    [Fact]
    public void Fence_IsNotVacuous_AgainstSyntheticReintroduction()
    {
        // Each of these patterns mirrors the literal we removed in P9-D — if a regression PR
        // added one back, the fence MUST trip. Vacuity guard per the stored memory.
        s_cronconvPattern.IsMatch(@"return ConversationId.From($""cronconv:{agentId}:{jobId}"");")
            .ShouldBeTrue("Vacuity guard: `cronconv:` interpolation must trip the fence.");
        s_cronconvPattern.IsMatch(@"var key = ""cronconv:foo:bar"";")
            .ShouldBeTrue("Vacuity guard: literal `cronconv:` string must trip the fence.");
        s_cronSessionPattern.IsMatch(@"var conversationId = $""cron-session:{sessionId}"";")
            .ShouldBeTrue("Vacuity guard: `cron-session:` interpolation must trip the fence.");
        s_cronSessionPattern.IsMatch(@"if (id.StartsWith(""cron-session:""))")
            .ShouldBeTrue("Vacuity guard: literal `cron-session:` string must trip the fence.");
    }

    [Fact]
    public void Fence_DoesNotFalsePositive_OnUnrelatedTokens()
    {
        // The fence is scoped to the exact prefix tokens — adjacent identifiers and the
        // session-id prefix `cron:` (which IS still used for session IDs, see CronTrigger)
        // must not match.
        s_cronconvPattern.IsMatch(@"var sessionId = SessionId.From(""cron:job-id:20250101120000:abc"");")
            .ShouldBeFalse("False-positive guard: `cron:` (session id prefix) must NOT match the cronconv fence.");
        s_cronSessionPattern.IsMatch(@"var sessionId = SessionId.From(""cron:job-id:20250101120000:abc"");")
            .ShouldBeFalse("False-positive guard: `cron:` (session id prefix) must NOT match the cron-session fence.");
        s_cronconvPattern.IsMatch(@"var cronConvention = ""scheduled job pattern"";")
            .ShouldBeFalse("False-positive guard: prose containing the word `cron` must not match.");
        s_cronSessionPattern.IsMatch(@"var cronSession = startCronSession();")
            .ShouldBeFalse("False-positive guard: CamelCase identifier `cronSession` must not match (no colon).");
    }

    [Fact]
    public void Fence_DoesNotFalsePositive_OnCommentMentions()
    {
        // Comments and XML docs explaining the historical shape (e.g. the legacy-migration
        // documentation in CronScheduler.cs) must not trip the fence — the lexer strips them.
        const string syntheticClean = """
            /// <summary>
            /// Pre-P9-D, the cron conversation id was `cronconv:{agent}:{job}` and the portal
            /// surfaced one virtual `cron-session:{sessionId}` per run. Both shapes are dead.
            /// </summary>
            // Legacy: cronconv: composite ids were removed in P9-D.
            /* Block comment: cron-session: virtual conversations were removed in P9-D. */
            public void Documented() { }
            """;

        s_cronconvPattern.IsMatch(StripComments(syntheticClean)).ShouldBeFalse(
            "False-positive guard: comment mentions of `cronconv:` for historical context must not trip.");
        s_cronSessionPattern.IsMatch(StripComments(syntheticClean)).ShouldBeFalse(
            "False-positive guard: comment mentions of `cron-session:` for historical context must not trip.");
    }

    // Match `cronconv:` only when it appears inside a string-like token (quoted literal or
    // interpolated string) so identifier names and comments don't trigger.
    private static readonly Regex s_cronconvPattern = new(
        @"""[^""]*cronconv:|\$""[^""]*cronconv:",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // Files allowed to construct the legacy composite id for one purpose only: the
    // scheduler's one-shot startup migration that reconciles pre-P9-D conversation rows
    // onto the new per-job ConversationId. Any other use is a regression.
    private static readonly HashSet<string> s_cronconvAllowlist = new(StringComparer.OrdinalIgnoreCase)
    {
        Path.Combine("gateway", "BotNexus.Cron", "CronScheduler.cs"),
    };

    // Match `cron-session:` (the legacy portal virtual conversation prefix) anywhere — the
    // hyphen makes it unambiguous, no scoping needed beyond the literal.
    private static readonly Regex s_cronSessionPattern = new(
        @"cron-session:",
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
    /// architecture fences (e.g. <see cref="SessionConversationIdNonNullableArchitectureTests"/>).
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
