using System.Text;
using System.Text.RegularExpressions;
using Shouldly;

namespace BotNexus.Architecture.Tests;

/// <summary>
/// Architecture fitness function enforcing the <c>#580</c> sub-PR 6.1 contract: production
/// source code must not read the legacy weakly-typed <c>InboundMessage.{TargetAgentId, SessionId,
/// ConversationId}</c> override fields. The typed equivalents on <see langword="InboundMessageContext"/>
/// (<c>RequestedAgentId</c>, <c>RequestedSessionId</c>, <c>RequestedConversationId</c>) and on the
/// new <see langword="InboundMessageRoutingHints"/> projection carry the same routing intent in
/// Vogen-typed shape and are the only sanctioned readers.
/// </summary>
/// <remarks>
/// <para>
/// The legacy fields remain on <see langword="InboundMessage"/> for one umbrella-issue (#579)
/// cycle so adapter writers (Telegram, SignalR, ServiceBus, internal channel adapter) can
/// continue populating them while the migration is in flight. Sub-PR 6.2 (issue
/// <c>#582</c>) introduced <c>InboundMessageRoutingHints</c> as the sanctioned single reader and
/// migrated <see langword="DefaultMessageRouter"/> and <see langword="GatewayHost"/> through it;
/// at that point <see langword="InboundMessageContext.FromInboundMessage"/> stopped reading the
/// legacy fields directly (it delegates to the hints helper) and dropped off the allowlist.
/// Sub-PR 6.3 deletes the legacy fields entirely and this fence becomes trivially vacuous.
/// </para>
/// <para>
/// The fence bans the pattern <c>message.&lt;FieldName&gt;</c> outside an allowlist. The
/// allowlist has two categories: (1) the single sanctioned reader
/// (<c>InboundMessageRoutingHints.FromMessage</c>) which exists precisely to lift the legacy
/// fields into typed routing hints once at the routing boundary, and (2) the
/// <c>OutboundMessage</c> adapter readers that share property names with <c>InboundMessage</c>
/// but operate on a distinct type and a different routing axis. After sub-PR 6.2 the allowlist
/// shrunk from 6 entries to 4.
/// </para>
/// </remarks>
public sealed class InboundMessageOverrideFenceTests
{
    /// <summary>
    /// Source files allowed to read the legacy <c>InboundMessage</c> override fields, with the
    /// rationale recorded inline. The list must shrink monotonically across sub-PRs 6.2 / 6.3.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> s_allowlist = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        // The sanctioned single reader (#582 sub-PR 6.2): InboundMessageRoutingHints.FromMessage
        // is the WHOLE POINT — it reads these three fields ONCE and projects them into typed
        // Vogen-shaped RequestedAgentId / RequestedSessionId / RequestedConversationId. Every
        // production consumer (DefaultMessageRouter, GatewayHost, InboundMessageContext) routes
        // through this helper instead of touching the legacy fields directly. Sub-PR 6.3 deletes
        // the legacy fields and this entry becomes vacuous (no fields to read).
        ["src/gateway/BotNexus.Gateway.Dispatching/InboundMessageRoutingHints.cs"] = "Sanctioned single reader — projects legacy override fields into typed routing hints at the routing boundary.",

        // OUT OF SCOPE for Phase 6 / F-10 entirely: these three adapters read message.SessionId
        // on OutboundMessage (NOT InboundMessage). The legacy override fields targeted by #580
        // are an InboundMessage migration; OutboundMessage carries SessionId as a legitimate
        // routing field at the outbound boundary (the gateway tells the adapter which session
        // a streamed reply belongs to). The fence regex anchors on `message.` and cannot
        // syntactically distinguish between an InboundMessage and an OutboundMessage receiver
        // with the same parameter name. If an OutboundMessage routing-field cleanup happens
        // in a future phase, these entries should be revisited; for now they are correct usage
        // of a distinct routing axis.
        ["src/extensions/BotNexus.Extensions.Channels.ServiceBus/ServiceBusChannelAdapter.cs"] = "OUT OF SCOPE: reads OutboundMessage.SessionId — not InboundMessage. Different type, same property name; fence cannot syntactically disambiguate.",
        ["src/extensions/BotNexus.Extensions.Channels.SignalR/SignalRChannelAdapter.cs"] = "OUT OF SCOPE: reads OutboundMessage.SessionId — not InboundMessage. Different type, same property name; fence cannot syntactically disambiguate.",
        ["src/gateway/BotNexus.Gateway/Channels/InternalChannelAdapter.cs"] = "OUT OF SCOPE: reads OutboundMessage.SessionId — not InboundMessage. Different type, same property name; fence cannot syntactically disambiguate.",
    };

    [Fact]
    public void NoProductionSourceFile_OutsideAllowlist_ReadsLegacyOverrideFields()
    {
        var srcRoot = FindSourceRoot();

        var violations = new List<string>();
        foreach (var path in EnumerateProductionCsFiles(srcRoot))
        {
            var rel = NormalizeRelativePath(srcRoot, path);
            if (s_allowlist.ContainsKey(rel))
            {
                continue;
            }

            var stripped = StripComments(File.ReadAllText(path));
            if (ContainsLegacyOverrideRead(stripped))
            {
                violations.Add(rel);
            }
        }

        violations.ShouldBeEmpty(
            "Reads of the legacy `InboundMessage.{TargetAgentId,SessionId,ConversationId}` " +
            "fields were replaced with the typed `InboundMessageRoutingHints` projection " +
            "(introduced in #580 sub-PR 6.1; expanded in #582 sub-PR 6.2 to cover the router " +
            "and gateway-host call sites). If you must add a new reader, either:\n" +
            "  (a) consume the typed properties via " +
            "`InboundMessageRoutingHints.FromMessage(message).Requested{Agent,Session,Conversation}Id` " +
            "(or `InboundMessageContext.FromInboundMessage(...)` if you already have an AgentId), or\n" +
            "  (b) add an explicit allowlist entry with an issue link explaining why the typed " +
            "path is not yet workable.\n" +
            "Adding without an allowlist entry will fail CI. Sub-PR 6.3 will delete the legacy " +
            "fields entirely, at which point this fence becomes vacuous.\n" +
            "Violations:\n  " + string.Join("\n  ", violations));
    }

    [Fact]
    public void Allowlist_ReferencesOnlyExistingFiles()
    {
        // Vacuity guard #1: the allowlist must not accumulate stale entries that quietly grant
        // permission to files that no longer exist. If a file is renamed or deleted, the entry
        // here must move with it — otherwise a future file at the same path silently inherits
        // the legacy-read allowance.
        var srcRoot = FindSourceRoot();
        var missing = new List<string>();
        foreach (var rel in s_allowlist.Keys)
        {
            var full = Path.Combine(srcRoot, "..", rel.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(Path.GetFullPath(full)))
            {
                missing.Add(rel);
            }
        }

        missing.ShouldBeEmpty(
            "Allowlist hygiene: every entry must point to an existing source file. " +
            "Stale entries silently grant the legacy-read allowance to whatever future file " +
            "lands at that path. Update the allowlist when files move.\nMissing:\n  " +
            string.Join("\n  ", missing));
    }

    [Fact]
    public void Fence_IsNotVacuous_AgainstSyntheticRegression()
    {
        // Vacuity guard #2: the fence must flag the canonical pre-#580 shapes — reading
        // message.TargetAgentId / message.SessionId / message.ConversationId outside the
        // shim. If this assertion fails, the fence regex has been weakened so far that the
        // original regression class is undetectable.
        const string syntheticTargetAgent = """
            var agent = message.TargetAgentId;
            """;
        const string syntheticSession = """
            var sid = message.SessionId;
            """;
        const string syntheticConv = """
            if (message.ConversationId is { } id) { /* ... */ }
            """;

        ContainsLegacyOverrideRead(StripComments(syntheticTargetAgent)).ShouldBeTrue(
            "Vacuity guard: the fence must flag `message.TargetAgentId` reads. If this fails, " +
            "the regex no longer catches the canonical pre-#580 shape and the entire purpose " +
            "of the fence is defeated.");
        ContainsLegacyOverrideRead(StripComments(syntheticSession)).ShouldBeTrue(
            "Vacuity guard: the fence must flag `message.SessionId` reads. If this fails, " +
            "the regex no longer catches the canonical pre-#580 shape.");
        ContainsLegacyOverrideRead(StripComments(syntheticConv)).ShouldBeTrue(
            "Vacuity guard: the fence must flag `message.ConversationId` reads. If this fails, " +
            "the regex no longer catches the canonical pre-#580 shape.");
    }

    [Fact]
    public void Fence_DetectsLegacyRead_NullConditional()
    {
        // Critique-sweep fold-in (#580 bug-hunt HIGH): null-conditional reads
        // (`message?.SessionId`) are a real-world C# idiom that the original
        // strict `message.` regex missed. This shape MUST trip the fence;
        // otherwise legacy reads can slip past unnoticed in any code path that
        // treats `message` as nullable.
        const string syntheticNullConditional = """
            var sid = message?.SessionId;
            """;
        ContainsLegacyOverrideRead(StripComments(syntheticNullConditional)).ShouldBeTrue(
            "Null-conditional vacuity pin: `message?.SessionId` MUST be detected. Without " +
            "this, any code reading the legacy field through a nullable reference silently " +
            "bypasses the migration fence.");
    }

    [Fact]
    public void Fence_DetectsLegacyRead_WhitespaceAroundDot()
    {
        // Critique-sweep fold-in (#580 bug-hunt HIGH): valid C# tolerates whitespace
        // between the receiver and the member-access dot. The fence must too.
        const string syntheticWhitespaced = """
            var sid = message .SessionId;
            """;
        ContainsLegacyOverrideRead(StripComments(syntheticWhitespaced)).ShouldBeTrue(
            "Whitespace-tolerant vacuity pin: `message .SessionId` (with space before the " +
            "dot) is legal C# and MUST trip the fence. Without this, a reformatter or a " +
            "deliberate spacing convention silently bypasses the migration.");
    }

    [Fact]
    public void Fence_DetectsLegacyRead_PropertyPattern()
    {
        // Critique-sweep fold-in (#580 bug-hunt HIGH): C# property patterns
        // (`message is { SessionId: { } sid }`) are reads — there is no equivalent
        // write form. The fence must detect them; without this, modern pattern-matching
        // code can deconstruct the legacy fields without tripping the migration.
        const string syntheticPropertyPattern = """
            if (message is { SessionId: { } sid }) { /* ... */ }
            """;
        const string syntheticMultiFieldPattern = """
            if (message is { Foo: var f, TargetAgentId: { } agent }) { /* ... */ }
            """;
        const string syntheticMultilinePattern = """
            if (message is
                {
                    ConversationId: { } conv
                }) { /* ... */ }
            """;

        ContainsLegacyOverrideRead(StripComments(syntheticPropertyPattern)).ShouldBeTrue(
            "Property-pattern vacuity pin: `message is { SessionId: ... }` is a READ shape " +
            "and MUST trip the fence. Pattern matching is increasingly idiomatic; missing " +
            "this would leave a large class of legacy reads undetectable.");
        ContainsLegacyOverrideRead(StripComments(syntheticMultiFieldPattern)).ShouldBeTrue(
            "Multi-field property-pattern vacuity pin: `message is { Foo: …, TargetAgentId: …}` " +
            "MUST trip even when the legacy field is not the first property in the pattern.");
        ContainsLegacyOverrideRead(StripComments(syntheticMultilinePattern)).ShouldBeTrue(
            "Multi-line property-pattern vacuity pin: pattern matches MUST work when the " +
            "pattern spans multiple lines — real-world property patterns often do.");
    }

    [Fact]
    public void Fence_DoesNotFalsePositive_OnAssignmentToLegacyField()
    {
        // False-positive guard #1: WRITING to the legacy fields is fine — adapters still
        // populate them on the InboundMessage they construct, and the shim reads them once.
        // The fence must only flag READS of the form `message.<Field>` not WRITES like
        // `... = message; message.<Field> = ...;` or object-initialiser `{ TargetAgentId = X }`.
        const string syntheticClean = """
            var msg = new InboundMessage
            {
                TargetAgentId = "agent-a",
                SessionId = "sess-1",
                ConversationId = "conv-1",
            };
            """;
        ContainsLegacyOverrideRead(StripComments(syntheticClean)).ShouldBeFalse(
            "False-positive guard: object-initialiser writes to the legacy fields must not " +
            "trip the fence. Adapter code still populates these fields; only reads are banned. " +
            "If this fails, the fence has been broadened to ban all references.");
    }

    [Fact]
    public void Fence_DoesNotFalsePositive_OnUnrelatedReceiver()
    {
        // False-positive guard #2: reads of identically-named properties on different
        // receivers (e.g. `result.SessionId`, `context.ConversationId`, `request.TargetAgentId`)
        // are unrelated to the InboundMessage shape and must not be flagged. The fence is
        // anchored specifically on the `message.` receiver.
        const string syntheticClean = """
            var sessionId = result.SessionId;
            var convId = context.ConversationId;
            var agentId = request.TargetAgentId;
            """;
        ContainsLegacyOverrideRead(StripComments(syntheticClean)).ShouldBeFalse(
            "False-positive guard: reads on receivers other than `message.` are unrelated " +
            "to the InboundMessage override fields and must not be flagged. If this fails, " +
            "the fence has been broadened to ban a substring rather than the anchored pattern.");
    }

    [Fact]
    public void Fence_DoesNotFalsePositive_OnCommentMention()
    {
        // False-positive guard #3: a deprecation comment that MENTIONS `message.SessionId`
        // or similar as historical context is fine — the comment stripper removes it before
        // the fence applies. Authors must be able to document the migration in comments
        // without tripping the rule.
        const string syntheticClean = """
            /// <summary>
            /// Replaces the pre-#580 pattern `message.TargetAgentId` / `message.SessionId` /
            /// `message.ConversationId` with the typed RequestedAgentId / RequestedSessionId /
            /// RequestedConversationId on InboundMessageContext.
            /// </summary>
            public AgentId AgentId { get; }
            """;
        ContainsLegacyOverrideRead(StripComments(syntheticClean)).ShouldBeFalse(
            "False-positive guard: comment-only mentions of the legacy shape must not trip " +
            "the fence. If this fails, the comment stripper has regressed and the fence will " +
            "block PRs that legitimately document the migration history.");
    }

    [Fact]
    public void Fence_KnownLimitation_AliasedReceiverEscapes()
    {
        // Bug-hunt critique fold-in (#582 sub-PR 6.2): the regex-based fence is anchored on
        // the literal `message` identifier so it CANNOT detect a deliberate alias such as
        // `var m = message; var sid = m.SessionId;`. Detecting alias chains requires symbol
        // tracking (Roslyn-semantic analysis), which is out of scope for the transitional
        // fence. The mitigation is sub-PR 6.3 (next PR): once the legacy fields are deleted
        // from InboundMessage, the C# compiler enforces the contract directly and any alias
        // bypass becomes a hard compile error. This pin documents the known limitation so a
        // future contributor who tries to "fix" the regex understands the trade-off.
        const string syntheticAlias = """
            var m = message;
            var sid = m.SessionId;
            """;
        ContainsLegacyOverrideRead(StripComments(syntheticAlias)).ShouldBeFalse(
            "Known-limitation pin: the regex fence does NOT detect aliased reads. This is " +
            "deliberate — symbol tracking is out of scope for a transitional fence whose " +
            "mitigation is sub-PR 6.3's field deletion. If this assertion starts FAILING " +
            "because the regex was extended to catch aliases, that's an improvement: update " +
            "this test to ShouldBeTrue and add a vacuity guard for the new shape.");
    }

    [Fact]
    public void Fence_KnownLimitation_CastReceiverEscapes()
    {
        // Bug-hunt critique fold-in (#582 sub-PR 6.2): companion to the aliased-receiver
        // pin above. A cast-then-read shape `((InboundMessage)obj).SessionId` slips past
        // the receiver-anchored regex. Same mitigation: sub-PR 6.3's field deletion makes
        // any such read a compile error. Pinned as a known limitation, not a defect.
        const string syntheticCast = """
            var sid = ((InboundMessage)obj).SessionId;
            """;
        ContainsLegacyOverrideRead(StripComments(syntheticCast)).ShouldBeFalse(
            "Known-limitation pin: the regex fence does NOT detect cast-then-read shapes. " +
            "Same trade-off as the aliased-receiver limitation; sub-PR 6.3's field deletion " +
            "is the structural mitigation. If this starts FAILING because the regex was " +
            "extended, update this test to ShouldBeTrue.");
    }

    /// <summary>
    /// Matches reads of the form <c>message.TargetAgentId</c>, <c>message?.SessionId</c>, or
    /// <c>message is { ConversationId: …}</c> — but not writes (those have <c>=</c>
    /// immediately after the field name in source). Anchored on the <c>message</c> receiver
    /// (with optional null-conditional and whitespace) so unrelated properties on other
    /// receivers (<c>result.SessionId</c>, <c>request.TargetAgentId</c>) are not flagged.
    /// </summary>
    /// <remarks>
    /// Three syntactic shapes are covered:
    /// <list type="bullet">
    /// <item><description>Member access (dot or null-conditional): <c>message.X</c>, <c>message?.X</c>, <c>message .X</c></description></item>
    /// <item><description>Property pattern: <c>message is { X: …</c> and <c>message is { …, X: …</c></description></item>
    /// </list>
    /// Bug-hunt critique fold-in (#580 critique sweep, HIGH): the prior regex only caught the
    /// strict <c>message.X</c> form. Null-conditional reads (<c>message?.SessionId</c>) and
    /// pattern-matched reads (<c>message is { SessionId: { } sid }</c>) silently slipped past.
    /// The vacuity guards below pin all three shapes.
    /// </remarks>
    private static bool ContainsLegacyOverrideRead(string source)
    {
        // Shape 1: member access with optional null-conditional and whitespace.
        //   message.X        — classic
        //   message?.X       — null-conditional
        //   message .X       — odd whitespace, still valid C#
        //   message?  . X    — odd whitespace + null-conditional, still valid C#
        // The trailing negative lookahead `(?!\s*=\s*[^=])` excludes assignments while still
        // accepting `==` comparisons (the second `=` is captured by `[^=]` exclusion).
        if (Regex.IsMatch(
            source,
            @"\bmessage\s*\??\s*\.\s*(TargetAgentId|SessionId|ConversationId)\b(?!\s*=\s*[^=])"))
        {
            return true;
        }

        // Shape 2: property pattern read.
        //   message is { TargetAgentId: …}
        //   message is { Foo: x, SessionId: { } sid }
        // Pattern matching is a READ shape — there is no equivalent write form, so no
        // exclusion lookahead is needed. We use Singleline mode so the pattern can span
        // multiple lines (real property patterns often do).
        return Regex.IsMatch(
            source,
            @"\bmessage\s+is\s*\{[^}]*\b(TargetAgentId|SessionId|ConversationId)\s*:",
            RegexOptions.Singleline);
    }

    /// <summary>
    /// Removes single-line (<c>//</c>, <c>///</c>) and block (<c>/* … */</c>) C# comments
    /// while preserving the contents of string and char literals. Handles regular strings
    /// (with <c>\\</c>/<c>\"</c> escapes), verbatim strings (<c>@"…"</c> with <c>""</c>-escaped
    /// quotes), and char literals. Required to avoid false-negatives where a <c>//</c> inside
    /// a URL string literal would otherwise be treated as a comment-start and silently strip
    /// real production code (see <c>SingleShotWireValueArchitectureTests</c> for the canonical
    /// reproduction and the rationale).
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

    private static string NormalizeRelativePath(string srcRoot, string fullPath)
    {
        var repoRoot = Path.GetFullPath(Path.Combine(srcRoot, ".."));
        var rel = Path.GetRelativePath(repoRoot, fullPath);
        return rel.Replace(Path.DirectorySeparatorChar, '/');
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
