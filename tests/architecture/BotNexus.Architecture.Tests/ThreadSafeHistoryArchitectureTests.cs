using Shouldly;

namespace BotNexus.Architecture.Tests;

/// <summary>
/// Architecture fitness functions enforcing the thread-safe history-mutation
/// invariant introduced for #532: every mutation of <c>Session.History</c>
/// must go through <c>GatewaySessionRuntime</c> so it observes the lock and
/// the addition/destructive version counters. Any inline mutation outside
/// the runtime defeats the optimistic-concurrency apply path
/// (<c>SnapshotHistoryForCompaction</c> /
/// <c>TryReplaceHistoryFromSnapshot</c>) and silently re-opens the race the
/// compactor fix was meant to close.
/// </summary>
public sealed class ThreadSafeHistoryArchitectureTests
{
    /// <summary>
    /// No file in <c>src/</c> outside the allowlist may call a mutating method
    /// (<c>Add</c>, <c>AddRange</c>, <c>Insert</c>, <c>Clear</c>, <c>Remove</c>,
    /// <c>RemoveAt</c>, <c>RemoveAll</c>) on a <c>.History.</c> accessor. The
    /// allowlist contains only <c>GatewaySessionRuntime.cs</c> — the single
    /// place that holds the lock and bumps the version counters.
    /// </summary>
    /// <remarks>
    /// The regex anchors on the literal substring <c>.History.</c> so it
    /// matches both <c>Session.History.Add(...)</c> and the
    /// <c>GatewaySession.History</c> alias (which returns the same list
    /// reference). It does NOT match local variables that happen to end in
    /// "History" (e.g. <c>newHistory.Add(...)</c> inside the compactor,
    /// <c>streamedHistory.Clear()</c> inside the streaming helper) because
    /// those don't contain the <c>.History.</c> sequence.
    /// </remarks>
    [Fact]
    public void NoDirectHistoryMutation_OutsideGatewaySessionRuntime()
    {
        var srcRoot = FindSourceRoot();
        var allowlist = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "GatewaySessionRuntime.cs",
        };

        var pattern = new System.Text.RegularExpressions.Regex(
            @"\.History\.(Add|AddRange|Insert|Clear|Remove|RemoveAt|RemoveAll)\s*\(",
            System.Text.RegularExpressions.RegexOptions.Compiled);

        var offenders = Directory
            .EnumerateFiles(srcRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(path => !allowlist.Contains(Path.GetFileName(path)))
            .Where(path => pattern.IsMatch(File.ReadAllText(path)))
            .Select(path => Path.GetRelativePath(srcRoot, path))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToArray();

        offenders.ShouldBeEmpty(
            "Files outside the allowlist mutate Session.History directly, bypassing " +
            "GatewaySessionRuntime's lock + version counters. This re-opens the #532 " +
            "race: a mutation during the LLM summary window will not bump the " +
            "destructive-version counter, and the compactor's optimistic apply path " +
            "will overwrite the concurrent change. Use AddEntry/AddEntries/ReplaceHistory " +
            "on GatewaySession instead.\n" +
            "Offenders:\n  " + string.Join("\n  ", offenders));
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
