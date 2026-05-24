using Shouldly;

namespace BotNexus.Architecture.Tests;

/// <summary>
/// Architecture fitness functions enforcing the Phase 3d invariant (#537):
/// the <c>systemPromptInitialized</c> session-metadata flag has been deleted
/// in favour of the natural <c>session.History.Count == 0</c> invariant. No
/// production code may reintroduce the literal — doing so would mean a future
/// change went looking for the flag, didn't find it, and silently invented its
/// own re-implementation of "has this session been initialised?", drifting from
/// the canonical <c>GatewayHost.ShouldInitializeSystemPrompt</c>.
/// </summary>
/// <remarks>
/// <para>
/// The metadata flag existed because the pre-3a compactor <em>deleted</em>
/// summarised turns, so the natural "history is non-empty" signal would have
/// flipped back to false after every compaction and re-initialised the system
/// prompt mid-conversation. Phase 3a fixed compaction to mark-not-delete, which
/// closed the only remaining ambiguity and made the flag redundant. Phase 3d
/// removed it.
/// </para>
/// </remarks>
public sealed class SystemPromptInitMetadataArchitectureTests
{
    /// <summary>
    /// No file under <c>src/</c> may contain the literal <c>"systemPromptInitialized"</c>.
    /// The flag was deleted in Phase 3d (#537); any reappearance is almost certainly an
    /// accidental reintroduction of the deprecated metadata gate.
    /// </summary>
    [Fact]
    public void NoCode_References_systemPromptInitialized_Literal()
    {
        var srcRoot = FindSourceRoot();

        var offenders = Directory
            .EnumerateFiles(srcRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(path =>
            {
                var text = File.ReadAllText(path);
                return text.Contains("systemPromptInitialized", StringComparison.Ordinal);
            })
            .Select(path => Path.GetRelativePath(srcRoot, path))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToArray();

        offenders.ShouldBeEmpty(
            "Files under src/ reference the literal \"systemPromptInitialized\" — a session-metadata " +
            "key that was deleted in Phase 3d (#537) in favour of the natural " +
            "session.History.Count == 0 invariant in GatewayHost.ShouldInitializeSystemPrompt. " +
            "Remove the reference; if you need to gate behaviour on \"has the system prompt been " +
            "loaded?\", route through GatewayHost or use session.History.Count directly.\n" +
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
