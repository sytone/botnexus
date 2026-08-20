using System.Text.RegularExpressions;

namespace BotNexus.Architecture.Tests;

/// <summary>
/// #2553: the cron job name is operator/agent-supplied. Every producer that copies it into
/// <c>InternalTriggerRequest.JobName</c>, and the single consumer that renders it into a
/// conversation title, must route through <c>ExternalText.Sanitize</c> so the newline /
/// control-character / length policy cannot drift per call site.
/// </summary>
public sealed class CronJobNameSanitizationArchitectureTests : ArchitectureTest
{
    private static readonly string[] s_producers =
    [
        Path.Combine("gateway", "BotNexus.Cron", "ActionStubs", "AgentPromptActionStub.cs"),
        Path.Combine("gateway", "BotNexus.Cron", "Actions", "HeartbeatAction.cs"),
        Path.Combine("gateway", "BotNexus.Cron", "Actions", "MemoryDreamingCronAction.cs"),
        Path.Combine("gateway", "BotNexus.Cron", "Actions", "SkillReviewCronAction.cs"),
    ];

    // `JobName = <something other than a Sanitize call>` assigned from the job name.
    private static readonly Regex s_rawAssignment = new(
        @"JobName\s*=\s*context\.Job\.Name",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex s_sanitizedAssignment = new(
        @"JobName\s*=\s*ExternalText\.Sanitize\(\s*context\.Job\.Name",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    [Fact]
    public void EveryKnownProducer_Exists_And_RoutesJobNameThroughExternalTextSanitize()
    {
        var srcRoot = Repository.SourceRoot;
        var problems = new List<string>();

        foreach (var relative in s_producers)
        {
            var full = Path.Combine(srcRoot, relative);
            if (!File.Exists(full))
            {
                problems.Add($"{relative} - producer file not found (rename? update this fence)");
                continue;
            }

            var text = File.ReadAllText(full);
            if (!s_sanitizedAssignment.IsMatch(text))
                problems.Add($"{relative} - does not assign JobName = ExternalText.Sanitize(context.Job.Name, ...)");
        }

        problems.ShouldBeEmpty(
            "#2553: all four cron JobName producers must normalise the operator-supplied job name " +
            "through the single ExternalText.Sanitize seam.\nProblems:\n  " + string.Join("\n  ", problems));
    }

    [Fact]
    public void NoProductionSourceFile_Assigns_JobName_FromRawJobName()
    {
        var srcRoot = Repository.SourceRoot;
        var violations = new List<string>();

        foreach (var path in EnumerateProductionCsFiles(srcRoot))
        {
            var text = File.ReadAllText(path);
            if (!s_rawAssignment.IsMatch(text)) continue;
            if (s_sanitizedAssignment.IsMatch(text) && !HasUnsanitizedAssignment(text)) continue;
            violations.Add(ToRelative(srcRoot, path));
        }

        violations.ShouldBeEmpty(
            "#2553: production code must not copy a raw cron job name into InternalTriggerRequest.JobName. " +
            "Wrap it in ExternalText.Sanitize(context.Job.Name, ExternalText.DefaultDisplayLength).\n" +
            "Violations:\n  " + string.Join("\n  ", violations));
    }

    /// <summary>Vacuity guard: both regexes must actually match their intended shapes.</summary>
    [Fact]
    public void Fence_Regexes_MatchTheirTargetShapes()
    {
        s_rawAssignment.IsMatch("JobName = context.Job.Name,").ShouldBeTrue();
        s_rawAssignment.IsMatch("JobName = ExternalText.Sanitize(context.Job.Name, 200),").ShouldBeFalse();
        s_sanitizedAssignment.IsMatch("JobName = ExternalText.Sanitize(context.Job.Name, 200),").ShouldBeTrue();
        s_sanitizedAssignment.IsMatch("JobName = context.Job.Name,").ShouldBeFalse();
        HasUnsanitizedAssignment("JobName = context.Job.Name,").ShouldBeTrue();
        HasUnsanitizedAssignment("JobName = ExternalText.Sanitize(context.Job.Name, 200),").ShouldBeFalse();
    }

    private static bool HasUnsanitizedAssignment(string text)
    {
        foreach (Match match in s_rawAssignment.Matches(text))
        {
            var prefixStart = Math.Max(0, match.Index);
            var window = text.Substring(prefixStart, Math.Min(text.Length - prefixStart, match.Length + 4));
            if (!s_sanitizedAssignment.IsMatch(window)) return true;
        }
        return false;
    }

    private static IEnumerable<string> EnumerateProductionCsFiles(string srcRoot) =>
        Directory.EnumerateFiles(srcRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path =>
                !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) &&
                !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase));

    private static string ToRelative(string srcRoot, string fullPath)
    {
        var full = Path.GetFullPath(fullPath);
        var root = Path.GetFullPath(srcRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? full[root.Length..] : full;
    }

}
