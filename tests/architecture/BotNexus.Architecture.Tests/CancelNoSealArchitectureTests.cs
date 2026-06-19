using System.Text.RegularExpressions;
using Shouldly;

namespace BotNexus.Architecture.Tests;

/// <summary>
/// Architecture fitness functions enforcing the <c>#553</c> contract: in
/// <see cref="System.Threading.CancellationToken"/>-aware code paths that seal the
/// session on any exception, the catch-all <c>catch (Exception)</c> block MUST be
/// preceded by a
/// <c>catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }</c>
/// clause so caller-initiated cancellation does not flip the session to
/// <c>SessionStatus.Sealed</c> and permanently break sender retries via the sealed-session
/// 409 guard in <c>ResolveSessionAsync</c>.
///
/// The fence covers the three pre-existing seal-on-error catch sites identified in #553:
/// <list type="bullet">
///   <item><c>CrossWorldFederationController.ExecuteRelayAsync</c></item>
///   <item><c>AgentExchangeService.ConverseAsync</c> (local agent-agent path)</item>
///   <item><c>AgentExchangeService.ConverseAsync</c> (cross-world relay-out path)</item>
/// </list>
/// </summary>
/// <remarks>
/// This is a <strong>smoke test</strong>, not a correctness proof. The fence shows that
/// a textual <c>catch (OperationCanceledException) when (...IsCancellationRequested) { throw; }</c>
/// clause appears before every <c>catch (Exception ...) { ... GatewaySessionStatus.Sealed ... }</c>
/// in the two target files. A clever regression that retained the textual ordering but
/// broke semantics (e.g. inverted the <c>when</c> condition) would pass this fence and
/// fail the behavioural tests in
/// <c>CrossWorldFederationControllerTests.RelayAsync_When*</c> and
/// <c>AgentExchangeServiceCancelNoSealTests</c>. The two layers complement; neither
/// alone is sufficient.
/// </remarks>
public sealed class CancelNoSealArchitectureTests
{
    [Fact]
    public void CrossWorldFederationController_SealOnErrorCatch_PrecededByCallerCancellationRethrow()
    {
        var path = LocateFile("gateway", "BotNexus.Gateway.Api", "Controllers", "CrossWorldFederationController.cs");
        var source = File.ReadAllText(path);

        var violations = FindMissingCancellationRethrow(source);

        violations.ShouldBeEmpty(BuildViolationMessage(path, violations));
    }

    [Fact]
    public void AgentExchangeService_SealOnErrorCatches_PrecededByCallerCancellationRethrow()
    {
        // #1542: the single seal-on-error catch-all (post-#1384) moved into
        // AgentExchangeTurnEngine.RunExchangeLoopAsync when the turn loop was extracted from
        // AgentExchangeService (SRP). The #553 cancellation invariant is unchanged — it now
        // lives in the engine file.
        var path = LocateFile("gateway", "BotNexus.Gateway", "Agents", "AgentExchangeTurnEngine.cs");
        var source = File.ReadAllText(path);

        var violations = FindMissingCancellationRethrow(source);

        violations.ShouldBeEmpty(BuildViolationMessage(path, violations));
    }

    [Fact]
    public void Fence_IsNotVacuous_AgainstSyntheticRegression_NoCancellationGuard()
    {
        // Synthetic shape: catch-all that seals with no preceding OCE rethrow. This is the
        // pre-#553 bug shape. The fence must flag it.
        const string syntheticViolation = """
            public async Task RelayAsync(CancellationToken cancellationToken)
            {
                try
                {
                    await sessionStore.SaveAsync(session, cancellationToken);
                    var response = await handle.PromptAsync(message, cancellationToken);
                }
                catch (Exception ex)
                {
                    session.Status = GatewaySessionStatus.Sealed;
                    await sessionStore.SaveAsync(session, CancellationToken.None);
                    throw;
                }
            }
            """;
        var violations = FindMissingCancellationRethrow(syntheticViolation);
        violations.ShouldNotBeEmpty(
            "Vacuity guard: the fence must flag a seal-on-error catch-all with no preceding " +
            "OperationCanceledException rethrow — this is exactly the pre-#553 bug shape. If " +
            "this assertion fails, the fence has been weakened so far that it cannot catch " +
            "the original bug class.");
    }

    [Fact]
    public void Fence_IsNotVacuous_AgainstSyntheticRegression_BareOceWithoutWhen()
    {
        // Subtler regression: a bare `catch (OperationCanceledException)` without the
        // `when (cancellationToken.IsCancellationRequested)` filter. This would catch ALL
        // OCEs including downstream-timeout-token cancellations and silently mask them as
        // "caller cancellation" — leaving the session Active when it should be sealed.
        const string syntheticViolation = """
            public async Task RelayAsync(CancellationToken cancellationToken)
            {
                try
                {
                    await handle.PromptAsync(message, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    session.Status = GatewaySessionStatus.Sealed;
                    throw;
                }
            }
            """;
        var violations = FindMissingCancellationRethrow(syntheticViolation);
        violations.ShouldNotBeEmpty(
            "Vacuity guard: the fence must flag a bare `catch (OperationCanceledException)` " +
            "without the `when (cancellationToken.IsCancellationRequested)` filter. A bare " +
            "catch swallows inner-token cancellations too, silently masking genuine failures " +
            "as caller cancellation. The discriminator is the whole point of the #553 fix.");
    }

    [Fact]
    public void Fence_DoesNotFalsePositive_OnLegitimateShape()
    {
        // Canonical shape from the #553 fix. The fence must NOT flag this — otherwise
        // authors will disable it instead of fixing real regressions.
        const string syntheticClean = """
            public async Task RelayAsync(CancellationToken cancellationToken)
            {
                try
                {
                    await sessionStore.SaveAsync(session, cancellationToken);
                    var response = await handle.PromptAsync(message, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    // Caller-initiated cancellation: rethrow without sealing so the session
                    // stays Active and the sender can retry.
                    throw;
                }
                catch (Exception ex)
                {
                    session.Status = GatewaySessionStatus.Sealed;
                    await sessionStore.SaveAsync(session, CancellationToken.None);
                    throw;
                }
            }
            """;
        var violations = FindMissingCancellationRethrow(syntheticClean);
        violations.ShouldBeEmpty(
            "False-positive guard: the fence must not flag the canonical post-#553 shape. " +
            "If this assertion fails, the fence is over-broad and authors will disable it " +
            "instead of using it.\n" +
            "Violations: " + string.Join("\n", violations));
    }

    [Fact]
    public void Fence_DoesNotFalsePositive_OnRethrowWithBraceInComment()
    {
        // The rethrow body intentionally includes a brace-bearing comment to pin the
        // regex loosening that permits one level of nested braces. Before the loosening
        // this shape would have been rejected, forcing a future author to either
        // delete the explanatory comment or disable the fence — exactly the friction
        // we want to avoid.
        const string syntheticClean = """
            public async Task RelayAsync(CancellationToken cancellationToken)
            {
                try
                {
                    await handle.PromptAsync(message, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    // Caller-initiated cancellation: rethrow without sealing. See
                    // ClearActiveSessionAsync { ... } for the diagnostic-only catches
                    // that are deliberately outside this fence's scope.
                    throw;
                }
                catch (Exception ex)
                {
                    session.Status = GatewaySessionStatus.Sealed;
                    await sessionStore.SaveAsync(session, CancellationToken.None);
                    throw;
                }
            }
            """;
        var violations = FindMissingCancellationRethrow(syntheticClean);
        violations.ShouldBeEmpty(
            "False-positive guard: the fence must accept a rethrow body containing a comment " +
            "with literal braces. If this fails, the body regex has been tightened back to " +
            "`[^{}]*` and the next author who keeps the comment will have their PR blocked " +
            "by an architecture test instead of by a real defect.\n" +
            "Violations: " + string.Join("\n", violations));
    }

    [Fact]
    public void CrossWorldFederationController_HasAtLeastOneSealWritingCatchAll()
    {
        // Counter-fence: if a future refactor moves the seal to a helper like
        // `SealSessionAsync(session)` (no literal `GatewaySessionStatus.Sealed` in the
        // catch body), the per-catch fence would silently pass with zero candidates.
        // This test asserts at least one Sealed-writing catch-all is structurally present.
        // If it fails, the author MUST either keep the literal token in the catch body
        // OR update the fence regex to track whatever the new seal-shape is.
        var path = LocateFile("gateway", "BotNexus.Gateway.Api", "Controllers", "CrossWorldFederationController.cs");
        var source = File.ReadAllText(path);

        var count = CountSealWritingCatchAlls(source);

        count.ShouldBeGreaterThanOrEqualTo(1,
            $"Counter-fence: {Path.GetFileName(path)} must contain at least one " +
            "`catch (Exception ...) {{ ... GatewaySessionStatus.Sealed ... }}` block. If the " +
            "seal-on-error logic has been extracted to a helper, the per-catch fence " +
            "(`*_SealOnErrorCatch_PrecededByCallerCancellationRethrow`) is no longer protecting " +
            "anything because it has zero candidates to check. Either inline the literal " +
            "`GatewaySessionStatus.Sealed` back into the catch body OR update the regex in " +
            "`CountSealWritingCatchAlls` / `FindMissingCancellationRethrow` to match the new shape.");
    }

    [Fact]
    public void AgentExchangeService_HasAtLeastOneSealWritingCatchAll()
    {
        // Counter-fence: AgentExchangeService used to have two seal-on-error catch sites (the
        // local agent-agent path in ConverseAsync and the cross-world relay-out path). As of
        // #1384 both paths delegate to the shared RunExchangeLoopAsync, which owns the single
        // seal-on-error catch-all. #1542: RunExchangeLoopAsync (and therefore that catch) moved
        // into AgentExchangeTurnEngine when the turn loop was extracted from the service. The
        // per-catch fence (`*_SealOnErrorCatches_PrecededByCallerCancellationRethrow`) still
        // polices that one consolidated catch in the engine (it must be preceded by the
        // `catch (OperationCanceledException) when (...)` rethrow). If a refactor collapses the
        // seal behind a helper (no literal `Sealed` in the catch body), this test forces the
        // author to either restore the literal or update the fence.
        var path = LocateFile("gateway", "BotNexus.Gateway", "Agents", "AgentExchangeTurnEngine.cs");
        var source = File.ReadAllText(path);

        var count = CountSealWritingCatchAlls(source);

        count.ShouldBeGreaterThanOrEqualTo(1,
            $"Counter-fence: {Path.GetFileName(path)} must contain at least one " +
            "`catch (Exception ...) {{ ... GatewaySessionStatus.Sealed ... }}` block. Post-#1384 " +
            "the local + cross-world seal-on-error logic is single-sourced in RunExchangeLoopAsync, " +
            "which #1542 moved into AgentExchangeTurnEngine. " +
            "See the comment in CrossWorldFederationController_HasAtLeastOneSealWritingCatchAll " +
            "for the rationale.");
    }

    [Fact]
    public void SealCatchDetector_IdentifiesPositiveShape()
    {
        // Synthetic positive case. Pins the detector against accidental over-tightening
        // (e.g. requiring `Sealed` to be on the same line as the assignment).
        const string syntheticSeal = """
            catch (Exception ex)
            {
                session.Status = GatewaySessionStatus.Sealed;
                await sessionStore.SaveAsync(session, CancellationToken.None);
                throw;
            }
            """;
        CountSealWritingCatchAlls(syntheticSeal).ShouldBe(1,
            "Detector vacuity guard: a synthetic catch-all that writes Sealed must be counted. " +
            "If this fails, the detector heuristic is broken and the counter-fence is vacuous.");
    }

    [Fact]
    public void SealCatchDetector_IgnoresNonSealingCatch()
    {
        // Synthetic negative case. The diagnostic-only catches in ClearActiveSessionAsync
        // (which swallow without sealing) MUST be ignored — otherwise the per-catch fence
        // would demand an OCE rethrow guard on every catch-all in the file, including
        // unrelated diagnostic ones.
        const string syntheticNoSeal = """
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to clear ActiveSessionId; swallowed.");
            }
            """;
        CountSealWritingCatchAlls(syntheticNoSeal).ShouldBe(0,
            "Detector specificity guard: a catch-all that does NOT write `GatewaySessionStatus.Sealed` " +
            "must not be counted. If this fails, the detector is over-broad and the fence will " +
            "force OCE guards onto unrelated diagnostic catches.");
    }

    /// <summary>
    /// Finds every <c>catch (Exception ...)</c> block whose body sets
    /// <c>GatewaySessionStatus.Sealed</c> (i.e. the bug-relevant seal-on-error catch-all)
    /// and verifies the preceding text (within ~500 chars upstream) contains a
    /// <c>catch (OperationCanceledException) when (...IsCancellationRequested...) { throw; }</c>
    /// clause.
    /// </summary>
    /// <remarks>
    /// The fence is textual and scoped to the file. It doesn't prove the <c>when</c> filter
    /// references the same token the catch's enclosing method threads down to PromptAsync —
    /// that's a behavioural concern covered by the unit tests. What it DOES prove is the
    /// structural shape that any regression would have to defeat (delete the OCE clause,
    /// invert the filter, change the body to something other than <c>throw</c>).
    /// </remarks>
    private static List<string> FindMissingCancellationRethrow(string source)
    {
        var violations = new List<string>();

        foreach (Match m in SealCatchRegex.Matches(source))
        {
            // Look at the next ~600 chars of the catch body to confirm it sets Sealed.
            // Skip non-relevant catch-all blocks (e.g. swallow-only diagnostic catches in
            // ClearActiveSessionAsync).
            var bodyEnd = Math.Min(source.Length, m.Index + 600);
            var body = source[m.Index..bodyEnd];
            if (!body.Contains("GatewaySessionStatus.Sealed", StringComparison.Ordinal))
            {
                continue;
            }

            // Look back ~1500 chars for the cancellation-rethrow clause. A larger window
            // is required because the rethrow catch carries a substantial doc comment
            // explaining the #553 rationale; a tighter window would false-positive there.
            var lookbackStart = Math.Max(0, m.Index - 1500);
            var preceding = source[lookbackStart..m.Index];

            if (!RethrowClauseRegex.IsMatch(preceding))
            {
                violations.Add(
                    $"  catch-all at offset {m.Index} that seals the session has no preceding " +
                    $"`catch (OperationCanceledException) when (...IsCancellationRequested) {{ throw; }}` clause " +
                    $"within the prior 1500 chars. Snippet: '{Snippet(source, m.Index)}'");
            }
        }

        return violations;
    }

    /// <summary>
    /// Counts the number of <c>catch (Exception ...)</c> blocks in <paramref name="source"/>
    /// whose body (next ~600 chars) contains <c>GatewaySessionStatus.Sealed</c>. Used by the
    /// counter-fence to assert that the per-catch fence has candidates to check.
    /// </summary>
    private static int CountSealWritingCatchAlls(string source)
    {
        var count = 0;
        foreach (Match m in SealCatchRegex.Matches(source))
        {
            var bodyEnd = Math.Min(source.Length, m.Index + 600);
            var body = source[m.Index..bodyEnd];
            if (body.Contains("GatewaySessionStatus.Sealed", StringComparison.Ordinal))
            {
                count++;
            }
        }
        return count;
    }

    private static readonly Regex SealCatchRegex = new(
        @"catch\s*\(\s*Exception\s+\w+\s*\)",
        RegexOptions.Compiled);

    // Loosened body alternation permits one level of nested braces so brace-bearing
    // comments inside the rethrow body don't false-positive the fence. See
    // Fence_DoesNotFalsePositive_OnRethrowWithBraceInComment.
    private static readonly Regex RethrowClauseRegex = new(
        @"catch\s*\(\s*OperationCanceledException\s*\)\s*when\s*\(\s*[\w\.]*\bIsCancellationRequested\b\s*\)\s*\{(?:[^{}]|\{[^{}]*\})*\bthrow\s*;(?:[^{}]|\{[^{}]*\})*\}",
        RegexOptions.Compiled | RegexOptions.Singleline);

    private static string Snippet(string source, int idx)
    {
        var start = Math.Max(0, idx - 10);
        var end = Math.Min(source.Length, idx + 80);
        return source[start..end].Replace("\n", "\\n").Replace("\r", "");
    }

    private static string BuildViolationMessage(string path, List<string> violations) =>
        $"{Path.GetFileName(path)} has a `catch (Exception)` block that seals the session " +
        "(GatewaySessionStatus.Sealed) but is NOT preceded by a " +
        "`catch (OperationCanceledException) when (...IsCancellationRequested) {{ throw; }}` clause. " +
        "This is the #553 structural invariant: caller-initiated cancellation must NOT seal the " +
        "session, otherwise the sender's retry policy hits the sealed-session 409 guard and the " +
        "exchange is permanently broken by a transient client-side timeout.\n" +
        "Violations:\n" + string.Join("\n", violations) + "\n" +
        "File: " + path;

    private static string LocateFile(params string[] relativeParts)
    {
        var srcRoot = FindSourceRoot();
        var path = Path.Combine(new[] { srcRoot }.Concat(relativeParts).ToArray());
        File.Exists(path).ShouldBeTrue("Expected " + Path.GetFileName(path) + " at " + path);
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
