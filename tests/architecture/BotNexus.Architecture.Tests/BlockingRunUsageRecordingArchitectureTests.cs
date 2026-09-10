using System.Text.RegularExpressions;

namespace BotNexus.Architecture.Tests;

/// <summary>
/// Every blocking <c>handle.PromptAsync</c> boundary must stamp the provider's usage onto the
/// session it just spent tokens on.
/// </summary>
/// <remarks>
/// <para>
/// This is a fence rather than a one-off fix because the same class of gap has been retrofitted
/// at these boundaries three separate times: #2614 (tool audit on the REST and cross-world
/// paths), #2616 (the ralph runner) and #2127 (soul reflection-on-seal). Each time a new blocking
/// caller appeared it quietly missed a cross-cutting concern the older ones had, and nothing
/// failed — the run worked, only the record was thin.
/// </para>
/// <para>
/// Usage recording has exactly that shape. A missing call leaves
/// <c>lastProviderPromptTokens</c> unwritten, so the compactor silently falls back to its
/// <c>chars/4</c> estimator for that session, and the cache-efficiency counters stay empty. On the
/// REST path this survived long enough to be measured on a live gateway before anyone noticed.
/// </para>
/// </remarks>
public sealed class BlockingRunUsageRecordingArchitectureTests : ArchitectureTest
{
    /// <summary>How far after the call a <c>Record</c> may appear and still count as covering it.</summary>
    private const int WindowLines = 40;

    /// <summary>
    /// Matches a handle-level blocking prompt. Deliberately excludes <c>_agent.PromptAsync</c>,
    /// which is the inner call the handle itself makes and has no session to record against.
    /// </summary>
    private static readonly Regex BlockingPrompt = new(
        @"await\s+\w*[Hh]andle\w*\.PromptAsync\(", RegexOptions.Compiled);

    /// <summary>
    /// Boundaries deliberately not recording, with the reason. A blocking run against a session
    /// this metadata was never meant to describe would change that session's compaction
    /// behaviour, which is a behavioural decision rather than a reporting fix.
    /// </summary>
    private static readonly Dictionary<string, string> Exemptions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["src/gateway/BotNexus.Gateway/Agents/DefaultSubAgentManager.cs"] =
                "Runs against a CHILD session the parent's usage does not describe; recording there " +
                "would start driving compaction on sub-agent sessions that never had it.",
            ["src/gateway/BotNexus.Gateway/Agents/AgentExchangeService.cs"] =
                "Prompts a PEER agent's session, not the caller's. Same reasoning as sub-agents.",
        };

    [Fact]
    public void EveryBlockingPromptBoundary_RecordsProviderUsage()
    {
        var offenders = new List<string>();

        foreach (var file in EnumerateSourceFiles())
        {
            var relative = ToRepoRelative(file);
            if (Exemptions.ContainsKey(relative))
                continue;

            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                if (!BlockingPrompt.IsMatch(lines[i]))
                    continue;

                var end = Math.Min(lines.Length, i + 1 + WindowLines);
                var covered = false;
                for (var j = i; j < end && !covered; j++)
                    covered = lines[j].Contains("ProviderTokenUsageRecorder.Record", StringComparison.Ordinal);

                if (!covered)
                    offenders.Add($"{relative}:{i + 1}");
            }
        }

        offenders.ShouldBeEmpty(
            "every blocking PromptAsync boundary must call ProviderTokenUsageRecorder.Record on the " +
            "session it spent tokens against, within " + WindowLines + " lines. Without it the " +
            "compactor reads no provider prompt count for that session and falls back to chars/4, " +
            "and the cache-efficiency counters stay empty. If a boundary legitimately prompts a " +
            "different agent's session, add it to Exemptions with the reason. Offenders: " +
            string.Join(", ", offenders));
    }

    [Fact]
    public void TheFenceActuallySeesTheBoundaries()
    {
        // A regex fence that matches nothing passes forever. Pin that it still finds the known
        // recording boundaries, so a rename cannot turn this into a no-op.
        var matches = EnumerateSourceFiles()
            .Sum(file => File.ReadAllLines(file).Count(line => BlockingPrompt.IsMatch(line)));

        matches.ShouldBeGreaterThanOrEqualTo(
            8,
            "the blocking-boundary pattern stopped matching; the fence is inspecting nothing.");
    }

    [Fact]
    public void EveryExemptionStillExists()
    {
        // An exemption for a deleted file is a silent hole the next similar file falls through.
        foreach (var (relative, _) in Exemptions)
        {
            File.Exists(Path.Combine(Repository.Root, relative.Replace('/', Path.DirectorySeparatorChar)))
                .ShouldBeTrue($"exemption names a file that no longer exists: {relative}");
        }
    }

    private List<string> EnumerateSourceFiles()
        => [.. Directory.EnumerateFiles(Path.Combine(Repository.Root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))];

    private string ToRepoRelative(string absolutePath)
        => absolutePath[Repository.Root.Length..].TrimStart(Path.DirectorySeparatorChar).Replace('\\', '/');
}
