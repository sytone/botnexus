using System.Text.RegularExpressions;
using Shouldly;

namespace BotNexus.Architecture.Tests;

/// <summary>
/// Architecture fence ensuring no production code writes to the dead
/// <c>session.Metadata["conversationStatus"]</c> shadow key.
/// </summary>
/// <remarks>
/// The metadata key <c>conversationStatus</c> was a redundant shadow of the typed
/// <c>session.Status</c> property (GatewaySessionStatus enum). It was written at 6
/// call sites in <c>AgentExchangeService.cs</c> and
/// <c>CrossWorldFederationController.cs</c> but never read by any code — the typed
/// <c>Status</c> field is the canonical state. Removed in #1019 (Part of #612 CC-2).
/// </remarks>
public sealed class ConversationStatusMetadataRemovedTests
{
    /// <summary>
    /// No production file under <c>src/</c> may write or read the dead
    /// <c>Metadata["conversationStatus"]</c> key. The typed
    /// <c>GatewaySessionStatus</c> enum on <c>session.Status</c> is the only
    /// correct way to express session seal/error state.
    /// </summary>
    [Fact]
    public void NoCode_References_ConversationStatus_Metadata_Key()
    {
        var srcRoot = FindSourceRoot();
        var pattern = new Regex(
            @"Metadata\s*\[\s*""conversationStatus""\s*\]",
            RegexOptions.Compiled);

        var offenders = Directory
            .EnumerateFiles(srcRoot, "*.cs", SearchOption.AllDirectories)
            .Where(IsProductionSource)
            .Where(path => pattern.IsMatch(File.ReadAllText(path)))
            .Select(path => Path.GetRelativePath(srcRoot, path))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToArray();

        offenders.ShouldBeEmpty(
            "Files under src/ reference Metadata[\"conversationStatus\"] — this dead shadow " +
            "key was removed in #1019 (Part of #612 CC-2). Use the typed session.Status " +
            "(GatewaySessionStatus enum) instead. The metadata key was never read by any " +
            "code and only duplicated information already expressed by the Status property.\n" +
            "Offenders:\n  " + string.Join("\n  ", offenders));
    }

    private static string FindSourceRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "BotNexus.slnx")))
            dir = dir.Parent;

        return dir is not null
            ? Path.Combine(dir.FullName, "src")
            : throw new InvalidOperationException("Cannot locate repo root (BotNexus.slnx not found).");
    }

    private static bool IsProductionSource(string path) =>
        !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
        !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal);
}
