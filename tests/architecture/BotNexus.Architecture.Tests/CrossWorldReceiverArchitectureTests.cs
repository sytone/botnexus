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
/// 3. Every <c>.ConversationId =</c> assignment must lexically precede every
///    <c>PromptAsync(</c> call (mirrors the sender's F-6 lexical-ordering fence).
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
    /// In <c>CrossWorldFederationController.cs</c>, within EACH method body, every
    /// <c>.ConversationId =</c> mutation must lexically precede every <c>PromptAsync(</c>
    /// invocation in that same method. Catches both inline regressions (assigning
    /// ConversationId after the prompt loop starts) and the helper-method shape (assignment
    /// moved into a helper called after the supervisor handle is acquired). Mirrors the F-6
    /// fence in <see cref="SubAgentEagerPinArchitectureTests"/> and the F-3 sender fence.
    /// </summary>
    [Fact]
    public void NoConversationIdMutation_AfterFirstPromptAsync_InRelayAsync()
    {
        var source = File.ReadAllText(LocateControllerFile());

        var methods = SplitIntoMethodBodies(source);
        methods.ShouldNotBeEmpty(
            "Expected to find at least one method body in CrossWorldFederationController.cs " +
            "but the regex split returned zero matches. The fence regex may need to be updated " +
            "for a method-shape change in the file.");

        var failures = new List<string>();
        foreach (var (methodName, body, methodStartIndex) in methods)
        {
            var assignmentIndexes = new System.Text.RegularExpressions.Regex(
                @"\.ConversationId\s*=",
                System.Text.RegularExpressions.RegexOptions.Compiled)
                .Matches(body)
                .Select(match => match.Index)
                .ToArray();

            var promptIndexes = new System.Text.RegularExpressions.Regex(
                @"\.PromptAsync\s*\(",
                System.Text.RegularExpressions.RegexOptions.Compiled)
                .Matches(body)
                .Select(match => match.Index)
                .ToArray();

            if (assignmentIndexes.Length == 0 || promptIndexes.Length == 0)
                continue;

            var firstPrompt = promptIndexes.Min();
            var lateAssignments = assignmentIndexes
                .Where(idx => idx > firstPrompt)
                .Select(idx => LineNumberAt(source, methodStartIndex + idx))
                .ToArray();

            if (lateAssignments.Length > 0)
            {
                failures.Add(
                    $"  {methodName}: late .ConversationId = assignments at lines " +
                    string.Join(", ", lateAssignments) +
                    " (first .PromptAsync at line " +
                    LineNumberAt(source, methodStartIndex + firstPrompt) + ")");
            }
        }

        failures.ShouldBeEmpty(
            "CrossWorldFederationController.cs has at least one method whose .ConversationId = " +
            "assignment occurs AFTER the first .PromptAsync(...) call IN THE SAME METHOD. " +
            "The receiver contract requires the receiver-side session's ConversationId to be " +
            "pinned eagerly BEFORE any code path observes the session (supervisor.GetOrCreateAsync, " +
            "handle.PromptAsync). Otherwise the session is briefly visible in the store with " +
            "ConversationId == null and is invisible to ISessionStore.ListByConversationAsync, " +
            "the portal, and any fan-out that filters by conversation.\n" +
            string.Join("\n", failures));
    }

    /// <summary>
    /// Self-test for <see cref="SplitIntoMethodBodies"/>. If the method-splitter regex ever
    /// regresses such that it stops matching <c>RelayAsync</c>, the prompt-ordering fence
    /// above would silently pass without checking the only method that actually matters.
    /// This self-test makes that failure mode loud (lesson learned from PR #548).
    /// </summary>
    [Fact]
    public void SplitIntoMethodBodies_FindsRelayAsync()
    {
        var path = LocateControllerFile();
        var source = File.ReadAllText(path);
        var methods = SplitIntoMethodBodies(source);
        var names = methods.Select(m => m.Name).ToList();

        names.ShouldContain("RelayAsync",
            "RelayAsync is the cross-world receiver entry point and the subject of the " +
            "lexical-ordering fence. If the method splitter cannot find it, the fence is " +
            "not actually checking anything. Method splitter found: " +
            string.Join(", ", names));
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
