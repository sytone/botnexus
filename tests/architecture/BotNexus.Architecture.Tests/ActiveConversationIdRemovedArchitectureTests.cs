namespace BotNexus.Architecture.Tests;

/// <summary>
/// Prevents the SignalR Blazor client from recreating ambient conversation identity after #3214.
/// Routes and explicit action arguments own conversation identity; the client state store retains
/// only the active agent and the explicit <c>SelectView</c> seam.
/// </summary>
public sealed class ActiveConversationIdRemovedArchitectureTests : ArchitectureTest
{
    private const string DeletedIdentifier = "ActiveConversationId";

    /// <summary>
    /// No production source in any SignalR BlazorClient project may mention the deleted ambient
    /// conversation identifier. Scanning the complete source contract also covers Razor-generated
    /// members that are not available to a reflection-only assembly scan.
    /// </summary>
    [Fact]
    public void SignalRBlazorClientProductionSource_DoesNotReferenceDeletedAmbientConversationIdentifier()
    {
        var extensionsRoot = Repository.Path("src", "extensions");
        var projectDirectories = Directory.EnumerateDirectories(
            extensionsRoot,
            "BotNexus.Extensions.Channels.SignalR.BlazorClient*",
            SearchOption.TopDirectoryOnly);

        var offenders = projectDirectories
            .SelectMany(directory => Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            .Where(IsProductionSourceFile)
            .Where(path => File.ReadAllText(path).Contains(DeletedIdentifier, StringComparison.Ordinal))
            .Select(path => Path.GetRelativePath(Repository.Root, path))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

        offenders.ShouldBeEmpty(
            $"{DeletedIdentifier} is deleted from the SignalR Blazor client contract. " +
            "Conversation identity must come from the route, an explicit action argument, MRU " +
            "lookup, or the SelectView value; do not recreate ambient conversation state.\n" +
            "Offenders:\n  " + string.Join("\n  ", offenders));
    }

    private static bool IsProductionSourceFile(string path)
    {
        var extension = Path.GetExtension(path);
        return (extension.Equals(".cs", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".razor", StringComparison.OrdinalIgnoreCase))
            && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            && !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal);
    }
}
