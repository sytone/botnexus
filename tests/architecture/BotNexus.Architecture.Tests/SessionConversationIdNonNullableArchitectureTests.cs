using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Shouldly;

namespace BotNexus.Architecture.Tests;

/// <summary>
/// Architecture fitness functions enforcing the Phase 9 / P9-B-2 contract: the
/// <c>ConversationId</c> property on <see cref="BotNexus.Gateway.Abstractions.Models.Session"/>
/// and <see cref="BotNexus.Gateway.Abstractions.Models.GatewaySession"/> must be a non-nullable
/// <see cref="BotNexus.Domain.Primitives.ConversationId"/>. The orphan-session path is
/// closed: every persisted session carries a real conversation id (legacy-conversation
/// backfill is the responsibility of the store layer — see <c>LegacyConversationResolver</c>
/// and the per-store stamping helpers).
/// </summary>
/// <remarks>
/// <para>
/// Before P9-B-2 the field was <c>ConversationId? ConversationId</c> (nullable). Phase 9 /
/// P9-B-1 (#615, PR #616) added save-time + load-time backfill to every store so that every
/// orphan session is durably stamped with the agent's <c>legacy:{agentId}</c> conversation.
/// P9-B-2 (#627, this PR) flips the field to non-nullable so the orphan path becomes
/// structurally impossible — and every reader is freed from null-check ceremony.
/// </para>
/// <para>
/// Two fences:
/// </para>
/// <list type="number">
///   <item>
///     <description><b>Reflection pin</b> — <c>Session.ConversationId</c> and
///     <c>GatewaySession.ConversationId</c> must both be typed
///     <c>BotNexus.Domain.Primitives.ConversationId</c> (NOT
///     <c>Nullable&lt;ConversationId&gt;</c>). Defends against an accidental revert to
///     <c>ConversationId?</c> in either layer.</description>
///   </item>
///   <item>
///     <description><b>Source fence</b> — production code (under <c>src/</c>) must not
///     write any nullable-handling shape against <c>ConversationId</c> on a session.
///     Banned shapes: <c>ConversationId is null</c>, <c>ConversationId is not null</c>,
///     <c>ConversationId.HasValue</c>, <c>ConversationId == null</c>,
///     <c>ConversationId != null</c>, <c>ConversationId?.Value</c>. The fence skips
///     comments via the same lexer used by <see cref="GatewaySessionFacadeArchitectureTests"/>
///     and <see cref="SingleShotWireValueArchitectureTests"/>.</description>
///   </item>
/// </list>
/// <para>
/// <b>Mandatory self-tests</b> (per the stored-memory rule "every regex-based architecture
/// fence must include a self-test asserting it matches its target methods/symbols"):
/// the synthetic-violation pin proves the fence is not vacuous; the false-positive guards
/// prove the fence does not over-fire on legitimate constructs that mention
/// <c>ConversationId</c> in adjacent but distinct contexts.
/// </para>
/// </remarks>
public sealed class SessionConversationIdNonNullableArchitectureTests
{
    [Fact]
    public void Session_ConversationId_IsNonNullable_ConversationId_NotNullable()
    {
        var sessionType = typeof(BotNexus.Gateway.Abstractions.Models.Session);
        var property = sessionType.GetProperty("ConversationId", BindingFlags.Public | BindingFlags.Instance);
        property.ShouldNotBeNull("Session.ConversationId must exist as a public instance property.");

        property.PropertyType.ShouldBe(
            typeof(BotNexus.Domain.Primitives.ConversationId),
            "P9-B-2 contract: Session.ConversationId must be the non-nullable Vogen type. " +
            "If this assertion fails, the orphan-session escape hatch has been re-opened — " +
            "every store layer has been updated to backfill to the agent's legacy conversation " +
            "before persistence; readers no longer need null-check ceremony. Use " +
            "ConversationId.IsInitialized() to distinguish the uninitialized sentinel from a real id.");
    }

    [Fact]
    public void GatewaySession_ConversationId_IsNonNullable_ConversationId_NotNullable()
    {
        var proxyType = typeof(BotNexus.Gateway.Abstractions.Models.GatewaySession);
        var property = proxyType.GetProperty("ConversationId", BindingFlags.Public | BindingFlags.Instance);
        property.ShouldNotBeNull("GatewaySession.ConversationId proxy must exist as a public instance property.");

        property.PropertyType.ShouldBe(
            typeof(BotNexus.Domain.Primitives.ConversationId),
            "P9-B-2 contract: GatewaySession.ConversationId must mirror Session.ConversationId " +
            "as the non-nullable Vogen type. If this fails, the proxy has drifted from the inner " +
            "record's shape and the fence at GatewaySessionFacadeArchitectureTests can no longer " +
            "keep the two in sync.");
    }

    [Fact]
    public void NoProductionSourceFile_TreatsConversationId_AsNullable_OnSession()
    {
        var srcRoot = FindSourceRoot();

        var violations = new List<string>();
        foreach (var path in EnumerateProductionCsFiles(srcRoot))
        {
            var relative = ToRelative(srcRoot, path);
            if (s_allowlist.Contains(relative)) continue;

            var stripped = StripComments(File.ReadAllText(path));
            var matches = FindNullableHandlingShapes(stripped);
            if (matches.Count > 0)
            {
                violations.Add($"{relative} — {string.Join(", ", matches)}");
            }
        }

        violations.ShouldBeEmpty(
            "P9-B-2: production code must not treat Session.ConversationId or " +
            "GatewaySession.ConversationId as nullable. The orphan path is closed — every " +
            "persisted session carries a real conversation id via the store-layer backfill " +
            "(LegacyConversationResolver). Use ConversationId.IsInitialized() ONLY in store " +
            "implementations where the sentinel value can briefly leak through (and only " +
            "during the stamping path). All other readers should treat ConversationId as " +
            "always set.\nViolations:\n  " + string.Join("\n  ", violations));
    }

    /// <summary>
    /// Files explicitly excluded from the source fence because they legitimately read a
    /// <c>ConversationId</c> on a type OTHER than <c>Session</c>/<c>GatewaySession</c>:
    /// either a wire DTO with a <c>string?</c>-shaped property, or a domain aggregate
    /// where the nullable lifecycle is part of the design.
    /// <list type="bullet">
    ///   <item>
    ///     <description><b>ServiceBusChannelAdapter.cs</b> — reads
    ///     <c>ServiceBusInboundEnvelope.ConversationId</c> (a <c>string?</c> wire field used
    ///     to mirror routing onto Service Bus application properties). Not the Vogen
    ///     value object on a session.</description>
    ///   </item>
    ///   <item>
    ///     <description><b>CronScheduler.cs</b> — reads <c>CronJob.ConversationId</c>, which
    ///     is <c>ConversationId?</c> by design (per G-5: cron jobs do not bind a conversation
    ///     until first run). The pinback path checks <c>HasValue</c> on the job record, not
    ///     on a session.</description>
    ///   </item>
    ///   <item>
    ///     <description><b>FileSessionStore.cs</b> — reads <c>SessionMeta.ConversationId</c>,
    ///     a JSON sidecar DTO that retains <c>ConversationId?</c> because pre-P9-B-1 sidecars
    ///     on disk may legitimately be missing the field. The eager startup sweep and
    ///     load-time backfill rely on this exact null-check to identify orphan sidecars
    ///     and rewrite them with the legacy conversation id.</description>
    ///   </item>
    /// </list>
    /// </summary>
    private static readonly HashSet<string> s_allowlist = new(StringComparer.OrdinalIgnoreCase)
    {
        Path.Combine("extensions", "BotNexus.Extensions.Channels.ServiceBus", "ServiceBusChannelAdapter.cs"),
        Path.Combine("gateway", "BotNexus.Cron", "CronScheduler.cs"),
        Path.Combine("gateway", "BotNexus.Gateway.Sessions", "FileSessionStore.cs"),
    };

    private static string ToRelative(string srcRoot, string fullPath)
    {
        var full = Path.GetFullPath(fullPath);
        var root = Path.GetFullPath(srcRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return full.StartsWith(root, StringComparison.OrdinalIgnoreCase)
            ? full.Substring(root.Length)
            : full;
    }

    [Fact]
    public void Allowlist_OnlyContains_FilesThat_StillExist_AndStill_TripTheFence()
    {
        // Allowlist hygiene: if a file is in the allowlist but no longer trips the fence,
        // the entry is stale and must be removed (otherwise it permanently exempts a real
        // future regression in the same file). If a file no longer exists, the path is
        // wrong.
        var srcRoot = FindSourceRoot();
        var stale = new List<string>();

        foreach (var relative in s_allowlist)
        {
            var full = Path.Combine(srcRoot, relative);
            if (!File.Exists(full))
            {
                stale.Add($"{relative} — file does not exist (delete this allowlist entry)");
                continue;
            }

            var stripped = StripComments(File.ReadAllText(full));
            if (FindNullableHandlingShapes(stripped).Count == 0)
            {
                stale.Add($"{relative} — file no longer trips the fence (delete this allowlist entry)");
            }
        }

        stale.ShouldBeEmpty(
            "Allowlist hygiene: every entry must still match an existing file AND still " +
            "trip the fence; otherwise it grants a permanent exemption to future " +
            "regressions in the same file.\nStale entries:\n  " + string.Join("\n  ", stale));
    }

    [Fact]
    public void Fence_IsNotVacuous_AgainstSyntheticReverts()
    {
        // If P9-B-2 were reverted to `ConversationId?` and a caller wrote a typical null
        // check, the fence must trip. This synthetic input mirrors what a regression PR
        // would look like — guard against the fence going silent under a refactor.
        var syntheticRevertShapes = new[]
        {
            "if (session.ConversationId is null) { stamp(session); }",
            "if (session.ConversationId is not null) return;",
            "var cid = session.ConversationId?.Value;",
            "if (session.ConversationId == null) throw new Exception();",
            "if (session.ConversationId != null) Console.WriteLine(\"set\");",
            "if (session.ConversationId.HasValue) Use(session.ConversationId.Value);",
        };

        foreach (var shape in syntheticRevertShapes)
        {
            FindNullableHandlingShapes(StripComments(shape)).Count.ShouldBeGreaterThan(
                0,
                $"Vacuity guard: the fence must detect the canonical revert shape `{shape}`. " +
                "If this assertion fails, the regex no longer recognises the very pattern it " +
                "exists to prevent.");
        }
    }

    [Fact]
    public void Fence_DoesNotFalsePositive_OnUnrelatedNullChecksAndMembers()
    {
        // Banned shapes are scoped to a `ConversationId` member access. Independent null
        // checks on other members and `ConversationId` mentions inside parameter or type
        // declarations must not trip the fence.
        var cleanShapes = new[]
        {
            // Different member with the same shape.
            "if (session.AgentId is null) return;",
            "var sid = session.SessionId?.Value;",
            // Parameter/type declaration — the type token itself, not a runtime check.
            "public void Resolve(ConversationId conversationId) { }",
            "public ConversationId? FindConversation() => null;",
            // The legitimate post-P9-B-2 read site — bare member access, no nullable shape.
            "var cid = session.ConversationId;",
            "store.GetByConversationAsync(session.ConversationId, ct);",
            // IsInitialized() is the sanctioned sentinel check — must NOT be banned.
            "if (session.ConversationId.IsInitialized()) return;",
            "if (!session.ConversationId.IsInitialized()) StampLegacy(session);",
        };

        foreach (var shape in cleanShapes)
        {
            FindNullableHandlingShapes(StripComments(shape)).Count.ShouldBe(
                0,
                $"False-positive guard: shape `{shape}` is legitimate and must not trip the " +
                "fence. If this fails, the regex is too broad — adjust the pattern, not the " +
                "calling code.");
        }
    }

    [Fact]
    public void Fence_DoesNotFalsePositive_OnCommentMentions()
    {
        // Comments and XML docs that mention the banned shape for historical reference
        // (e.g. documenting the migration) must not trip the fence — the comment-stripping
        // lexer removes them before regex matching.
        const string syntheticClean = """
            /// <summary>
            /// Pre-P9-B-2, the legacy code was: if (session.ConversationId is null) backfill();
            /// Post-P9-B-2: use ConversationId.IsInitialized() for the sentinel discriminator.
            /// </summary>
            public void Documented(GatewaySession session) => Use(session.ConversationId);
            // Historical: session.ConversationId.HasValue used to gate the orphan branch.
            /* Block comment: session.ConversationId == null was the legacy null check. */
            """;

        FindNullableHandlingShapes(StripComments(syntheticClean)).Count.ShouldBe(
            0,
            "False-positive guard: XML doc, line comments, and block comments referencing " +
            "the banned shapes for historical context must not trip the fence. If this fails, " +
            "the comment-stripping lexer has regressed.");
    }

    /// <summary>
    /// Detects the six banned nullable-handling shapes around a <c>ConversationId</c>
    /// member access. The regex is intentionally scoped to <c>ConversationId</c> as the
    /// member name immediately preceding the nullable operator so unrelated null checks
    /// on other properties (e.g. <c>AgentId is null</c>) do not match.
    /// </summary>
    private static IReadOnlyList<string> FindNullableHandlingShapes(string source)
    {
        var matches = new List<string>();

        // `ConversationId is null` / `ConversationId is not null`
        foreach (Match m in Regex.Matches(source, @"\bConversationId\s+is\s+(?:not\s+)?null\b"))
            matches.Add(m.Value.Trim());

        // `ConversationId == null` / `ConversationId != null`
        foreach (Match m in Regex.Matches(source, @"\bConversationId\s*[!=]=\s*null\b"))
            matches.Add(m.Value.Trim());

        // `ConversationId.HasValue` — Nullable<T>.HasValue accessor.
        foreach (Match m in Regex.Matches(source, @"\bConversationId\.HasValue\b"))
            matches.Add(m.Value.Trim());

        // `ConversationId?.Value` — null-conditional access on a nullable shape.
        foreach (Match m in Regex.Matches(source, @"\bConversationId\?\."))
            matches.Add(m.Value.Trim());

        return matches;
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

    /// <summary>
    /// Removes single-line (<c>//</c>, <c>///</c>) and block (<c>/* … */</c>) C# comments
    /// while preserving string and char literals. Identical lexer to
    /// <c>SingleShotWireValueArchitectureTests.StripComments</c> (PR #569) and
    /// <c>GatewaySessionFacadeArchitectureTests.StripComments</c> (PR #571).
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
}
