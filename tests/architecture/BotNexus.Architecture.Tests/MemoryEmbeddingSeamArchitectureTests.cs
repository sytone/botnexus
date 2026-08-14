using System.IO;
using System.Linq;
using System.Xml.Linq;
using Shouldly;

namespace BotNexus.Architecture.Tests;

/// <summary>
/// Architecture fitness function for the memory embedding seam boundary (#2855, criterion 2).
///
/// <para>
/// The embeddings feature was required to land with <b>zero diff under
/// <c>src/gateway/BotNexus.Memory</c></b>. That constraint is only meaningful if it keeps holding
/// after the PR merges: the natural next change - "just add a project reference so the memory store
/// can build its own generator" - would quietly couple the memory store to the whole provider
/// stack, and with it every transport, credential resolver and HTTP handler in that closure.
/// </para>
///
/// <para>
/// <c>BotNexus.Memory</c> consumes the provider-neutral <c>Microsoft.Extensions.AI</c> abstraction
/// and NOTHING else; the adapter that satisfies it lives on the provider side
/// (<c>EmbeddingProviderGenerator</c>) and is wired in composition
/// (<c>MemoryEmbeddingComposition</c>). This fence asserts that shape structurally rather than
/// socially, so a reviewer does not have to remember the rule.
/// </para>
/// </summary>
public sealed class MemoryEmbeddingSeamArchitectureTests
{
    private static string RepoRoot => FindRepoRoot();

    private static string MemoryProject =>
        Path.Combine(RepoRoot, "src", "gateway", "BotNexus.Memory", "BotNexus.Memory.csproj");

    [Fact]
    public void MemoryProject_HasNoProjectReferenceIntoTheProviderStack()
    {
        var references = ProjectReferencesOf(MemoryProject);

        var providerReferences = references
            .Where(r => r.Contains("BotNexus.Agent", StringComparison.OrdinalIgnoreCase))
            .ToList();

        providerReferences.ShouldBeEmpty(
            "BotNexus.Memory must consume the provider-neutral Microsoft.Extensions.AI seam only (#2855 AC2). "
            + "The IEmbeddingProvider adapter belongs on the provider side or in composition, not behind a "
            + "reference from the memory store into the provider stack. Offending: "
            + string.Join(", ", providerReferences));
    }

    [Fact]
    public void MemoryProject_ReferencesNoGatewayComposition()
    {
        // The other direction of the same rule: composition may reference memory, never the reverse.
        var references = ProjectReferencesOf(MemoryProject);

        references
            .Where(r => r.Contains("BotNexus.Gateway", StringComparison.OrdinalIgnoreCase))
            .ShouldBeEmpty("BotNexus.Memory must not depend on the gateway composition layer.");
    }

    [Fact]
    public void MemorySources_DoNotMentionTheEmbeddingProviderCapability()
    {
        // A `using BotNexus.Agent.Providers.Core.Embeddings;` anywhere under BotNexus.Memory means
        // the boundary was crossed by source even if the csproj edge came in transitively.
        var memoryDirectory = Path.Combine(RepoRoot, "src", "gateway", "BotNexus.Memory");
        var offenders = Directory
            .EnumerateFiles(memoryDirectory, "*.cs", SearchOption.AllDirectories)
            .Where(file => File.ReadAllText(file).Contains("BotNexus.Agent.Providers", StringComparison.Ordinal))
            .Select(file => Path.GetRelativePath(RepoRoot, file))
            .ToList();

        offenders.ShouldBeEmpty(
            "No source file under BotNexus.Memory may reference the provider stack (#2855 AC2). Offending: "
            + string.Join(", ", offenders));
    }

    [Fact]
    public void ProviderEmbeddingAdapter_DoesNotReferenceTheMemoryProject()
    {
        // The adapter satisfies the seam through Microsoft.Extensions.AI, so the provider project
        // must not need the memory assembly either. Neither side knows about the other.
        var providerCore = Path.Combine(
            RepoRoot, "src", "agent", "BotNexus.Agent.Providers.Core", "BotNexus.Agent.Providers.Core.csproj");

        ProjectReferencesOf(providerCore)
            .Where(r => r.Contains("BotNexus.Memory", StringComparison.OrdinalIgnoreCase))
            .ShouldBeEmpty(
                "BotNexus.Agent.Providers.Core adapts to Microsoft.Extensions.AI, not to BotNexus.Memory (#2855 AC2).");
    }

    private static List<string> ProjectReferencesOf(string csprojPath)
    {
        File.Exists(csprojPath).ShouldBeTrue($"Expected project file at {csprojPath}.");

        return XDocument.Load(csprojPath)
            .Descendants("ProjectReference")
            .Select(e => (string?)e.Attribute("Include") ?? string.Empty)
            .Where(v => v.Length > 0)
            .ToList();
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, ".git"))
                || File.Exists(Path.Combine(directory.FullName, ".git")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate the repository root from the test output directory.");
    }
}
