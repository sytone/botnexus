using Shouldly;

namespace BotNexus.Architecture.Tests;

/// <summary>
/// Architecture fitness functions enforcing the F-11 contract: an agent-to-agent exchange
/// completes <strong>only</strong> when the target agent invokes the structured
/// <c>finish_agent_exchange</c> tool (validated by the active-exchange-id metadata gate).
/// String matching on the response Content is banned in the completion-decision path —
/// it is brittle (false positives from narrative text or code blocks) and exploitable
/// via active prompt injection (an upstream RAG hit containing the magic phrase would
/// terminate the exchange against the operator's intent).
/// </summary>
/// <remarks>
/// Pattern follows the multi-model-critique-approved fence shape from PR #549:
/// (a) compound check (multiple banned shapes, not just one),
/// (b) vacuity self-test (proves the regex matches something so the fence isn't a no-op),
/// (c) synthetic-violation self-test (proves the regex fires when violated),
/// (d) synthetic-clean self-test (proves the regex doesn't fire on legitimate code).
/// </remarks>
public sealed class AgentExchangeCompletionArchitectureTests
{
    [Fact]
    public void AgentExchangeService_HasNoSubstringMatchInCompletionDecision()
    {
        AssertNoSubstringHeuristic(LocateAgentExchangeServiceFile());
    }

    [Fact]
    public void CrossWorldFederationController_HasNoSubstringMatchInCompletionDecision()
    {
        // The cross-world receiver is the second site that makes completion decisions for an
        // agent-to-agent exchange. If a future change reintroduces a substring/regex heuristic
        // here, the F-11 XPIA vector reopens on the receiver side (an attacker who controls
        // remote content in a cross-world relay could terminate exchanges prematurely on the
        // local gateway). Per plan-vs-impl critique NB-3 on PR #553, scan both files.
        AssertNoSubstringHeuristic(LocateCrossWorldFederationControllerFile());
    }

    private static void AssertNoSubstringHeuristic(string path)
    {
        var source = File.ReadAllText(path);
        var violations = FindBannedShapes(source);

        violations.ShouldBeEmpty(
            $"{Path.GetFileName(path)} contains substring-style heuristics on agent response " +
            "Content in what looks like a completion-decision context. The F-11 contract " +
            "requires authoritative completion via a structured finish_agent_exchange tool " +
            "call (validated by the active-exchange-id metadata gate) — never substring " +
            "matching, regex matching, or .StartsWith/.EndsWith on the response text.\n" +
            "Banned shapes found:\n" + string.Join("\n", violations) + "\n" +
            "File: " + path);
    }

    [Fact]
    public void Fence_IsNotVacuous_AgainstSyntheticViolation()
    {
        const string syntheticViolation = """
            public bool IsObjectiveMet(string finalResponse)
            {
                if (finalResponse.Contains("OBJECTIVE MET", StringComparison.OrdinalIgnoreCase))
                    return true;
                return false;
            }
            """;
        var violations = FindBannedShapes(syntheticViolation);
        violations.ShouldNotBeEmpty(
            "Vacuity guard: the fence regex must match the synthetic violation pattern " +
            "(string.Contains on the response variable). If this assertion fails, the " +
            "fence has been weakened so far that it cannot catch the original F-11 bug.");
    }

    [Fact]
    public void Fence_DoesNotFalsePositive_OnLegitimateCode()
    {
        // Legitimate completion-decision logic uses ToolCalls + metadata equality — no string ops on response.
        const string syntheticClean = """
            private static bool TryConsumeFinishSignal(AgentResponse response, GatewaySession refreshed, string expectedExchangeId)
            {
                var toolCalled = response.ToolCalls.Any(tc =>
                    !tc.IsError && string.Equals(tc.ToolName, "finish_agent_exchange", StringComparison.OrdinalIgnoreCase));
                if (!toolCalled) return false;
                var finishedExchangeId = MetadataString(refreshed.Metadata, FinishAgentExchangeTool.FinishedExchangeIdKey);
                return string.Equals(finishedExchangeId, expectedExchangeId, StringComparison.Ordinal);
            }
            """;
        var violations = FindBannedShapes(syntheticClean);
        violations.ShouldBeEmpty(
            "False-positive guard: the fence must not flag the canonical tool-call-based " +
            "completion logic. If this assertion fails, the fence is over-broad and will " +
            "force authors to disable it instead of fixing real bugs.\n" +
            "Violations: " + string.Join("\n", violations));
    }

    private static List<string> FindBannedShapes(string source)
    {
        var violations = new List<string>();

        // Banned: any string operation against a variable named like the agent response text.
        // Common identifiers in this file: finalResponse, response.Content, latestResponse.
        var responseLike = @"(finalResponse|latestResponse|response\.Content|\.FinalResponse|relayResponse\.Response)";
        var bannedOps = new[]
        {
            (Label: ".Contains(...)",   Regex: $@"{responseLike}\s*\.\s*Contains\s*\("),
            (Label: ".StartsWith(...)", Regex: $@"{responseLike}\s*\.\s*StartsWith\s*\("),
            (Label: ".EndsWith(...)",   Regex: $@"{responseLike}\s*\.\s*EndsWith\s*\("),
            (Label: ".IndexOf(...)",    Regex: $@"{responseLike}\s*\.\s*IndexOf\s*\("),
            (Label: "Regex.IsMatch(response)", Regex: $@"Regex\.IsMatch\s*\(\s*{responseLike}"),
            (Label: "Regex.Match(response)",   Regex: $@"Regex\.Match\s*\(\s*{responseLike}"),
        };

        foreach (var (label, pattern) in bannedOps)
        {
            var matches = new System.Text.RegularExpressions.Regex(
                pattern,
                System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled)
                .Matches(source);
            foreach (System.Text.RegularExpressions.Match m in matches)
            {
                violations.Add($"  {label} at offset {m.Index}: '{Snippet(source, m.Index)}'");
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

    private static string LocateAgentExchangeServiceFile()
    {
        var srcRoot = FindSourceRoot();
        var path = Path.Combine(srcRoot, "gateway", "BotNexus.Gateway", "Agents", "AgentExchangeService.cs");
        File.Exists(path).ShouldBeTrue("Expected AgentExchangeService.cs at " + path);
        return path;
    }

    private static string LocateCrossWorldFederationControllerFile()
    {
        var srcRoot = FindSourceRoot();
        var path = Path.Combine(srcRoot, "gateway", "BotNexus.Gateway.Api", "Controllers", "CrossWorldFederationController.cs");
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
        var srcRoot = Path.Combine(current.FullName, "src");
        Directory.Exists(srcRoot).ShouldBeTrue("Expected src/ under " + current.FullName);
        return srcRoot;
    }
}
