using Shouldly;

namespace BotNexus.Architecture.Tests;

/// <summary>
/// Architecture fitness functions enforcing the <c>#551</c> contract: the cross-world
/// relay <c>RelayAsync</c> entry point must acquire an <c>ISessionWriteLock</c> before
/// touching the per-turn write→prompt→reload window. Two concurrent senders supplying
/// the same <c>RemoteSessionId</c> would otherwise interleave their per-turn
/// active-exchange-id writes and satisfy each other's freshness gate with the wrong
/// finish payload (cross-attribution + concurrent-writer reason/summary tampering — see
/// bug-hunt HIGH-1 and MEDIUM-2 against PR #550).
/// </summary>
/// <remarks>
/// This is a <strong>smoke test</strong>, not a correctness proof. The fence shows that
/// the lock-acquire call appears textually before any <c>PromptAsync</c> or
/// <c>PrepareTurn</c> call in the receiver file — that's the structural shape a regression
/// would have to defeat. The behavioural pins are in
/// <c>CrossWorldFederationControllerTests</c> (the two concurrent RelayAsync tests with
/// the deterministic-barrier handle) and in <c>SessionWriteLockTests</c>. A clever
/// regression that retained the textual ordering but broke semantics (e.g. wrapped
/// <c>AcquireAsync</c> in a no-op <c>IAsyncDisposable</c>) would pass this fence and
/// fail the behavioural tests — by design. The two layers complement; neither alone
/// is sufficient.
/// </remarks>
public sealed class SessionWriteLockArchitectureTests
{
    [Fact]
    public void CrossWorldFederationController_AcquiresSessionWriteLock_BeforePromptAndFinishGate()
    {
        var path = LocateCrossWorldFederationControllerFile();
        var source = File.ReadAllText(path);

        var violations = FindOrderingViolations(source);

        violations.ShouldBeEmpty(
            $"{Path.GetFileName(path)} has a call to PromptAsync(...) or " +
            "AgentExchangeCompletionGate.PrepareTurn(...) that is NOT preceded by an " +
            "ISessionWriteLock.AcquireAsync(...) call in the same file. This is the #551 " +
            "structural invariant: two concurrent senders supplying the same RemoteSessionId " +
            "must serialise on the per-session lock for the entire write→prompt→reload window, " +
            "otherwise their per-turn active-exchange-id writes interleave and satisfy each " +
            "other's freshness gate with the wrong finish payload.\n" +
            "Ordering violations:\n" + string.Join("\n", violations) + "\n" +
            "File: " + path);
    }

    /// <summary>
    /// The lock MUST be registered as a singleton — a transient or scoped registration would
    /// make every request acquire its own private lock with no contention, silently re-opening
    /// the #551 race window. The behavioural tests in <c>CrossWorldFederationControllerTests</c>
    /// wire a single shared lock by hand so they cannot catch this regression. Per plan-vs-impl
    /// critique MEDIUM-4 on the PR sweep, scan the DI composition root explicitly.
    /// </summary>
    [Fact]
    public void GatewayDi_RegistersISessionWriteLock_AsSingleton()
    {
        var path = LocateGatewayServiceCollectionExtensionsFile();
        var source = File.ReadAllText(path);

        // The registration must be a singleton variant. Accept either AddSingleton or
        // TryAddSingleton — both produce the same lifetime semantics.
        var singletonPattern = new System.Text.RegularExpressions.Regex(
            @"\b(?:AddSingleton|TryAddSingleton)\s*<\s*ISessionWriteLock\s*,\s*SessionWriteLock\s*>",
            System.Text.RegularExpressions.RegexOptions.Compiled);
        singletonPattern.IsMatch(source).ShouldBeTrue(
            "GatewayServiceCollectionExtensions.cs must register ISessionWriteLock as a singleton " +
            "(AddSingleton or TryAddSingleton). A transient or scoped registration silently " +
            "re-opens the #551 race window because every request would get its own private lock " +
            "instance and never contend on the shared per-session slots.\n" +
            "File: " + path);

        // Sanity: there must NOT be a competing non-singleton registration anywhere in the file.
        var transientPattern = new System.Text.RegularExpressions.Regex(
            @"\b(?:AddTransient|TryAddTransient|AddScoped|TryAddScoped)\s*<\s*ISessionWriteLock\s*,",
            System.Text.RegularExpressions.RegexOptions.Compiled);
        transientPattern.IsMatch(source).ShouldBeFalse(
            "GatewayServiceCollectionExtensions.cs contains a transient or scoped registration of " +
            "ISessionWriteLock alongside the singleton one. The DI container would pick whichever " +
            "ran last, silently downgrading the lock lifetime and re-opening the #551 race.\n" +
            "File: " + path);
    }

    [Fact]
    public void Fence_IsNotVacuous_AgainstSyntheticViolation_NoAcquireAtAll()
    {
        // If a regression deletes the lock entirely (most likely accidental form), the
        // fence must fire because PromptAsync still appears in the file with no
        // AcquireAsync to precede it.
        const string syntheticViolation = """
            public async Task<ActionResult> RelayAsync(CrossWorldRelayRequest request, CancellationToken ct)
            {
                var session = await sessionStore.GetAsync(SessionId.From(request.RemoteSessionId), ct);
                AgentExchangeCompletionGate.PrepareTurn(session.Metadata, Guid.NewGuid().ToString("N"));
                var handle = await supervisor.GetOrCreateAsync(session.AgentId, session.SessionId, ct);
                var response = await handle.PromptAsync(request.Message, ct);
                return Ok();
            }
            """;
        var violations = FindOrderingViolations(syntheticViolation);
        violations.ShouldNotBeEmpty(
            "Vacuity guard: the fence must catch the most obvious regression shape — " +
            "PromptAsync and PrepareTurn calls with no AcquireAsync anywhere in the file. " +
            "If this assertion fails, the fence has been weakened so far that it cannot " +
            "catch the original #551 bug class.");
    }

    [Fact]
    public void Fence_IsNotVacuous_AgainstSyntheticViolation_AcquireAfterPrompt()
    {
        // Subtler regression: a future refactor that moves AcquireAsync into a "post-prompt
        // hardening" block. The lock exists but is acquired too late, missing the entire
        // write→prompt window. The fence must catch this too.
        const string syntheticViolation = """
            public async Task<ActionResult> RelayAsync(CrossWorldRelayRequest request, CancellationToken ct)
            {
                var session = await sessionStore.GetAsync(SessionId.From(request.RemoteSessionId), ct);
                AgentExchangeCompletionGate.PrepareTurn(session.Metadata, Guid.NewGuid().ToString("N"));
                var handle = await supervisor.GetOrCreateAsync(session.AgentId, session.SessionId, ct);
                var response = await handle.PromptAsync(request.Message, ct);
                await using var lease = await sessionWriteLock.AcquireAsync(session.SessionId, ct);
                await sessionStore.SaveAsync(session, ct);
                return Ok();
            }
            """;
        var violations = FindOrderingViolations(syntheticViolation);
        violations.ShouldNotBeEmpty(
            "Vacuity guard: the fence must catch AcquireAsync that appears textually AFTER " +
            "the first PromptAsync/PrepareTurn — a lock acquired post-prompt cannot serialise " +
            "the write→prompt window the #551 race occurs in.");
    }

    [Fact]
    public void Fence_DoesNotFalsePositive_OnLegitimateOrdering()
    {
        // Legitimate two-branch acquisition: lock first on supplied RemoteSessionId, resolve
        // first then lock on fresh-mint, ExecuteRelayAsync extracted as helper. The fence
        // must not flag this shape (otherwise authors will disable it instead of fixing real
        // regressions).
        const string syntheticClean = """
            public async Task<ActionResult> RelayAsync(CrossWorldRelayRequest request, CancellationToken ct)
            {
                if (!string.IsNullOrWhiteSpace(request.RemoteSessionId))
                {
                    var supplied = SessionId.From(request.RemoteSessionId);
                    await using var lease = await sessionWriteLock.AcquireAsync(supplied, ct);
                    var resolved = await ResolveSessionAsync(request, targetAgentId, ct);
                    return await ExecuteRelayAsync(request, resolved.Session!, resolved.Conversation!, ct);
                }
                else
                {
                    var resolved = await ResolveSessionAsync(request, targetAgentId, ct);
                    await using var lease = await sessionWriteLock.AcquireAsync(resolved.Session!.SessionId, ct);
                    return await ExecuteRelayAsync(request, resolved.Session!, resolved.Conversation!, ct);
                }
            }

            private async Task<ActionResult> ExecuteRelayAsync(CrossWorldRelayRequest request, GatewaySession session, Conversation conversation, CancellationToken ct)
            {
                await sessionStore.SaveAsync(session, ct);
                AgentExchangeCompletionGate.PrepareTurn(session.Metadata, Guid.NewGuid().ToString("N"));
                var handle = await supervisor.GetOrCreateAsync(session.AgentId, session.SessionId, ct);
                var response = await handle.PromptAsync(request.Message, ct);
                return Ok();
            }
            """;
        var violations = FindOrderingViolations(syntheticClean);
        violations.ShouldBeEmpty(
            "False-positive guard: the fence must not flag the canonical two-branch " +
            "lock-acquisition shape used in production. If this assertion fails, the fence " +
            "is over-broad and authors will disable it instead of using it.\n" +
            "Violations: " + string.Join("\n", violations));
    }

    /// <summary>
    /// Finds every <c>PromptAsync(</c> or <c>AgentExchangeCompletionGate.PrepareTurn(</c>
    /// call that is not preceded by at least one <c>AcquireAsync(</c> call somewhere
    /// earlier in the source.
    /// </summary>
    /// <remarks>
    /// Textual ordering is a structural smoke check — it confirms the lock-acquire token
    /// appears before the prompt/prepare tokens in the file, which is the shape any
    /// regression that deletes or relocates the lock would have to defeat. It does not
    /// prove the lock has the right scope, the right key, or holds across the right
    /// statements (those are behavioural concerns covered by the unit + concurrency tests).
    /// </remarks>
    private static List<string> FindOrderingViolations(string source)
    {
        var violations = new List<string>();

        var firstAcquireIdx = source.IndexOf("AcquireAsync(", StringComparison.Ordinal);

        var promptRegex = new System.Text.RegularExpressions.Regex(
            @"\.PromptAsync\s*\(",
            System.Text.RegularExpressions.RegexOptions.Compiled);
        var prepareRegex = new System.Text.RegularExpressions.Regex(
            @"AgentExchangeCompletionGate\s*\.\s*PrepareTurn\s*\(",
            System.Text.RegularExpressions.RegexOptions.Compiled);

        foreach (System.Text.RegularExpressions.Match m in promptRegex.Matches(source))
        {
            if (firstAcquireIdx < 0 || m.Index < firstAcquireIdx)
            {
                violations.Add($"  PromptAsync at offset {m.Index} with no preceding AcquireAsync: " +
                    $"'{Snippet(source, m.Index)}'");
            }
        }

        foreach (System.Text.RegularExpressions.Match m in prepareRegex.Matches(source))
        {
            if (firstAcquireIdx < 0 || m.Index < firstAcquireIdx)
            {
                violations.Add($"  PrepareTurn at offset {m.Index} with no preceding AcquireAsync: " +
                    $"'{Snippet(source, m.Index)}'");
            }
        }

        return violations;
    }

    private static string Snippet(string source, int idx)
    {
        var start = Math.Max(0, idx - 10);
        var end = Math.Min(source.Length, idx + 60);
        return source[start..end].Replace("\n", "\\n").Replace("\r", "");
    }

    private static string LocateCrossWorldFederationControllerFile()
    {
        var srcRoot = FindSourceRoot();
        var path = Path.Combine(srcRoot, "gateway", "BotNexus.Gateway.Api", "Controllers", "CrossWorldFederationController.cs");
        File.Exists(path).ShouldBeTrue("Expected CrossWorldFederationController.cs at " + path);
        return path;
    }

    private static string LocateGatewayServiceCollectionExtensionsFile()
    {
        var srcRoot = FindSourceRoot();
        var path = Path.Combine(srcRoot, "gateway", "BotNexus.Gateway", "Extensions", "GatewayServiceCollectionExtensions.cs");
        File.Exists(path).ShouldBeTrue("Expected GatewayServiceCollectionExtensions.cs at " + path);
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
