using System.Text;
using System.Text.RegularExpressions;
using Shouldly;

namespace BotNexus.Architecture.Tests;

/// <summary>
/// Architecture fitness function enforcing PR <c>#575</c>: the 8 stream-replay
/// members previously hosted on <c>GatewaySession</c> / <c>GatewaySessionRuntime</c>
/// (<c>NextSequenceId</c>, <c>StreamEventLog</c>, <c>ReplayBuffer</c>,
/// <c>AllocateSequenceId</c>, <c>AddStreamEvent</c>, <c>GetStreamEventsAfter</c>,
/// <c>GetStreamEventSnapshot</c>, <c>SetStreamReplayState</c>) collapse to a single
/// <c>StreamReplay</c> peer accessor; the renamed surface is
/// <c>StreamReplay.AddEvent</c>, <c>StreamReplay.GetEventsAfter</c>,
/// <c>StreamReplay.GetEventSnapshot</c>, <c>StreamReplay.SetState</c>.
/// </summary>
/// <remarks>
/// <para>
/// Production source under <c>src/</c> must not contain a member-access shape
/// referencing any of the 8 legacy names. The only allowlisted file is the new
/// <c>SessionStreamReplay.cs</c>, which legitimately calls
/// <c>_buffer.AllocateSequenceId</c> / <c>_buffer.AddStreamEvent</c> / etc. as the
/// composition target of the extract. <c>SessionReplayBuffer.cs</c> only contains
/// declarations (no <c>.X</c> access shapes) so it does not need an allowlist
/// entry. <c>GatewaySessionRuntime.cs</c> post-extract owns the type by
/// construction only and does not call any legacy member access shape.
/// </para>
/// <para>
/// The <c>.ReplayBuffer</c> ban (inherent in the 8-name list) closes the
/// reach-through leak that <c>GatewaySessionThreadSafetyTests</c> previously
/// exploited at <c>session.ReplayBuffer.AddStreamEvent(...)</c>, bypassing the
/// <c>UpdatedAt</c>-stamping facade.
/// </para>
/// <para>
/// The fence scans <c>src/</c> only so <c>SessionReplayBufferTests</c> (which
/// legitimately uses the underlying <c>SessionReplayBuffer</c> type via
/// <c>new SessionReplayBuffer()</c>) is not false-positively flagged.
/// </para>
/// <para>
/// Comment stripping reuses the string-aware state-machine lexer from
/// <see cref="SingleShotWireValueArchitectureTests"/> to defend against the
/// realistic regression shape <c>var u = "https://x"; session.AddStreamEvent(...)</c>
/// where a naive line-comment regex would strip from the first <c>//</c> inside
/// the URL string to end-of-line, hiding the violation.
/// </para>
/// </remarks>
public sealed class SessionStreamReplayArchitectureTests
{
    // Negative lookbehind on `\.StreamReplay` distinguishes new facade access
    // (`session.StreamReplay.NextSequenceId` — allowed) from legacy direct access
    // (`session.NextSequenceId` — banned). .NET regex supports fixed-width lookbehind,
    // and `.StreamReplay` is 13 characters fixed-width. The `\b` after the member name
    // prevents prefix-collision matches like `.AddStreamEventBatch`.
    private static readonly Regex s_legacyMemberAccess = new(
        @"(?<!\.StreamReplay)\.(NextSequenceId|StreamEventLog|ReplayBuffer|AllocateSequenceId|AddStreamEvent|GetStreamEventsAfter|GetStreamEventSnapshot|SetStreamReplayState)\b",
        RegexOptions.Compiled);

    private static readonly HashSet<string> s_allowlistFilenames = new(StringComparer.OrdinalIgnoreCase)
    {
        // The new extracted facade is the single composition root that legitimately
        // calls the underlying SessionReplayBuffer members (`_buffer.AddStreamEvent`,
        // `_buffer.AllocateSequenceId`, etc.) via field-access (`_buffer.X`) — which
        // is NOT covered by the `(?<!\.StreamReplay)` guard. No other file under src/
        // should reach into the buffer or the legacy access shapes post-#575.
        "SessionStreamReplay.cs",
    };

    [Fact]
    public void NoProductionSourceFile_ReferencesLegacyStreamReplayMembersByAccess()
    {
        var srcRoot = FindSourceRoot();

        var violations = new List<string>();
        foreach (var path in EnumerateProductionCsFiles(srcRoot))
        {
            var filename = Path.GetFileName(path);
            if (s_allowlistFilenames.Contains(filename))
                continue;

            var stripped = StripComments(File.ReadAllText(path));
            if (s_legacyMemberAccess.IsMatch(stripped))
            {
                violations.Add(NormalizePath(path));
            }
        }

        violations.ShouldBeEmpty(
            "The 8 legacy stream-replay members on GatewaySession / GatewaySessionRuntime " +
            "(NextSequenceId, StreamEventLog, ReplayBuffer, AllocateSequenceId, AddStreamEvent, " +
            "GetStreamEventsAfter, GetStreamEventSnapshot, SetStreamReplayState) were extracted " +
            "to `session.StreamReplay.*` in #575. Production code must funnel through the new " +
            "facade — `session.StreamReplay.AddEvent`, `.GetEventsAfter`, `.GetEventSnapshot`, " +
            "`.SetState`. The `.ReplayBuffer` reach-through is structurally closed; any " +
            "regression is a real bypass of the UpdatedAt-stamping layer.\n" +
            "Violations:\n  " + string.Join("\n  ", violations));
    }

    [Fact]
    public void Fence_IsNotVacuous_AgainstSyntheticRegression()
    {
        // Synthetic post-revert shape: code calling any of the 8 legacy members MUST trip
        // the fence. If this test fails, the fence has been weakened enough that the
        // original regression class is undetectable.
        const string syntheticViolation = """
            public void Persist(GatewaySession session)
            {
                var nextId = session.NextSequenceId;
                var snapshot = session.GetStreamEventSnapshot();
                session.SetStreamReplayState(nextId, snapshot);
            }
            """;
        s_legacyMemberAccess.IsMatch(StripComments(syntheticViolation)).ShouldBeTrue(
            "Vacuity guard: the fence must flag any production code accessing the legacy " +
            "stream-replay members on GatewaySession. If this assertion fails, the regex " +
            "has been neutered and a #575 regression would be undetectable.");
    }

    [Fact]
    public void Fence_IsNotVacuous_AgainstReachThroughLeak()
    {
        // The reach-through shape `session.ReplayBuffer.AddStreamEvent(...)` was the
        // specific leak GatewaySessionThreadSafetyTests previously exploited at L169-171
        // pre-#575 — it bypassed the UpdatedAt-stamping facade. The fence must catch
        // BOTH `.ReplayBuffer` access AND the chained `.AddStreamEvent` call.
        const string syntheticViolation = """
            session.ReplayBuffer.AddStreamEvent(1, "{}", 10);
            """;
        s_legacyMemberAccess.IsMatch(StripComments(syntheticViolation)).ShouldBeTrue(
            "Reach-through guard: the fence must flag `session.ReplayBuffer.X` access. " +
            "If this fails, the leak that motivated the entire extract has been re-opened " +
            "without detection.");
    }

    [Fact]
    public void Fence_DoesNotFalsePositive_OnNewFacadeUsage()
    {
        // Canonical post-#575 shape: code routing through the new facade
        // `session.StreamReplay.X` must NOT trip the fence. The `(?<!\.StreamReplay)`
        // negative lookbehind in the regex blocks matches preceded by `.StreamReplay`,
        // so the facade-access surface (the only valid post-#575 shape) cleanly passes.
        const string syntheticClean = """
            public void Persist(GatewaySession session)
            {
                var nextId = session.StreamReplay.NextSequenceId;
                var snapshot = session.StreamReplay.GetEventSnapshot();
                session.StreamReplay.SetState(nextId, snapshot);
                session.StreamReplay.AddEvent(nextId, "{}", 10);
                var after = session.StreamReplay.GetEventsAfter(0, 10);
            }
            """;
        s_legacyMemberAccess.IsMatch(StripComments(syntheticClean)).ShouldBeFalse(
            "Facade-access guard: routing through `session.StreamReplay.X` must not be " +
            "flagged. If this fails, the negative-lookbehind on `.StreamReplay` has " +
            "regressed and every FileSessionStore / GatewaySession call into the facade " +
            "would be a false positive.");
    }

    [Fact]
    public void Fence_DoesNotFalsePositive_OnFileSessionStoreShape()
    {
        // FileSessionStore is the only production persistence caller of the facade. Its
        // exact post-migration shape must not trip the fence — if it does, the only
        // legitimate caller cannot exist outside the allowlist, which would force the
        // entire `src/gateway/BotNexus.Gateway.Sessions/` to be allowlisted (defeating
        // the fence). This is the round-trip pin for the negative-lookbehind guard.
        const string fileStoreShape = """
            var meta = new SessionMeta(
                session.AgentId,
                session.ConversationId,
                session.StreamReplay.NextSequenceId,
                [.. session.StreamReplay.GetEventSnapshot()],
                session.Metadata);
            """;
        s_legacyMemberAccess.IsMatch(StripComments(fileStoreShape)).ShouldBeFalse(
            "FileSessionStore round-trip pin: the canonical `session.StreamReplay.X` " +
            "shape used by the only persistence caller must not be flagged. If this " +
            "fails, the lookbehind guard is broken and the fence would block every " +
            "legitimate facade caller.");
    }

    [Fact]
    public void Fence_IsNotVacuous_AgainstSyntheticReplayBufferAccess_OutsideAllowlist()
    {
        // Synthetic violation: any file outside the allowlist that returns the raw
        // SessionReplayBuffer (re-introducing the leak) must trip the `.ReplayBuffer`
        // rule. This guards against a regression where someone re-adds the proxy on
        // GatewaySession or exposes it via a different type.
        const string syntheticViolation = """
            public SessionReplayBuffer ReplayBuffer => _runtime.ReplayBuffer;
            """;
        s_legacyMemberAccess.IsMatch(StripComments(syntheticViolation)).ShouldBeTrue(
            "Reach-through fence: any expression matching `.ReplayBuffer` must trip the " +
            "fence outside the allowlist. If this fails, the raw buffer can be re-leaked " +
            "via a new property and the structural protection of #575 is gone.");
    }

    [Fact]
    public void Fence_DoesNotFalsePositive_OnCommentMention()
    {
        // Synthetic clean shape: a comment mentioning a legacy member name for historical
        // reference must NOT trip the fence — the string-aware stripper removes comments
        // before the regex applies. This is what allows the rename to be documented in
        // place without forcing future authors to remove explanatory text.
        const string syntheticClean = """
            // Legacy `session.AddStreamEvent` and `session.GetStreamEventSnapshot` were
            // extracted to `session.StreamReplay.AddEvent` / `.GetEventSnapshot` in #575.
            public void Noop() { }
            """;
        s_legacyMemberAccess.IsMatch(StripComments(syntheticClean)).ShouldBeFalse(
            "False-positive guard: the fence must not flag comment-only mentions of the " +
            "legacy member names. If this fails, the comment stripper has regressed and " +
            "the fence will block PRs that legitimately document the rename.");
    }

    [Fact]
    public void Fence_IsNotVacuous_AgainstRawStringBypassShape()
    {
        // C# 11+ raw string literals (3+ consecutive `"`) could otherwise create a
        // bypass: without raw-string-aware lexing, the contrived shape
        //
        //   var s = """a"//x"""; session.AddStreamEvent(...);
        //
        // is misparsed by a 1/2-quote-only lexer as `""` + `"a"` + `/` + `/x"""...`
        // — the `//` after the inner `"` is treated as a real line comment that
        // strips the trailing legacy-member call through to end-of-line, hiding the
        // violation. Surfaced by the #575 bug-hunt critique. After raw-string-aware
        // stripping the full call survives in the residue and the fence catches it.
        const string bypassShape =
            "var s = \"\"\"a\"//x\"\"\"; session.AddStreamEvent(1, \"{}\", 10);";
        s_legacyMemberAccess.IsMatch(StripComments(bypassShape)).ShouldBeTrue(
            "Raw-string-bypass guard: a trailing legacy-member call after a raw " +
            "string literal containing `\"//` must remain visible to the fence " +
            "regex after comment stripping. If this fails, the raw-string branch " +
            "of `StripComments` has regressed and contrived strings could mask " +
            "real legacy-member calls on the same line.");
    }

    [Fact]
    public void Fence_DoesNotFalsePositive_OnRawStringContent()
    {
        // Canonical clean shape: a raw string literal whose content happens to
        // contain a `//` or `/* */` sequence must not have that sequence
        // mis-stripped as a comment, AND the literal itself (which has no
        // banned-member call after it) must not trip the fence. This pins the
        // realistic production case — tool-description JSON schemas in
        // `BotNexus.Tools/*Tool.cs` use multi-line raw strings with embedded
        // URLs (`https://`) and comment-like fragments.
        const string cleanShape = "var schema = \"\"\"\nhello // world\n/* block */\n\"\"\";\n";
        s_legacyMemberAccess.IsMatch(StripComments(cleanShape)).ShouldBeFalse(
            "Raw-string false-positive guard: content inside a raw string literal " +
            "must not be parsed as code, and embedded `//` / `/* */` sequences must " +
            "not be stripped. If this fails, raw-string JSON schemas in tool " +
            "implementations would either false-positive or mask real violations.");
    }

    /// <summary>
    /// Removes single-line (<c>//</c>, <c>///</c>) and block (<c>/* … */</c>) C# comments
    /// while preserving the contents of string and char literals — including C# 11+
    /// raw string literals (<c>"""…"""</c>). Reused (verbatim shape) from
    /// <see cref="SingleShotWireValueArchitectureTests"/> with one additional branch:
    /// see that file for the rationale on why a naive regex would miss the realistic
    /// regression shape <c>var u = "https://x"; session.X(...)</c>. The raw-string
    /// branch closes a further bypass surfaced in the #575 bug-hunt critique sweep:
    /// without raw-string-aware lexing, the contrived shape
    /// <c>var s = """a"//x"""; session.X(...)</c> is misparsed as two regular
    /// strings followed by a line comment that strips the trailing call. Other
    /// architecture fences in this folder share this lexer pattern and have the
    /// same pre-#575 limitation; lifting the raw-string branch to them is tracked
    /// as a separate follow-up.
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
                // Detect raw string literal: 3+ consecutive double quotes open a raw
                // string whose closer is any run of >= openCount consecutive `"`
                // characters. Content of any length (including embedded `"`, `//`,
                // and `/* */` sequences) is preserved verbatim into the residue so
                // the regex sees the same shape as the original source.
                var openCount = 1;
                while (i + openCount < n && source[i + openCount] == '"') openCount++;
                if (openCount >= 3)
                {
                    sb.Append(source, i, openCount);
                    i += openCount;
                    while (i < n)
                    {
                        if (source[i] == '"')
                        {
                            var closeCount = 0;
                            while (i + closeCount < n && source[i + closeCount] == '"') closeCount++;
                            sb.Append(source, i, closeCount);
                            i += closeCount;
                            if (closeCount >= openCount) break;
                            continue;
                        }
                        sb.Append(source[i++]);
                    }
                    continue;
                }

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
