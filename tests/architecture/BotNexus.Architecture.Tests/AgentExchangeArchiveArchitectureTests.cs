using System.Text.RegularExpressions;
using Shouldly;

namespace BotNexus.Architecture.Tests;

/// <summary>
/// Architecture fitness functions enforcing the Phase 9 / P9-C contract: every
/// terminal A↔A exchange path (both success-seal AND error-catch) in the three
/// sealing methods MUST invoke <c>ArchiveOnExchangeEndAsync</c> so the per-call
/// conversation is archived (not just have its <c>ActiveSessionId</c> cleared).
///
/// Driven by W-3 directive — <em>"A-A conversations should have an end and then
/// the conversation is done as that topic of conversation is over"</em>.
///
/// The fence covers three production methods:
/// <list type="bullet">
///   <item><c>AgentExchangeService.ConverseAsync</c> (local A↔A path)</item>
///   <item><c>AgentExchangeService.ConverseCrossWorldAsync</c> (cross-world sender path)</item>
///   <item><c>CrossWorldFederationController.ExecuteRelayAsync</c> (cross-world receiver path)</item>
/// </list>
/// </summary>
/// <remarks>
/// <para>The fence is a <strong>structural smoke test</strong>: each method body must
/// contain at least one <c>ArchiveOnExchangeEndAsync(</c> invocation. A regression that
/// retained the literal but inverted the call order (archiving on success but not on
/// error, or vice versa) would pass this fence and fail the behavioural pins in
/// <c>AgentExchangeArchiveTests</c> + <c>CrossWorldFederationControllerTests.RelayAsync_*ArchivesReceiverConversation</c>.
/// The two layers complement; neither alone is sufficient.</para>
///
/// <para>A separate counter-fence asserts each file contains the helper definition
/// itself (so the per-method fence is not vacuous against a refactor that deletes
/// the helper but leaves stale call sites uncompilable).</para>
/// </remarks>
public sealed class AgentExchangeArchiveArchitectureTests
{
    private const string HelperName = "ArchiveOnExchangeEndAsync";

    [Fact]
    public void AgentExchangeService_ConverseAsync_DelegatesToSharedExchangeLoop()
    {
        // P9-C (#1384): the local A↔A path no longer inlines the seal/archive epilogue — it is
        // single-sourced in RunExchangeLoopAsync. The fence now asserts ConverseAsync routes
        // through that shared loop (so it cannot seal without going through the archive call),
        // and a dedicated fence below pins that RunExchangeLoopAsync invokes the archive helper.
        var (source, methods) = LoadAndSplit(LocateAgentExchangeServiceFile());

        AssertMethodInvokesHelper(methods, source, "ConverseAsync", "RunExchangeLoopAsync",
            "P9-C: the local A↔A path in AgentExchangeService.ConverseAsync MUST drive the shared " +
            "RunExchangeLoopAsync(...) which owns the terminal seal + ArchiveOnExchangeEndAsync " +
            "contract. If you inline the loop again, restore a direct ArchiveOnExchangeEndAsync " +
            "fence on its seal paths or A↔A conversations stay Active forever.");
    }

    [Fact]
    public void AgentExchangeService_ConverseCrossWorldAsync_DelegatesToSharedExchangeLoop()
    {
        // P9-C (#1384): the cross-world sender path likewise delegates the seal/archive lifecycle
        // to the shared RunExchangeLoopAsync. See the local-path fence above for rationale.
        // #1542: ConverseCrossWorldAsync moved into CrossWorldExchangeRouter when cross-world
        // federation routing was split out of AgentExchangeService (SRP); the invariant holds.
        var (source, methods) = LoadAndSplit(LocateCrossWorldExchangeRouterFile());

        AssertMethodInvokesHelper(methods, source, "ConverseCrossWorldAsync", "RunExchangeLoopAsync",
            "P9-C: the cross-world sender path in CrossWorldExchangeRouter.ConverseCrossWorldAsync " +
            "MUST drive the shared RunExchangeLoopAsync(...) which owns the terminal seal + " +
            "ArchiveOnExchangeEndAsync contract. Without it, sender-side A↔A conversations stay " +
            "Active forever after the exchange ends.");
    }

    [Fact]
    public void AgentExchangeService_RunExchangeLoop_InvokesArchiveHelper()
    {
        // The single source of truth for the A↔A seal/archive lifecycle (#1384). Both
        // ConverseAsync and ConverseCrossWorldAsync delegate here, so this is where the
        // ArchiveOnExchangeEndAsync(...) call must live — on BOTH the success seal and the
        // error catch arm. #1542: RunExchangeLoopAsync + the archive helper moved into
        // AgentExchangeTurnEngine (the shared turn engine) when the service was split.
        var (source, methods) = LoadAndSplit(LocateAgentExchangeTurnEngineFile());

        AssertMethodInvokesHelper(methods, source, "RunExchangeLoopAsync", HelperName,
            "P9-C: the shared AgentExchangeTurnEngine.RunExchangeLoopAsync MUST invoke " +
            $"{HelperName}(...) on its terminal seal paths (success + error catch). This is the " +
            "single-sourced lifecycle that ConverseAsync and ConverseCrossWorldAsync both drive. " +
            "Without it, A↔A conversations stay Active forever in portal/list APIs.");
    }

    [Fact]
    public void CrossWorldFederationController_ExecuteRelayAsync_InvokesArchiveHelper()
    {
        var (source, methods) = LoadAndSplit(LocateCrossWorldFederationControllerFile());

        AssertMethodInvokesHelper(methods, source, "ExecuteRelayAsync", HelperName,
            "P9-C: the cross-world receiver path in CrossWorldFederationController.ExecuteRelayAsync " +
            $"MUST invoke {HelperName}(...) on its terminal paths " +
            "(exchangeFinished, CloseAfterResponse, and error catch). Without this, " +
            "receiver-side A↔A conversations stay Active forever after the exchange ends.");
    }

    [Fact]
    public void AgentExchangeService_DefinesArchiveHelper()
    {
        // Counter-fence: the helper must be defined in the file. If a refactor deletes the
        // helper (e.g. extracts it to a shared service), the per-method fence above passes
        // vacuously if the call sites also get refactored. This pin forces an author who
        // deletes the helper to ALSO update the per-method regex to track the new shape.
        // #1542: the helper now lives in AgentExchangeTurnEngine (the shared turn engine).
        var path = LocateAgentExchangeTurnEngineFile();
        var source = File.ReadAllText(path);

        var definitionPattern = new Regex(
            @"\bprivate\s+(?:async\s+)?(?:static\s+)?(?:async\s+)?Task\s+" + Regex.Escape(HelperName) + @"\s*\(",
            RegexOptions.Compiled);

        definitionPattern.IsMatch(source).ShouldBeTrue(
            $"AgentExchangeTurnEngine.cs must define a `private (async) Task {HelperName}(...)` " +
            "helper. If you have moved this helper to a shared service, the per-method " +
            $"fences (`*_InvokesArchiveHelper`) will become vacuous because the literal " +
            $"`{HelperName}(` no longer appears in the call sites. Either keep the helper " +
            "here OR update the fence regex to track the new shape (e.g. " +
            "`_archiveService.ArchiveOnExchangeEndAsync(`).\nFile: " + path);
    }

    [Fact]
    public void CrossWorldFederationController_DefinesArchiveHelper()
    {
        var path = LocateCrossWorldFederationControllerFile();
        var source = File.ReadAllText(path);

        var definitionPattern = new Regex(
            @"\bprivate\s+(?:async\s+)?(?:static\s+)?(?:async\s+)?Task\s+" + Regex.Escape(HelperName) + @"\s*\(",
            RegexOptions.Compiled);

        definitionPattern.IsMatch(source).ShouldBeTrue(
            $"CrossWorldFederationController.cs must define a `private (async) Task {HelperName}(...)` " +
            "helper. See the rationale in AgentExchangeService_DefinesArchiveHelper.\nFile: " + path);
    }

    /// <summary>
    /// Self-test for <see cref="SplitIntoMethodBodies"/> (REQUIRED per stored architecture-fence
    /// memory). If the splitter regex regresses and stops matching any of the three target
    /// methods, the per-method fences above silently pass without policing anything. This test
    /// asserts the splitter finds every target method by name.
    /// </summary>
    [Fact]
    public void SplitIntoMethodBodies_FindsAllThreeTargetMethods()
    {
        // #1542: the three policed methods now live across three files after the SRP split:
        //   - ConverseAsync                stays in AgentExchangeService.cs (local path)
        //   - RunExchangeLoopAsync         moved to AgentExchangeTurnEngine.cs (shared loop)
        //   - ConverseCrossWorldAsync      moved to CrossWorldExchangeRouter.cs (federation)
        //   - ExecuteRelayAsync            stays in CrossWorldFederationController.cs (receiver)
        var (_, serviceMethods) = LoadAndSplit(LocateAgentExchangeServiceFile());
        var serviceNames = serviceMethods.Select(m => m.Name).ToList();
        serviceNames.ShouldContain("ConverseAsync",
            "Method splitter must find ConverseAsync in AgentExchangeService.cs — otherwise " +
            "the local A↔A fence is vacuous. Splitter found: " + string.Join(", ", serviceNames));

        var (_, engineMethods) = LoadAndSplit(LocateAgentExchangeTurnEngineFile());
        var engineNames = engineMethods.Select(m => m.Name).ToList();
        engineNames.ShouldContain("RunExchangeLoopAsync",
            "Method splitter must find RunExchangeLoopAsync in AgentExchangeTurnEngine.cs — " +
            "otherwise the shared-loop archive fence is vacuous. Splitter found: " +
            string.Join(", ", engineNames));

        var (_, routerMethods) = LoadAndSplit(LocateCrossWorldExchangeRouterFile());
        var routerNames = routerMethods.Select(m => m.Name).ToList();
        routerNames.ShouldContain("ConverseCrossWorldAsync",
            "Method splitter must find ConverseCrossWorldAsync in CrossWorldExchangeRouter.cs — " +
            "otherwise the cross-world sender fence is vacuous. Splitter found: " +
            string.Join(", ", routerNames));

        var (_, controllerMethods) = LoadAndSplit(LocateCrossWorldFederationControllerFile());
        var controllerNames = controllerMethods.Select(m => m.Name).ToList();

        controllerNames.ShouldContain("ExecuteRelayAsync",
            "Method splitter must find ExecuteRelayAsync in CrossWorldFederationController.cs " +
            "— otherwise the cross-world receiver fence is vacuous. Splitter found: " +
            string.Join(", ", controllerNames));
    }

    [Fact]
    public void Fence_IsNotVacuous_AgainstSyntheticRegression_NoArchiveCall()
    {
        // Synthetic regression shape: a target method that seals the session on success but
        // does NOT invoke the archive helper. This is the pre-P9-C behaviour shape. The
        // detection helper must flag it.
        const string syntheticMissingArchive = """
            private async Task ExecuteRelayAsync(CancellationToken cancellationToken)
            {
                try
                {
                    var response = await handle.PromptAsync(message, cancellationToken);
                    session.Status = GatewaySessionStatus.Sealed;
                    await sessionStore.SaveAsync(session, CancellationToken.None);
                    await ClearActiveSessionAsync(session, conversation);
                }
                catch (Exception ex)
                {
                    session.Status = GatewaySessionStatus.Sealed;
                    await sessionStore.SaveAsync(session, CancellationToken.None);
                    throw;
                }
            }
            """;

        var methods = SplitIntoMethodBodies(syntheticMissingArchive);
        var executeRelay = methods.FirstOrDefault(m => m.Name == "ExecuteRelayAsync");
        executeRelay.Name.ShouldBe("ExecuteRelayAsync",
            "Vacuity guard precondition: splitter must find the synthetic method.");

        var invokesHelper = MethodInvokesHelper(executeRelay.Body);
        invokesHelper.ShouldBeFalse(
            "Vacuity guard: a synthetic method that seals but does NOT call " +
            $"{HelperName}(...) MUST be detected as a violation. If this assertion fails, " +
            "the detection helper is broken and the per-method fences are silently passing.");
    }

    [Fact]
    public void Fence_PositivePin_DetectsCanonicalCleanShape()
    {
        // Synthetic positive: a method that DOES call the archive helper. The detection
        // helper must accept it. Pins the regex against accidental over-tightening.
        const string syntheticCleanShape = """
            private async Task ExecuteRelayAsync(CancellationToken cancellationToken)
            {
                try
                {
                    var response = await handle.PromptAsync(message, cancellationToken);
                    session.Status = GatewaySessionStatus.Sealed;
                    await sessionStore.SaveAsync(session, CancellationToken.None);
                    await ArchiveOnExchangeEndAsync(session, conversation);
                }
                catch (Exception ex)
                {
                    session.Status = GatewaySessionStatus.Sealed;
                    await sessionStore.SaveAsync(session, CancellationToken.None);
                    await ArchiveOnExchangeEndAsync(session, conversation);
                    throw;
                }
            }
            """;

        var methods = SplitIntoMethodBodies(syntheticCleanShape);
        var executeRelay = methods.FirstOrDefault(m => m.Name == "ExecuteRelayAsync");
        executeRelay.Name.ShouldBe("ExecuteRelayAsync");

        MethodInvokesHelper(executeRelay.Body).ShouldBeTrue(
            "Positive pin: a method that calls " + HelperName + "(...) at least once must be " +
            "accepted. If this assertion fails, the regex is over-tight and would false-positive " +
            "against the real production code.");
    }

    // ---- helpers ----

    private static void AssertMethodInvokesHelper(
        List<(string Name, string Body, int StartIndex)> methods,
        string source,
        string methodName,
        string invokedLiteral,
        string failureMessage)
    {
        var method = methods.FirstOrDefault(m => m.Name == methodName);
        method.Name.ShouldBe(methodName,
            $"Splitter did not find `{methodName}` — fence cannot run. If the method has been " +
            "renamed or refactored away, update this test accordingly. Found methods: " +
            string.Join(", ", methods.Select(m => m.Name)));

        MethodInvokes(method.Body, invokedLiteral).ShouldBeTrue(failureMessage);
    }

    private static bool MethodInvokesHelper(string body) => MethodInvokes(body, HelperName);

    private static bool MethodInvokes(string body, string invokedLiteral)
    {
        var pattern = new Regex(@"\b" + Regex.Escape(invokedLiteral) + @"\s*\(", RegexOptions.Compiled);
        return pattern.IsMatch(body);
    }

    private static (string Source, List<(string Name, string Body, int StartIndex)> Methods)
        LoadAndSplit(string path)
    {
        var source = File.ReadAllText(path);
        return (source, SplitIntoMethodBodies(source));
    }

    /// <summary>
    /// Splits the source file into top-level method bodies (each as a substring of the
    /// original source plus its start index in the file). Identical pattern to the
    /// splitter in <see cref="AgentExchangeConversationArchitectureTests"/>; the regex
    /// supports generic return types like <c>Task&lt;AgentExchangeResult&gt;</c>.
    /// </summary>
    private static List<(string Name, string Body, int StartIndex)> SplitIntoMethodBodies(string source)
    {
        var methods = new List<(string, string, int)>();
        var headerPattern = new Regex(
            @"^\s*(?:public|private|protected|internal)(?:\s+(?:async|static|override|virtual|sealed|new|partial|readonly|extern|unsafe))*\s+[\w<>?,\.\[\]\s]+?\s+(?<name>\w+)\s*\(",
            RegexOptions.Multiline | RegexOptions.Compiled);

        foreach (Match header in headerPattern.Matches(source))
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

    // #1542: the shared turn loop (RunExchangeLoopAsync + ArchiveOnExchangeEndAsync) lives here.
    private static string LocateAgentExchangeTurnEngineFile()
    {
        var path = Path.Combine(
            FindSourceRoot(),
            "gateway",
            "BotNexus.Gateway",
            "Agents",
            "AgentExchangeTurnEngine.cs");
        File.Exists(path).ShouldBeTrue("Expected AgentExchangeTurnEngine.cs at " + path);
        return path;
    }

    // #1542: the cross-world sender path (ConverseCrossWorldAsync) lives here.
    private static string LocateCrossWorldExchangeRouterFile()
    {
        var path = Path.Combine(
            FindSourceRoot(),
            "gateway",
            "BotNexus.Gateway",
            "Agents",
            "CrossWorldExchangeRouter.cs");
        File.Exists(path).ShouldBeTrue("Expected CrossWorldExchangeRouter.cs at " + path);
        return path;
    }

    private static string LocateCrossWorldFederationControllerFile()
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

    private static string FindSourceRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "BotNexus.slnx")))
        {
            current = current.Parent;
        }

        current.ShouldNotBeNull("Could not locate repo root from " + AppContext.BaseDirectory);
        var srcRoot = Path.Combine(current!.FullName, "src");
        Directory.Exists(srcRoot).ShouldBeTrue("Expected src/ under " + current.FullName);
        return srcRoot;
    }
}
