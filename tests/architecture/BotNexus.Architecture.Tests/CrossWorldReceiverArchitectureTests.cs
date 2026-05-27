using Shouldly;

namespace BotNexus.Architecture.Tests;

/// <summary>
/// Architecture fitness functions enforcing the Phase 4 / 1b receiver contract:
/// <c>CrossWorldFederationController.RelayAsync</c> must create a real
/// <see cref="BotNexus.Domain.Primitives.Conversation"/> via <c>IConversationStore</c>
/// and pin the receiver-side session to it BEFORE the supervisor is invoked. The
/// synthetic <c>SessionId.ForAgentConversation(...)</c> factory (which produces the
/// <c>{init}::agent-agent::{tgt}::{guid}</c> encoding) must NOT be called from
/// <c>CrossWorldFederationController.cs</c>.
/// </summary>
/// <remarks>
/// <para>
/// Mirrors <see cref="AgentExchangeConversationArchitectureTests"/> which guards the
/// sender branch (PR #548 / F-3). The original receiver bug: <c>RelayAsync</c> minted a
/// synthetic <c>SessionId.ForAgentConversation(...)</c>, created the receiver session
/// with <c>ConversationId == null</c>, ran the prompt loop, and returned. The exchange
/// never appeared in the conversation list and ISessionStore.ListByConversationAsync
/// returned empty for it.
/// </para>
/// <para>
/// These fences make the regression structurally impossible:
/// 1. <c>CrossWorldFederationController.cs</c> may not call <c>SessionId.ForAgentConversation(</c>.
/// 2. <c>CrossWorldFederationController.cs</c> must import <c>IConversationStore</c>.
/// 3. Inside <c>RelayAsync</c>, every <c>.ConversationId =</c> assignment must lexically precede
///    the first <c>.PromptAsync(</c>; AND no helper method called from <c>RelayAsync</c> AFTER
///    the first <c>.PromptAsync(</c> may contain a <c>.ConversationId =</c> mutation. This
///    "compound" check catches the helper-extraction bypass that a per-method fence would miss
///    AND avoids the false-positive of a naive cross-file check on helpers defined later in
///    the file (PR #549 critique sweep — bug-hunt BLOCKING #6 / plan-vs-impl HIGH).
/// 4. The constructor must accept an <c>IConversationStore</c> parameter — if someone
///    removes the dep, every other invariant collapses.
/// </para>
/// </remarks>
public sealed class CrossWorldReceiverArchitectureTests
{
    [Fact]
    public void CrossWorldFederationController_DoesNotCall_SessionIdForAgentConversation()
    {
        var source = File.ReadAllText(LocateControllerFile());

        var match = new System.Text.RegularExpressions.Regex(
            @"\bSessionId\.ForAgentConversation\s*\(",
            System.Text.RegularExpressions.RegexOptions.Compiled)
            .Match(source);

        match.Success.ShouldBeFalse(
            "CrossWorldFederationController.cs contains a SessionId.ForAgentConversation(...) call. " +
            "The Phase 4 / 1b receiver contract requires cross-world relays to flow through " +
            "IConversationStore.CreateAsync with a freshly minted ConversationId, and the receiver " +
            "session to be assigned a generic SessionId.Create() (no `::agent-agent::` encoding). " +
            "If the factory is genuinely needed for back-compat reading, it must live in a " +
            "dedicated reader, not in the relay write path.\n" +
            "Match at character index: " + match.Index);
    }

    [Fact]
    public void CrossWorldFederationController_Imports_IConversationStore()
    {
        var source = File.ReadAllText(LocateControllerFile());

        var hasUsing = source.Contains("using BotNexus.Gateway.Abstractions.Conversations;");
        hasUsing.ShouldBeTrue(
            "CrossWorldFederationController.cs must import the IConversationStore namespace " +
            "(`using BotNexus.Gateway.Abstractions.Conversations;`). If this import is missing, " +
            "the controller has lost the ability to create a Conversation for each relay, which " +
            "means every cross-world call would orphan its receiver session again.");
    }

    [Fact]
    public void CrossWorldFederationController_Ctor_Accepts_IConversationStore()
    {
        var source = File.ReadAllText(LocateControllerFile());

        var ctorParam = new System.Text.RegularExpressions.Regex(
            @"IConversationStore\s+\w+",
            System.Text.RegularExpressions.RegexOptions.Compiled);

        ctorParam.IsMatch(source).ShouldBeTrue(
            "CrossWorldFederationController.cs must declare an IConversationStore parameter " +
            "(constructor primary or classical). Without the dependency the receiver cannot " +
            "create or look up the local Conversation, and the Phase 4 / 1b contract collapses " +
            "back to the orphan-session shape.");
    }

    /// <summary>
    /// Compound fence that catches the helper-method bypass (PR #549 critique sweep —
    /// bug-hunt BLOCKING #6, plan-vs-impl HIGH):
    /// (a) Within <c>RelayAsync</c> itself, every inline <c>.ConversationId =</c> mutation
    ///     must lexically precede the first <c>.PromptAsync(</c> call.
    /// (b) For every helper method <c>H</c> defined in this file and invoked from
    ///     <c>RelayAsync</c> AFTER the first <c>.PromptAsync(</c>, <c>H</c>'s body must NOT
    ///     contain <c>.ConversationId =</c>. This kills the helper-extraction shape where
    ///     someone moves the assignment into <c>PinAndSaveAsync(...)</c> and calls it after
    ///     the supervisor handle is acquired.
    /// A naive cross-file "every assignment before every prompt" check false-positives on
    /// legitimate helpers (like <c>ResolveSessionAsync</c>) that are defined later in the
    /// file but called before the prompt loop.
    /// </summary>
    [Fact]
    public void NoConversationIdMutation_AfterFirstPromptAsync_InRelayAsync_OrAnyHelperCalledAfter()
    {
        var source = File.ReadAllText(LocateControllerFile());
        var methods = SplitIntoMethodBodies(source);

        var relay = methods.FirstOrDefault(m => m.Name == "RelayAsync");
        relay.Name.ShouldBe("RelayAsync",
            "Method splitter could not find RelayAsync — fence is vacuous. Method splitter found: " +
            string.Join(", ", methods.Select(m => m.Name)));

        // #551 refactor: RelayAsync now delegates the actual write→prompt→reload pipeline to an
        // extracted helper (ExecuteRelayAsync) that runs inside the per-session write lock. The
        // helper holds the .PromptAsync( call. Without the inlining step below, the vacuity
        // guard would (incorrectly) declare the fence dead the moment anyone extracts a method.
        // We treat any helper called from RelayAsync whose body contains .PromptAsync( as a
        // logical continuation of RelayAsync and concatenate its body in.
        var inlinedHelperNames = new HashSet<string>(StringComparer.Ordinal);
        var logicalRelayBody = relay.Body;
        foreach (var helper in methods.Where(m => m.Name != "RelayAsync"))
        {
            if (!System.Text.RegularExpressions.Regex.IsMatch(helper.Body, @"\.PromptAsync\s*\("))
                continue;
            // The helper must actually be called from RelayAsync — don't inline unrelated
            // methods just because they happen to call PromptAsync (e.g. a sibling endpoint).
            var calledFromRelay = System.Text.RegularExpressions.Regex.IsMatch(
                relay.Body,
                $@"\b{System.Text.RegularExpressions.Regex.Escape(helper.Name)}\s*\(");
            if (!calledFromRelay)
                continue;
            inlinedHelperNames.Add(helper.Name);
            logicalRelayBody += "\n" + helper.Body;
        }

        var firstPromptInRelay = FindFirstPromptIndex(logicalRelayBody);
        firstPromptInRelay.ShouldBeGreaterThanOrEqualTo(0,
            "Neither RelayAsync nor any helper it invokes has a .PromptAsync( call — fence is " +
            "vacuous. Either the controller stopped invoking the supervisor or the API was " +
            "renamed. (Searched RelayAsync + inlined helpers: " +
            string.Join(", ", inlinedHelperNames) + ")");

        var inlineFailures = FindInlineLateAssignments(logicalRelayBody);

        // Helpers that mutate .ConversationId AND are NOT already inlined into the logical
        // relay flow. The inlined helpers are scanned directly by the inline check above —
        // double-counting them would produce duplicate failures.
        var helpersWithAssignments = methods
            .Where(m => m.Name != "RelayAsync")
            .Where(m => !inlinedHelperNames.Contains(m.Name))
            .Where(m => System.Text.RegularExpressions.Regex.IsMatch(m.Body, @"\.ConversationId\s*="))
            .Select(m => m.Name)
            .ToHashSet(StringComparer.Ordinal);

        var postPromptHelperCalls = FindHelperInvocationsAfter(logicalRelayBody, firstPromptInRelay, helpersWithAssignments);

        var failureMessage = new List<string>();
        foreach (var (line, name) in inlineFailures)
            failureMessage.Add($"  inline assignment at logical-body offset {line} (first PromptAsync at logical-body offset {firstPromptInRelay})");
        foreach (var (line, name) in postPromptHelperCalls)
            failureMessage.Add($"  logical RelayAsync flow calls helper '{name}()' at offset {line} AFTER first .PromptAsync — and {name}() mutates .ConversationId");

        failureMessage.ShouldBeEmpty(
            "CrossWorldFederationController.RelayAsync has a .ConversationId mutation path that " +
            "runs AFTER .PromptAsync. The receiver contract requires the receiver-side session's " +
            "ConversationId to be pinned eagerly BEFORE the supervisor handle is acquired " +
            "(supervisor.GetOrCreateAsync, handle.PromptAsync). Otherwise the session is briefly " +
            "visible in the store with ConversationId == null and is invisible to " +
            "ISessionStore.ListByConversationAsync, the portal, and any fan-out that filters by " +
            "conversation.\n" +
            "Inlined prompt-host helpers: " + string.Join(", ", inlinedHelperNames) + "\n" +
            string.Join("\n", failureMessage));
    }

    /// <summary>
    /// Vacuity guard: confirm the fence is actually scanning something. Without this check,
    /// renaming <c>.ConversationId</c> or <c>.PromptAsync(</c> would silently turn the fence
    /// into a no-op (the failure mode the original per-method fence shipped in this PR).
    /// </summary>
    [Fact]
    public void Fence_IsNonVacuous_FindsAtLeastOneAssignmentAndOnePromptCall()
    {
        var source = File.ReadAllText(LocateControllerFile());

        var assignments = System.Text.RegularExpressions.Regex.Matches(source, @"\.ConversationId\s*=").Count;
        var prompts = System.Text.RegularExpressions.Regex.Matches(source, @"\.PromptAsync\s*\(").Count;

        assignments.ShouldBeGreaterThan(0,
            "Fence expected to find at least one '.ConversationId =' assignment in " +
            "CrossWorldFederationController.cs but found zero. The fence is vacuously passing.");
        prompts.ShouldBeGreaterThan(0,
            "Fence expected to find at least one '.PromptAsync(' call in " +
            "CrossWorldFederationController.cs but found zero. The fence cannot detect ordering " +
            "violations because there is nothing to order against.");
    }

    /// <summary>
    /// Scanner self-test on a synthetic violation: prove the helper-bypass detector CATCHES the
    /// shape where a helper called after PromptAsync mutates ConversationId. This is the exact
    /// bypass that the per-method fence shipped in this PR would have silently allowed.
    /// </summary>
    [Fact]
    public void HelperBypass_OnSyntheticViolation_IsDetected()
    {
        var synthetic = """
            public sealed class FakeController
            {
                public async Task RelayAsync()
                {
                    await handle.PromptAsync("hi", ct);
                    await PinAndSaveAsync(session);
                }

                private async Task PinAndSaveAsync(GatewaySession s)
                {
                    s.Session.ConversationId = ConversationId.Create();
                }
            }
            """;

        var methods = SplitIntoMethodBodies(synthetic);
        var relay = methods.First(m => m.Name == "RelayAsync");
        var firstPromptInRelay = FindFirstPromptIndex(relay.Body);
        var helpersWithAssignments = methods
            .Where(m => m.Name != "RelayAsync")
            .Where(m => System.Text.RegularExpressions.Regex.IsMatch(m.Body, @"\.ConversationId\s*="))
            .Select(m => m.Name)
            .ToHashSet(StringComparer.Ordinal);
        var postPromptHelperCalls = FindHelperInvocationsAfter(relay.Body, firstPromptInRelay, helpersWithAssignments);

        postPromptHelperCalls.ShouldNotBeEmpty(
            "Helper-bypass detector failed to flag a synthetic violation where a helper that " +
            "mutates ConversationId is called from RelayAsync AFTER the first PromptAsync. The " +
            "scanner is broken — production fence will not catch real regressions.");
    }

    /// <summary>
    /// Scanner self-test on a synthetic CLEAN file with helpers defined later that are called
    /// BEFORE the prompt (the shape that broke the naive cross-file scanner): assert no
    /// false-positive.
    /// </summary>
    [Fact]
    public void HelperBypass_OnSyntheticCleanFile_ReportsNoFailures()
    {
        var synthetic = """
            public sealed class FakeController
            {
                public async Task RelayAsync()
                {
                    var s = await Resolve();
                    await handle.PromptAsync("hi", ct);
                }

                private async Task<GatewaySession> Resolve()
                {
                    var s = new GatewaySession();
                    s.Session.ConversationId = ConversationId.Create();
                    return s;
                }
            }
            """;

        var methods = SplitIntoMethodBodies(synthetic);
        var relay = methods.First(m => m.Name == "RelayAsync");
        var firstPromptInRelay = FindFirstPromptIndex(relay.Body);
        var helpersWithAssignments = methods
            .Where(m => m.Name != "RelayAsync")
            .Where(m => System.Text.RegularExpressions.Regex.IsMatch(m.Body, @"\.ConversationId\s*="))
            .Select(m => m.Name)
            .ToHashSet(StringComparer.Ordinal);
        var postPromptHelperCalls = FindHelperInvocationsAfter(relay.Body, firstPromptInRelay, helpersWithAssignments);
        var inlineFailures = FindInlineLateAssignments(relay.Body);

        postPromptHelperCalls.ShouldBeEmpty(
            "False positive: helper 'Resolve' is called BEFORE PromptAsync but the bypass " +
            "detector flagged it. This was the bug in the naive cross-file scanner.");
        inlineFailures.ShouldBeEmpty(
            "False positive on inline check: RelayAsync has no inline assignments.");
    }

    private static int FindFirstPromptIndex(string body)
    {
        var match = System.Text.RegularExpressions.Regex.Match(body, @"\.PromptAsync\s*\(");
        return match.Success ? match.Index : -1;
    }

    private static List<(int Index, string Marker)> FindInlineLateAssignments(string relayBody)
    {
        var firstPrompt = FindFirstPromptIndex(relayBody);
        if (firstPrompt < 0) return [];
        return System.Text.RegularExpressions.Regex.Matches(relayBody, @"\.ConversationId\s*=")
            .Where(m => m.Index > firstPrompt)
            .Select(m => (m.Index, ".ConversationId ="))
            .ToList();
    }

    private static List<(int Index, string HelperName)> FindHelperInvocationsAfter(
        string relayBody,
        int firstPromptIndex,
        HashSet<string> helpersWithAssignments)
    {
        if (firstPromptIndex < 0) return [];
        var hits = new List<(int, string)>();
        foreach (var helper in helpersWithAssignments)
        {
            // Match `Helper(` or `await Helper(` or `this.Helper(` — any invocation form.
            var pattern = $@"\b{System.Text.RegularExpressions.Regex.Escape(helper)}\s*\(";
            foreach (System.Text.RegularExpressions.Match m in
                System.Text.RegularExpressions.Regex.Matches(relayBody, pattern))
            {
                if (m.Index > firstPromptIndex)
                    hits.Add((m.Index, helper));
            }
        }
        return hits;
    }

    /// <summary>
    /// Self-test for <see cref="SplitIntoMethodBodies"/>. If the method-splitter regex ever
    /// regresses such that it stops matching <c>RelayAsync</c>, the helper-bypass fence above
    /// would silently fail to find the method and the vacuity check would catch it — but this
    /// test makes the splitter regression itself loud.
    /// </summary>
    [Fact]
    public void SplitIntoMethodBodies_FindsRelayAsync()
    {
        var path = LocateControllerFile();
        var source = File.ReadAllText(path);
        var methods = SplitIntoMethodBodies(source);
        var names = methods.Select(m => m.Name).ToList();

        names.ShouldContain("RelayAsync",
            "RelayAsync is the cross-world receiver entry point. If the method splitter cannot " +
            "find it, fences cannot inspect it. Method splitter found: " + string.Join(", ", names));
    }

    private static List<(string Name, string Body, int StartIndex)> SplitIntoMethodBodies(string source)
    {
        var methods = new List<(string, string, int)>();
        // Match: <access-modifier> [async|static|...]* <return-type> <name> (
        // The return-type segment uses [\w<>?,\.\[\]\s]+? to support generic return types like
        // Task<ActionResult<CrossWorldRelayResponse>>.
        // The atomic group `(?>...)` on the modifier list prevents backtracking. Without it,
        // for a primary-constructor class like
        // `public sealed class CrossWorldFederationController(...)` the regex engine could
        // backtrack out of consuming `sealed` as a modifier and instead fold `sealed class`
        // into the return-type segment — defeating the `(?!class\b|...)` lookahead. The
        // atomic group locks in the longest modifier match.
        var headerPattern = new System.Text.RegularExpressions.Regex(
            @"^\s*(?>(?:public|private|protected|internal)(?:\s+(?:async|static|override|virtual|sealed|new|partial|readonly|extern|unsafe))*)\s+(?!class\b|record\b|struct\b|interface\b|enum\b)[\w<>?,\.\[\]\s]+?\s+(?<name>\w+)\s*\(",
            System.Text.RegularExpressions.RegexOptions.Multiline
            | System.Text.RegularExpressions.RegexOptions.Compiled);

        foreach (System.Text.RegularExpressions.Match header in headerPattern.Matches(source))
        {
            var bodyOpen = source.IndexOf('{', header.Index + header.Length);
            if (bodyOpen < 0)
                continue;

            var depth = 1;
            var i = bodyOpen + 1;
            while (i < source.Length && depth > 0)
            {
                var c = source[i];
                if (c == '{') depth++;
                else if (c == '}') depth--;
                i++;
            }

            if (depth != 0)
                continue;

            var bodyEnd = i;
            methods.Add((header.Groups["name"].Value, source[bodyOpen..bodyEnd], bodyOpen));
        }

        return methods;
    }

    private static string LocateControllerFile()
    {
        var path = Path.Combine(
            FindSourceRoot(),
            "gateway",
            "BotNexus.Gateway.Api",
            "Controllers",
            "CrossWorldFederationController.cs");
        File.Exists(path).ShouldBeTrue("Expected CrossWorldFederationController.cs at " + path);
        return path;
    }

    private static int LineNumberAt(string source, int charIndex)
    {
        var line = 1;
        for (var i = 0; i < charIndex && i < source.Length; i++)
        {
            if (source[i] == '\n')
                line++;
        }
        return line;
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
