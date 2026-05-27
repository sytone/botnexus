using System.Text;
using System.Text.RegularExpressions;
using Shouldly;

namespace BotNexus.Architecture.Tests;

/// <summary>
/// Architecture fitness function enforcing the F-9 / Phase 7 contract: production code
/// must mutate session state through the <c>GatewaySession</c> proxy, never by reaching
/// through to the inner <c>Session</c> record via <c>gatewaySession.Session.Field</c>.
/// </summary>
/// <remarks>
/// <para>
/// Before Phase 7 the codebase had ~25 reach-through call sites of the shape
/// <c>session.Session.ConversationId = ...</c>, <c>session.Session.UpdatedAt = ...</c>,
/// and <c>session.Session.AgentId</c>. The reach-throughs existed because the proxy was
/// missing a <c>ConversationId</c> facade — every caller that needed to read/write the
/// conversation id had to dive through the inner record. Once a few call sites had done
/// it for <c>ConversationId</c>, the same shape spread to fields that already had proxies
/// (laziness pattern).
/// </para>
/// <para>
/// The damage: thread-safety guarantees from <c>GatewaySessionRuntime</c> (the lock, the
/// stream replay buffer, the secret redactor) only fire if mutation happens through the
/// proxy. Reach-through writes bypass <em>all</em> of that quietly. PR #540 already had
/// to add a <c>ThreadSafeHistoryArchitectureTests</c> fence for the History case; this
/// fence generalises the same defence to every other proxied field.
/// </para>
/// <para>
/// The fence is intentionally narrow: only <c>GatewaySession.cs</c> and
/// <c>GatewaySessionRuntime.cs</c> (the proxy and its runtime) are allowed to use
/// <c>this.Session.X</c> internally — every other production file under <c>src/</c> must
/// route through the proxy properties. Tests are out of scope so behavioural pins like
/// <c>GatewaySessionBehaviorSnapshotTests</c> can still inspect the inner record.
/// </para>
/// <para>
/// The fence uses the same comment-stripping state-machine lexer from
/// <c>SingleShotWireValueArchitectureTests</c> (PR #569) so XML doc references to the
/// banned shape (e.g. inside <c>&lt;see cref="..."/&gt;</c> or prose comments) do not
/// false-positive. The matching regex <c>\.Session\b\s*!?\s*\.\s*\w+</c> permits the
/// null-forgiving operator (<c>session.Session!.X</c>) and inter-token whitespace, both
/// of which appear in real reach-through shapes. The word-boundary after <c>Session</c>
/// avoids matching <c>Sessions.X</c> (e.g. <c>ServiceCollection.Sessions.Foo</c>) and
/// <c>SessionStore.X</c>.
/// </para>
/// </remarks>
public sealed class GatewaySessionFacadeArchitectureTests
{
    private static readonly string[] s_allowlist =
    {
        // The proxy itself — its whole job is to talk to the inner Session.
        Path.Combine("domain", "BotNexus.Domain", "Gateway", "Models", "GatewaySession.cs"),
        // The runtime that owns the lock + history mutation; also a legitimate insider.
        Path.Combine("domain", "BotNexus.Domain", "Gateway", "Models", "GatewaySessionRuntime.cs"),
    };

    [Fact]
    public void NoProductionSourceFile_ReachesThroughGatewaySession_ToInnerRecord()
    {
        var srcRoot = FindSourceRoot();

        var violations = new List<string>();
        foreach (var path in EnumerateProductionCsFiles(srcRoot))
        {
            if (IsAllowlisted(path, srcRoot))
                continue;

            var stripped = StripComments(File.ReadAllText(path));
            if (ContainsReachThrough(stripped))
            {
                violations.Add(NormalizePath(path));
            }
        }

        violations.ShouldBeEmpty(
            "F-9 / Phase 7: production code must mutate session state through the " +
            "GatewaySession proxy properties — never by reaching through to the inner " +
            "record via `gatewaySession.Session.Field`. Reach-through writes bypass " +
            "the GatewaySessionRuntime lock, the stream replay buffer, and the secret " +
            "redactor. If a needed proxy is missing, add it to GatewaySession.cs (this " +
            "is how the ConversationId proxy was added in Phase 7) rather than reaching " +
            "through.\n" +
            "Violations:\n  " + string.Join("\n  ", violations));
    }

    [Fact]
    public void Fence_IsNotVacuous_AgainstSyntheticReachThrough()
    {
        // Synthetic post-revert shape: a typical reach-through write MUST trip the fence.
        // If this test fails, the fence has been weakened so far that the original
        // bug class is undetectable.
        const string syntheticViolation = """
            public void Pin(GatewaySession session, ConversationId? conversationId)
            {
                session.Session.ConversationId = conversationId;
            }
            """;
        ContainsReachThrough(StripComments(syntheticViolation)).ShouldBeTrue(
            "Vacuity guard: the fence must flag the canonical reach-through shape " +
            "`gatewaySession.Session.Field = ...`. If this assertion fails, the fence " +
            "is no longer detecting the very bug it was created to prevent.");
    }

    [Fact]
    public void Fence_DetectsReachThrough_ThroughNullForgivingOperator()
    {
        // Real call sites sometimes use the null-forgiving operator (e.g. after a null
        // check that the compiler cannot prove). The fence must catch this shape too.
        const string syntheticViolation = """
            var ts = session!.Session!.UpdatedAt;
            """;
        ContainsReachThrough(StripComments(syntheticViolation)).ShouldBeTrue(
            "Null-forgiving operator pin: `session!.Session!.UpdatedAt` is still a " +
            "reach-through. If this fails, the fence regex is missing the `!` allowance " +
            "and a realistic regression shape would bypass.");
    }

    [Fact]
    public void Fence_DoesNotFalsePositive_OnArgumentPassing()
    {
        // Passing the whole `session.Session` record as an argument to a method that
        // accepts `Session` is legitimate — the receiver gets the value record, not
        // the proxy, by design (e.g. persistence layer signatures). The fence matches
        // `.Session.<identifier>`, so `.Session)` (followed by close-paren or comma)
        // must not trip it.
        const string syntheticClean = """
            await _memoryFlusher.FlushAsync(session.AgentId, session.Session, options, ct);
            await store.SaveAsync(session.Session);
            DoStuff(session.Session, more, args);
            """;
        ContainsReachThrough(StripComments(syntheticClean)).ShouldBeFalse(
            "False-positive guard: passing `session.Session` as a bare argument " +
            "(without `.Field` after it) is legitimate — the fence specifically " +
            "targets reach-through reads/writes of the form `.Session.<identifier>`. " +
            "If this fails, the fence would block legitimate persistence-layer calls.");
    }

    [Fact]
    public void Fence_DoesNotFalsePositive_OnObjectInitializer()
    {
        // Constructing a GatewaySession via record initializer uses
        // `{ Session = ... }` which is a property assignment on the proxy, not a
        // reach-through. The fence regex requires `\.Session` (a member access on
        // some receiver), so initializer assignments without a receiver dot don't match.
        const string syntheticClean = """
            var gs = new GatewaySession { Session = inner };
            return gs with { Session = updated };
            """;
        ContainsReachThrough(StripComments(syntheticClean)).ShouldBeFalse(
            "False-positive guard: object/record initializer `{ Session = ... }` is " +
            "not a reach-through (no `.Session.Field` access chain). If this fails, " +
            "the fence would block legitimate construction.");
    }

    [Fact]
    public void Fence_DoesNotFalsePositive_OnSimilarNamedMembers()
    {
        // `Sessions.X`, `SessionStore.X`, `SessionId.X` etc. must NOT trip the fence —
        // the `\b` word boundary after `Session` is the discriminator. Without it,
        // `services.Sessions.X` would false-positive.
        const string syntheticClean = """
            var first = services.Sessions.First();
            var store = ctx.SessionStore.SaveAsync(x);
            var id = session.SessionId.Value;
            """;
        ContainsReachThrough(StripComments(syntheticClean)).ShouldBeFalse(
            "False-positive guard: `Sessions.X`, `SessionStore.X`, and `SessionId.X` " +
            "are NOT the inner-record reach-through. If this fails, the word-boundary " +
            "after `Session` has been lost and the fence would over-fire on unrelated " +
            "members.");
    }

    [Fact]
    public void Fence_DoesNotFalsePositive_OnCommentMention()
    {
        // XML doc and prose comments that mention `.Session.X` for historical reference
        // (e.g. documenting the F-9 ban) must NOT trip the fence — the comment stripper
        // removes them before regex matching.
        const string syntheticClean = """
            /// <summary>
            /// Banned shape under Phase 7: session.Session.ConversationId = id.
            /// Use the GatewaySession.ConversationId proxy instead.
            /// </summary>
            public void SafeSetter(GatewaySession s, ConversationId? id) => s.ConversationId = id;
            // Note: do not write session.Session.UpdatedAt directly — use the proxy.
            """;
        ContainsReachThrough(StripComments(syntheticClean)).ShouldBeFalse(
            "False-positive guard: XML doc and `//` comments referencing the banned " +
            "shape for historical context must not trip the fence. If this fails, the " +
            "comment-stripping lexer has regressed.");
    }

    private static bool ContainsReachThrough(string source)
    {
        // The reach-through shape: a member access ending in `.Session` (with optional
        // null-forgiving `!`) followed by another `.identifier` access. The `\b` after
        // `Session` prevents matching `Sessions.X` / `SessionStore.X` / `SessionId.X`.
        return Regex.IsMatch(source, @"\.Session\b\s*!?\s*\.\s*\w+");
    }

    private static bool IsAllowlisted(string path, string srcRoot)
    {
        var relative = Path.GetRelativePath(srcRoot, path);
        foreach (var allowed in s_allowlist)
        {
            if (string.Equals(relative, allowed, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Removes single-line (<c>//</c>, <c>///</c>) and block (<c>/* … */</c>) C# comments
    /// while preserving string and char literals. Required so XML docs and prose comments
    /// that legitimately mention the banned shape do not false-positive the fence.
    /// Identical lexer to <c>SingleShotWireValueArchitectureTests.StripComments</c> (PR #569).
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
