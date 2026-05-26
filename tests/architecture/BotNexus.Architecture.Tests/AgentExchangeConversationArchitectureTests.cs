using Shouldly;

namespace BotNexus.Architecture.Tests;

/// <summary>
/// Architecture fitness functions enforcing the Phase 4 / F-3 contract: a named↔named
/// agent exchange must create a real <see cref="BotNexus.Domain.Primitives.Conversation"/>
/// via <c>IConversationStore</c> and pin the child session to it BEFORE the supervisor
/// hands the session out for prompting. The synthetic
/// <c>SessionId.ForAgentConversation(...)</c> factory (which produces the
/// <c>{init}::agent-agent::{tgt}::{guid}</c> encoding) must NOT be called from
/// <c>AgentExchangeService.cs</c>.
/// </summary>
/// <remarks>
/// The original bug: <c>AgentExchangeService.ConverseAsync</c> minted a synthetic
/// <c>SessionId.ForAgentConversation(...)</c>, created the child session with
/// <c>ConversationId == null</c>, and ran the prompt loop. The exchange never appeared
/// in conversation listings, the portal could not render it, and
/// <c>IConversationStore.ListByCitizenAsync</c> / <c>ISessionStore.ListByConversationAsync</c>
/// produced empty results.
///
/// These fences make the regression structurally impossible:
/// 1. <c>AgentExchangeService.cs</c> may not call <c>SessionId.ForAgentConversation(</c>.
/// 2. <c>AgentExchangeService.cs</c> must import <c>IConversationStore</c>.
/// 3. Every <c>.ConversationId =</c> assignment must lexically precede every
///    <c>PromptAsync(</c> call (mirrors the F-6 lexical-ordering fence).
/// 4. <c>AgentExchangeResult.ConversationId</c> must be a required non-nullable
///    <c>ConversationId</c>, so no construction path can omit it.
/// </remarks>
public sealed class AgentExchangeConversationArchitectureTests
{
    [Fact]
    public void AgentExchangeService_DoesNotCall_SessionIdForAgentConversation()
    {
        var source = File.ReadAllText(LocateAgentExchangeServiceFile());

        var match = new System.Text.RegularExpressions.Regex(
            @"\bSessionId\.ForAgentConversation\s*\(",
            System.Text.RegularExpressions.RegexOptions.Compiled)
            .Match(source);

        match.Success.ShouldBeFalse(
            "AgentExchangeService.cs contains a SessionId.ForAgentConversation(...) call. " +
            "The Phase 4 / F-3 contract requires named↔named exchanges to flow through " +
            "IConversationStore.CreateAsync with a freshly minted ConversationId, and the " +
            "session to be assigned a generic SessionId.Create() (no `::agent-agent::` " +
            "encoding). The synthetic factory is reserved for back-compat readers in " +
            "CrossWorldFederationController and DefaultAgentCommunicator until Phase 4 " +
            "item 1b ships.\n" +
            "Match at character index: " + match.Index);
    }

    [Fact]
    public void AgentExchangeService_Imports_IConversationStore()
    {
        var source = File.ReadAllText(LocateAgentExchangeServiceFile());

        var hasUsing = source.Contains("using BotNexus.Gateway.Abstractions.Conversations;");
        hasUsing.ShouldBeTrue(
            "AgentExchangeService.cs must import the IConversationStore namespace " +
            "(`using BotNexus.Gateway.Abstractions.Conversations;`). If this import is " +
            "missing, the service has lost the ability to create a Conversation for the " +
            "exchange, which means every named↔named call would orphan its session again.");
    }

    /// <summary>
    /// In <c>AgentExchangeService.cs</c>, within EACH method body, every
    /// <c>.ConversationId =</c> mutation must lexically precede every
    /// <c>PromptAsync(</c> invocation in that same method. This catches both inline
    /// regressions (assigning ConversationId after the prompt loop has started) and the
    /// helper-method regression shape (assignment moved into a helper called after the
    /// supervisor handle is acquired). Mirrors the F-6 fence in
    /// <see cref="SubAgentEagerPinArchitectureTests"/>.
    /// </summary>
    [Fact]
    public void NoConversationIdMutation_AfterFirstPromptAsync_InAgentExchangeService()
    {
        var source = File.ReadAllText(LocateAgentExchangeServiceFile());

        var methods = SplitIntoMethodBodies(source);
        methods.ShouldNotBeEmpty(
            "Expected to find at least one method body in AgentExchangeService.cs " +
            "but the regex split returned zero matches. The fence regex may need to be " +
            "updated for a method-shape change in the file.");

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
            "AgentExchangeService.cs has at least one method whose .ConversationId = " +
            "assignment occurs AFTER the first .PromptAsync(...) call IN THE SAME METHOD. " +
            "The F-3 + F-6 contract requires the child session's ConversationId to be " +
            "pinned eagerly on the synchronous path BEFORE any code path observes the " +
            "session (supervisor.GetOrCreateAsync / handle.PromptAsync). Otherwise the " +
            "session is briefly visible in the store with ConversationId == null and is " +
            "invisible to ISessionStore.ListByConversationAsync, the portal, and any " +
            "fan-out that filters by conversation.\n" +
            string.Join("\n", failures));
    }

    /// <summary>
    /// Splits the source file into top-level method bodies (each as a substring of the
    /// original source plus its start index in the file). Uses brace-matching from the
    /// first <c>{</c> after each <c>(public|private|protected|internal) ... Method(</c>
    /// declaration. Returns tuples of <c>(MethodName, BodyText, BodyStartIndexInFile)</c>.
    /// </summary>
    private static List<(string Name, string Body, int StartIndex)> SplitIntoMethodBodies(string source)
    {
        var methods = new List<(string, string, int)>();
        // Match: <access-modifier> [async|static|...]* <return-type> <name> (
        // The return-type segment uses [\w<>?,\.\[\]\s]+? to support generic return types like
        // Task<AgentExchangeResult> and arrays/nullable annotations. An earlier version used
        // \w+\s+\w+ which silently skipped every method with a generic return type — including
        // ConverseAsync and ConverseCrossWorldAsync, the two methods this fence exists to police.
        var headerPattern = new System.Text.RegularExpressions.Regex(
            @"^\s*(?:public|private|protected|internal)(?:\s+(?:async|static|override|virtual|sealed|new|partial|readonly|extern|unsafe))*\s+[\w<>?,\.\[\]\s]+?\s+(?<name>\w+)\s*\(",
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

            var bodyEnd = i; // one past the closing brace
            methods.Add((header.Groups["name"].Value, source[bodyOpen..bodyEnd], bodyOpen));
        }

        return methods;
    }

    [Fact]
    public void AgentExchangeResult_ConversationId_IsRequiredAndNonNullable()
    {
        var path = LocateAgentExchangeResultFile();
        var source = File.ReadAllText(path);

        var requiredNonNullable = new System.Text.RegularExpressions.Regex(
            @"required\s+ConversationId\s+ConversationId\b",
            System.Text.RegularExpressions.RegexOptions.Compiled);

        requiredNonNullable.IsMatch(source).ShouldBeTrue(
            "AgentExchangeResult.ConversationId must be declared as " +
            "`required ConversationId ConversationId` (non-nullable, required). " +
            "This forces every code path that constructs an AgentExchangeResult to " +
            "supply the real ConversationId, which means callers (AgentConverseTool, " +
            "tests, federation receivers) cannot accidentally drop the link.\n" +
            "File: " + path);
    }

    /// <summary>
    /// Self-test for <see cref="SplitIntoMethodBodies"/>. If the method-splitter regex ever
    /// regresses such that it stops matching <c>ConverseAsync</c> or
    /// <c>ConverseCrossWorldAsync</c> (e.g. someone changes it to require non-generic return
    /// types again), the prompt-ordering fence above would silently pass without checking the
    /// only two methods that actually matter. This self-test makes that failure mode loud.
    /// </summary>
    [Fact]
    public void SplitIntoMethodBodies_FindsTheTwoMethodsTheFenceMustPolice()
    {
        var path = LocateAgentExchangeServiceFile();
        var source = File.ReadAllText(path);
        var methods = SplitIntoMethodBodies(source);
        var names = methods.Select(m => m.Name).ToList();

        names.ShouldContain("ConverseAsync",
            "ConverseAsync is the local named↔named branch and is the primary subject of the " +
            "lexical-ordering fence. If the method splitter cannot find it, the fence is not " +
            "actually checking anything for the local branch. Method splitter found: " +
            string.Join(", ", names));
        names.ShouldContain("ConverseCrossWorldAsync",
            "ConverseCrossWorldAsync is the cross-world sender branch. If the method splitter " +
            "cannot find it, regressions in that branch slip through. Method splitter found: " +
            string.Join(", ", names));
    }

    private static string LocateAgentExchangeServiceFile()
    {
        var path = Path.Combine(
            FindSourceRoot(),
            "gateway",
            "BotNexus.Gateway",
            "Agents",
            "AgentExchangeService.cs");
        File.Exists(path).ShouldBeTrue("Expected AgentExchangeService.cs at " + path);
        return path;
    }

    private static string LocateAgentExchangeResultFile()
    {
        var path = Path.Combine(
            FindSourceRoot(),
            "domain",
            "BotNexus.Domain",
            "Gateway",
            "Models",
            "AgentConversationResult.cs");
        File.Exists(path).ShouldBeTrue("Expected AgentConversationResult.cs at " + path);
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
