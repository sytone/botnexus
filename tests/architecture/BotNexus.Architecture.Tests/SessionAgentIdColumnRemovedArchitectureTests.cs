using System.Text;
using System.Text.RegularExpressions;
using Shouldly;

namespace BotNexus.Architecture.Tests;

/// <summary>
/// Architecture fitness functions enforcing the Phase 9 / P9-I (#674) "agent_id column
/// removed from sessions table" contract.
/// </summary>
/// <remarks>
/// <para>
/// P9-I deletes <c>sessions.agent_id</c> and the legacy <c>idx_sessions_agent_id</c> /
/// <c>idx_sessions_conversation_agent</c> indexes. AgentId is hydrated on load from
/// <c>Conversation.AgentId</c> by <c>SqliteSessionStore.HydrateAgentIdAsync</c> /
/// <c>FileSessionStore.HydrateAgentIdAsync</c>. The only remaining references to the
/// string <c>"agent_id"</c> in production are the migration helpers that detect and
/// drop the legacy shape; the only remaining references to <c>"idx_sessions_agent_id"</c>
/// / <c>"idx_sessions_conversation_agent"</c> are the <c>DROP INDEX</c> statements that
/// run alongside the column drop.
/// </para>
/// <para>
/// The fence guards against accidental re-introduction of the column or its indexes
/// in fresh schema DDL, INSERT/UPDATE statements, SELECT lists, or read code that
/// would resurrect the per-session agent identity. Ships vacuity / false-positive /
/// comment-mention self-tests per the "every regex-based architecture fence must
/// include a self-test" memory.
/// </para>
/// </remarks>
public sealed class SessionAgentIdColumnRemovedArchitectureTests
{
    [Fact]
    public void NoProductionSourceFile_ReferencesLegacyAgentIdColumn_OutsideAllowlist()
    {
        var srcRoot = FindSourceRoot();
        var violations = new List<string>();

        foreach (var path in EnumerateProductionCsFiles(srcRoot))
        {
            var relative = ToRelative(srcRoot, path);
            if (s_allowlist.Contains(relative)) continue;
            var stripped = StripComments(File.ReadAllText(path));
            if (s_agentIdColumnPattern.IsMatch(stripped))
                violations.Add(relative);
        }

        violations.ShouldBeEmpty(
            "P9-I contract: the sessions.agent_id column was deleted. AgentId is hydrated " +
            "on load from Conversation.AgentId. References to the string \"agent_id\" or the " +
            "legacy indexes are restricted to the migration helpers in SqliteSessionStore.cs " +
            "(MigrateOrphanedSessionsAsync / DropLegacyAgentIdColumnAsync / VerifyAgentIdColumnConsistencyAsync).\n" +
            "Violations:\n  " + string.Join("\n  ", violations));
    }

    [Fact]
    public void Allowlist_OnlyContains_FilesThat_StillExist_AndStill_TripTheFence()
    {
        var srcRoot = FindSourceRoot();
        var stale = new List<string>();

        foreach (var relative in s_allowlist)
        {
            // Allowlist is stored with forward slashes (portable); convert to the
            // platform separator before resolving against the filesystem.
            var platformRelative = relative.Replace('/', Path.DirectorySeparatorChar);
            var full = Path.Combine(srcRoot, platformRelative);
            if (!File.Exists(full))
            {
                stale.Add($"{relative} — file does not exist (delete this allowlist entry)");
                continue;
            }
            if (!s_agentIdColumnPattern.IsMatch(StripComments(File.ReadAllText(full))))
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
        // Each of these synthetic regressions MUST trip — if not, the fence is vacuous.
        s_agentIdColumnPattern.IsMatch("CREATE TABLE sessions (id TEXT, agent_id TEXT);")
            .ShouldBeTrue("Vacuity guard: re-adding agent_id column in CREATE TABLE must trip.");
        s_agentIdColumnPattern.IsMatch("INSERT INTO sessions (id, agent_id, status) VALUES ($id, $a, 'Active');")
            .ShouldBeTrue("Vacuity guard: writing agent_id in INSERT must trip.");
        s_agentIdColumnPattern.IsMatch("SELECT id, agent_id FROM sessions WHERE id = $id")
            .ShouldBeTrue("Vacuity guard: reading agent_id in SELECT must trip.");
        s_agentIdColumnPattern.IsMatch("UPDATE sessions SET agent_id = $a WHERE id = $id")
            .ShouldBeTrue("Vacuity guard: assigning agent_id in UPDATE must trip.");
        s_agentIdColumnPattern.IsMatch("CREATE INDEX idx_sessions_agent_id ON sessions(agent_id);")
            .ShouldBeTrue("Vacuity guard: re-creating idx_sessions_agent_id must trip.");
        s_agentIdColumnPattern.IsMatch("CREATE INDEX idx_sessions_conversation_agent ON sessions(conversation_id, agent_id);")
            .ShouldBeTrue("Vacuity guard: re-creating idx_sessions_conversation_agent must trip.");
        // Multi-line DDL inside a C# raw string literal must also trip — the fence
        // operates after comment stripping and over arbitrary whitespace.
        s_agentIdColumnPattern.IsMatch("""
            CREATE TABLE IF NOT EXISTS sessions (
                id TEXT PRIMARY KEY,
                conversation_id TEXT NOT NULL,
                agent_id TEXT,
                status TEXT NOT NULL
            )
            """).ShouldBeTrue("Vacuity guard: multi-line CREATE TABLE sessions(...agent_id...) must trip.");
    }

    [Fact]
    public void Fence_DoesNotFalsePositive_OnUnrelatedSymbols()
    {
        // Unrelated property names / .NET symbols must NOT trip — the fence targets only
        // the SQL identifier "agent_id" (snake_case) appearing inside a sessions-table
        // statement, plus the two named legacy indexes.
        s_agentIdColumnPattern.IsMatch("session.AgentId = AgentId.From(\"x\");")
            .ShouldBeFalse("False-positive guard: C# AgentId property accesses must NOT match.");
        s_agentIdColumnPattern.IsMatch("var aId = conversation.AgentId;")
            .ShouldBeFalse("False-positive guard: C# AgentId reads must NOT match.");
        s_agentIdColumnPattern.IsMatch("AgentId agentId = session.AgentId;")
            .ShouldBeFalse("False-positive guard: AgentId parameter declarations must NOT match.");
        s_agentIdColumnPattern.IsMatch("private readonly AgentId _agentId;")
            .ShouldBeFalse("False-positive guard: snake_cased private field names must NOT match.");
        s_agentIdColumnPattern.IsMatch("WHERE conversation_id = $id ORDER BY created_at, id")
            .ShouldBeFalse("False-positive guard: unrelated SQL columns must NOT match.");
        // Other tables legitimately have their own agent_id column — `conversations`,
        // `memories`, `cron_jobs`, etc. The fence MUST NOT trip on those tables.
        s_agentIdColumnPattern.IsMatch("SELECT id FROM conversations WHERE agent_id = $agentId")
            .ShouldBeFalse("False-positive guard: conversations.agent_id is the durable owner — MUST NOT match.");
        s_agentIdColumnPattern.IsMatch("INSERT INTO memories (id, agent_id, session_id) VALUES ($id, $a, $s);")
            .ShouldBeFalse("False-positive guard: memories.agent_id is a legitimate FK — MUST NOT match.");
        s_agentIdColumnPattern.IsMatch("CREATE TABLE cron_jobs (id TEXT, agent_id TEXT NULL);")
            .ShouldBeFalse("False-positive guard: cron_jobs.agent_id is a legitimate FK — MUST NOT match.");
        // Statement boundary: a session SELECT followed by a separate agent_id mention
        // in a different statement (semicolon-separated) must NOT trip.
        s_agentIdColumnPattern.IsMatch(
            "SELECT id FROM sessions WHERE status = 'Active';\n" +
            "INSERT INTO memories (id, agent_id) VALUES ($id, $a);")
            .ShouldBeFalse("False-positive guard: cross-statement co-occurrence must NOT match.");
    }

    [Fact]
    public void Fence_DoesNotFalsePositive_OnCommentMentions()
    {
        // Comments referencing the legacy column for historical context must NOT trip —
        // the lexer strips them before the regex runs.
        const string syntheticClean = """
            // The pre-P9-I sessions table had an agent_id column and an
            // idx_sessions_agent_id index — both dropped in DropLegacyAgentIdColumnAsync.
            /* Also dropped: idx_sessions_conversation_agent (composite over the now-gone column). */
            /// <summary>Mentioning sessions.agent_id in doc comments is fine.</summary>
            public void Documented() { }
            """;

        s_agentIdColumnPattern.IsMatch(StripComments(syntheticClean)).ShouldBeFalse(
            "False-positive guard: comment mentions of the legacy column / indexes for " +
            "historical context must not trip — the lexer strips them.");
    }

    // The fence trips on three precise shapes:
    //   1. The legacy index names (unambiguous P9-I artifacts: cannot collide with anything else).
    //   2. `sessions` ... `agent_id` co-occurring within a single SQL statement window.
    //   3. `agent_id` ... `sessions` (the reverse order — e.g. SELECT id, agent_id FROM sessions).
    // The window is bounded by `;` (SQL statement terminator) and `"""` (C# raw string
    // literal terminator) so co-occurrence in *different* statements does not trip. Word
    // boundaries on both anchors prevent matches against `session_id`, `_agentId`, etc.
    // Other tables (`conversations`, `memories`, `cron_jobs`, ...) legitimately have their
    // own `agent_id` columns; the `\bsessions\b` requirement keeps the fence scoped.
    private static readonly Regex s_agentIdColumnPattern = new(
        @"\bidx_sessions_agent_id\b" +
        @"|\bidx_sessions_conversation_agent\b" +
        @"|\bsessions\b(?:(?!;|"""").){0,500}?\bagent_id\b" +
        @"|\bagent_id\b(?:(?!;|"""").){0,500}?\bsessions\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Singleline);

    // The migration helpers in SqliteSessionStore.cs are the only production code permitted
    // to reference the legacy column / indexes. Everything else must use the post-P9-I
    // shape (Conversation.AgentId via HydrateAgentIdAsync; idx_sessions_conversation_created).
    // Paths use forward-slash separators for cross-platform stability (Linux CI vs Windows
    // dev). ToRelative normalises filesystem paths to the same shape before lookup.
    private static readonly HashSet<string> s_allowlist = new(StringComparer.OrdinalIgnoreCase)
    {
        // MigrateOrphanedSessionsAsync reads agent_id from legacy rows to find the owner;
        // DropLegacyAgentIdColumnAsync drops both legacy indexes then the column itself;
        // VerifyAgentIdColumnConsistencyAsync logs any pre-existing data corruption.
        "gateway/BotNexus.Gateway.Sessions/SqliteSessionStore.cs",
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
        var relative = full.StartsWith(root, StringComparison.OrdinalIgnoreCase)
            ? full.Substring(root.Length)
            : full;
        // Normalise to forward slashes so the allowlist (and any string comparisons) are
        // stable across Linux CI and Windows dev. Backslash is not legal in Linux paths,
        // so the substitution is a one-way no-op on Linux and a portability fix on Windows.
        return relative.Replace('\\', '/');
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
