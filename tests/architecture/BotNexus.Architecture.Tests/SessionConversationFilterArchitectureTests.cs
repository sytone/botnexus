using System.Text.RegularExpressions;
using Shouldly;

namespace BotNexus.Architecture.Tests;

/// <summary>
/// Architecture fitness function enforcing the F-7 invariant: callers must
/// use <c>ISessionStore.ListByConversationAsync(ConversationId, AgentId?)</c>
/// to retrieve sessions for a conversation, NOT load the full session table
/// and filter by <c>Session.ConversationId</c> in memory.
/// </summary>
/// <remarks>
/// <para>
/// The load-all-then-filter pattern (<c>sessions.ListAsync(...).Where(s =&gt; s.ConversationId == ...)</c>)
/// has two well-documented problems:
/// </para>
/// <list type="bullet">
///   <item>
///   On Sqlite-backed deployments it loads the entire session table per
///   request — a real perf issue once a deployment accrues thousands of
///   sessions.
///   </item>
///   <item>
///   Every caller reimplements the filter/order/orphan-exclusion contract
///   and silently diverges. F-7 was a direct consequence of this — the
///   FileSessionStore ConversationId-drop bug was invisible until every
///   call site happened to fail the same way.
///   </item>
/// </list>
/// <para>
/// This fence is intentionally narrow: it only flags methods whose body
/// contains ALL of the three telltales (<c>.ListAsync(</c>, <c>.Where(</c>,
/// <c>.ConversationId</c>). It will not catch sufficiently obfuscated
/// rewrites — but it catches the specific shape that has historically
/// reappeared at code-review time.
/// </para>
/// <para>
/// The fence runs on every cs file under <c>src/</c>. To resolve a failure:
/// rewrite the offending method to use
/// <see cref="BotNexus.Gateway.Abstractions.Sessions.ISessionStore.ListByConversationAsync"/>.
/// If you genuinely need every session in the store (e.g. orphan migration,
/// memory backfill, TTL cleanup), the lookup is not the F-7 pattern — add
/// the file to <see cref="AllowedFiles"/> with a short rationale comment.
/// </para>
/// </remarks>
public sealed class SessionConversationFilterArchitectureTests
{
    // Files genuinely doing a cross-conversation walk (orphan migration, memory backfill,
    // TTL cleanup, broader access-controlled listing) are NOT F-7 offenders. Add new
    // entries here only after confirming the caller really must see every session.
    private static readonly string[] AllowedFiles =
    {
        // Sqlite orphan migration: must see every conversation-less session to repair it.
        "SqliteSessionStore.cs",
        // Memory backfill: indexes every session by design.
        "MemoryIndexer.cs",
        // Access-controlled listing tool used by agents — not conversation-scoped.
        "SessionTool.cs",
        // Per-agent warmup — not conversation-scoped.
        "SessionWarmupService.cs",
        // Cross-conversation TTL scan.
        "SessionCleanupService.cs",
        // CronTrigger.NormalizeDuplicateCronConversationsAsync calls
        // _conversations.ListAsync(agentId) (Conversations, not Sessions) and dedupes by
        // Conversation.ConversationId. Its session-by-conversation lookups already use
        // ISessionStore.ListByConversationAsync. False positive at the file-grain because
        // the fence checks "any .ListAsync(" + ".Where(...ConversationId...)" in the same file.
        "CronTrigger.cs",
        // CronScheduler.ReconcileJobLegacyConversationsAsync (P9-D one-shot legacy migration)
        // walks every agent session by SessionId.StartsWith("cron:{jobIdSlug}:") prefix — this
        // is the only viable index for "find the sessions that belong to this cron job" because
        // legacy rows were spread across many per-agent legacy:* conversations after P9-B-1
        // backfill. The .Where(...ConversationId...) calls in the same file operate on the
        // conversations list returned by IConversationStore.ListAsync, NOT on the sessions
        // list. ListByConversationAsync would loop one call per legacy conversation per job
        // and still need a SessionId-prefix filter — strictly worse than the one-shot scan.
        "CronScheduler.cs",
    };

    /// <summary>
    /// No method body under <c>src/</c> may contain the F-7 anti-pattern:
    /// <c>sessions.ListAsync(...)</c> followed by a <c>.Where(...)</c> that
    /// filters on <c>.ConversationId</c>.
    /// </summary>
    [Fact]
    public void NoMethod_LoadsAllSessions_AndFiltersByConversationId()
    {
        var srcRoot = FindSourceRoot();

        // The F-7 fingerprint: a .Where(...) whose lambda directly accesses .ConversationId
        // with no intervening parens. This precisely matches the historic shape
        //   .Where(s => s.Session.ConversationId == x)
        //   .Where(s => s.Session.ConversationId.HasValue && s.Session.ConversationId.Value == x)
        // and (correctly) does NOT match downstream .Select(...) that exposes ConversationId
        // as a response field, because there's a closing/opening paren in the chain
        // between .Where(...) and that .ConversationId access.
        var whereByConvIdPattern = new Regex(
            @"\.Where\([^()]*?\.ConversationId",
            RegexOptions.Compiled | RegexOptions.Singleline);

        var offenders = new List<string>();
        foreach (var path in Directory
                     .EnumerateFiles(srcRoot, "*.cs", SearchOption.AllDirectories)
                     .Where(IsProductionSource)
                     .Where(p => !AllowedFiles.Contains(Path.GetFileName(p), StringComparer.Ordinal)))
        {
            var source = StripCommentsAndStrings(File.ReadAllText(path));

            // Require BOTH a .ListAsync(...) call AND a .Where(...) that filters
            // on .ConversationId in the SAME file. The .ListAsync requirement
            // distinguishes "load-from-store" from in-memory chained filters.
            if (!source.Contains(".ListAsync(", StringComparison.Ordinal))
                continue;
            if (!whereByConvIdPattern.IsMatch(source))
                continue;

            offenders.Add(Path.GetRelativePath(srcRoot, path));
        }

        offenders.Sort(StringComparer.Ordinal);
        offenders.ShouldBeEmpty(
            "Files under src/ contain the F-7 anti-pattern: a .ListAsync(...) call " +
            "in the same file as a .Where(...) lambda that filters on .ConversationId. " +
            "Use ISessionStore.ListByConversationAsync(conversationId, agentId?) instead — " +
            "it goes through the idx_sessions_conversation_agent SQLite index and " +
            "guarantees the Active+Sealed inclusion and CreatedAt-ascending ordering " +
            "contract. If the .ListAsync call is on IConversationStore (loading " +
            "conversations, not sessions) the false positive can be resolved by adding " +
            "the file to AllowedFiles with a short rationale.\n" +
            "Offenders:\n  " + string.Join("\n  ", offenders));
    }

    private static bool IsProductionSource(string path)
        => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
        && !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal);

    // Replace comments and string/char literals with whitespace so the .Where(...).ConversationId
    // pattern match isn't fooled by text appearing inside an error-message string literal
    // (e.g., this very test file's failure message). Cheap and deterministic; not a full lexer.
    private static string StripCommentsAndStrings(string source)
    {
        var buffer = new char[source.Length];
        var i = 0;
        while (i < source.Length)
        {
            // Line comment
            if (i + 1 < source.Length && source[i] == '/' && source[i + 1] == '/')
            {
                while (i < source.Length && source[i] != '\n') { buffer[i] = ' '; i++; }
                continue;
            }
            // Block comment
            if (i + 1 < source.Length && source[i] == '/' && source[i + 1] == '*')
            {
                while (i + 1 < source.Length && !(source[i] == '*' && source[i + 1] == '/'))
                {
                    buffer[i] = source[i] == '\n' ? '\n' : ' ';
                    i++;
                }
                if (i + 1 < source.Length) { buffer[i] = ' '; buffer[i + 1] = ' '; i += 2; }
                continue;
            }
            // Verbatim/interpolated/raw string-prefix tokens — fall through to the
            // regular string handler; we don't need 100% correctness, just brace neutralisation.
            // String literal
            if (source[i] == '"')
            {
                buffer[i] = ' ';
                i++;
                while (i < source.Length && source[i] != '"')
                {
                    if (source[i] == '\\' && i + 1 < source.Length)
                    {
                        buffer[i] = ' '; buffer[i + 1] = ' '; i += 2; continue;
                    }
                    buffer[i] = source[i] == '\n' ? '\n' : ' ';
                    i++;
                }
                if (i < source.Length) { buffer[i] = ' '; i++; }
                continue;
            }
            // Char literal
            if (source[i] == '\'')
            {
                buffer[i] = ' ';
                i++;
                while (i < source.Length && source[i] != '\'')
                {
                    if (source[i] == '\\' && i + 1 < source.Length)
                    {
                        buffer[i] = ' '; buffer[i + 1] = ' '; i += 2; continue;
                    }
                    buffer[i] = ' ';
                    i++;
                }
                if (i < source.Length) { buffer[i] = ' '; i++; }
                continue;
            }

            buffer[i] = source[i];
            i++;
        }
        return new string(buffer);
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
}
