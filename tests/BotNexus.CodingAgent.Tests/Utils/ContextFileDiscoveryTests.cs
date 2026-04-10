using System.Text;
using BotNexus.CodingAgent.Utils;
using FluentAssertions;
using System.IO.Abstractions.TestingHelpers;

namespace BotNexus.CodingAgent.Tests.Utils;

public sealed class ContextFileDiscoveryTests
{
    private readonly MockFileSystem _fileSystem = new();
    private readonly string _testRoot = @"C:\context-discovery";

    [Fact]
    public async Task DiscoverAsync_FindsInstructionsInParentDirectory()
    {
        var repoRoot = Path.Combine(_testRoot, "repo");
        var workingDirectory = Path.Combine(repoRoot, "src", "feature");
        _fileSystem.Directory.CreateDirectory(Path.Combine(repoRoot, ".git"));
        _fileSystem.Directory.CreateDirectory(Path.Combine(repoRoot, ".github"));
        _fileSystem.Directory.CreateDirectory(workingDirectory);
        await _fileSystem.File.WriteAllTextAsync(Path.Combine(repoRoot, ".github", "copilot-instructions.md"), "parent instructions");

        var discovered = await ContextFileDiscovery.DiscoverAsync(_fileSystem, workingDirectory, CancellationToken.None);

        discovered.Should().Contain(file => file.Content.Contains("parent instructions", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DiscoverAsync_FindsInstructionsMdAlongAncestors()
    {
        var repoRoot = Path.Combine(_testRoot, "repo");
        var workingDirectory = Path.Combine(repoRoot, "src", "feature");
        _fileSystem.Directory.CreateDirectory(Path.Combine(repoRoot, ".git"));
        _fileSystem.Directory.CreateDirectory(workingDirectory);
        await _fileSystem.File.WriteAllTextAsync(Path.Combine(repoRoot, "INSTRUCTIONS.md"), "runtime instructions");

        var discovered = await ContextFileDiscovery.DiscoverAsync(_fileSystem, workingDirectory, CancellationToken.None);

        discovered.Should().Contain(file => file.Content.Contains("runtime instructions", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DiscoverAsync_StopsAtGitBoundary()
    {
        var outsideRoot = Path.Combine(_testRoot, "outside");
        var repoRoot = Path.Combine(outsideRoot, "repo");
        var workingDirectory = Path.Combine(repoRoot, "src");
        _fileSystem.Directory.CreateDirectory(Path.Combine(repoRoot, ".git"));
        _fileSystem.Directory.CreateDirectory(Path.Combine(repoRoot, ".github"));
        _fileSystem.Directory.CreateDirectory(Path.Combine(outsideRoot, ".github"));
        _fileSystem.Directory.CreateDirectory(workingDirectory);
        await _fileSystem.File.WriteAllTextAsync(Path.Combine(repoRoot, ".github", "copilot-instructions.md"), "repo instructions");
        await _fileSystem.File.WriteAllTextAsync(Path.Combine(outsideRoot, ".github", "copilot-instructions.md"), "outside instructions");

        var discovered = await ContextFileDiscovery.DiscoverAsync(_fileSystem, workingDirectory, CancellationToken.None);

        discovered.Should().Contain(file => file.Content.Contains("repo instructions", StringComparison.Ordinal));
        discovered.Should().NotContain(file => file.Content.Contains("outside instructions", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DiscoverAsync_ClosestPathWinsOnConflict()
    {
        var repoRoot = Path.Combine(_testRoot, "repo");
        var child = Path.Combine(repoRoot, "app");
        _fileSystem.Directory.CreateDirectory(Path.Combine(repoRoot, ".git"));
        _fileSystem.Directory.CreateDirectory(child);
        await _fileSystem.File.WriteAllTextAsync(Path.Combine(repoRoot, "AGENTS.md"), "parent");
        await _fileSystem.File.WriteAllTextAsync(Path.Combine(child, "AGENTS.md"), "child");

        var discovered = await ContextFileDiscovery.DiscoverAsync(_fileSystem, child, CancellationToken.None);
        var agentsFiles = discovered.Where(file => file.Path.EndsWith("AGENTS.md", StringComparison.OrdinalIgnoreCase)).ToList();

        agentsFiles.Should().ContainSingle();
        agentsFiles[0].Content.Should().Be("child");
    }

    [Fact]
    public async Task DiscoverAsync_UsesConfigDirectoryNameForAgentsLookup()
    {
        var repoRoot = Path.Combine(_testRoot, "repo");
        var workingDirectory = Path.Combine(repoRoot, "src");
        _fileSystem.Directory.CreateDirectory(Path.Combine(repoRoot, ".git"));
        _fileSystem.Directory.CreateDirectory(Path.Combine(repoRoot, ".custom-agent"));
        _fileSystem.Directory.CreateDirectory(workingDirectory);
        await _fileSystem.File.WriteAllTextAsync(Path.Combine(repoRoot, ".custom-agent", "AGENTS.md"), "custom agent instructions");

        var discovered = await ContextFileDiscovery.DiscoverAsync(_fileSystem, workingDirectory, CancellationToken.None, ".custom-agent");

        discovered.Should().Contain(file => file.Content.Contains("custom agent instructions", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DiscoverAsync_StaysWithinContextBudget()
    {
        var repoRoot = Path.Combine(_testRoot, "repo");
        var workingDirectory = Path.Combine(repoRoot, "src");
        _fileSystem.Directory.CreateDirectory(Path.Combine(repoRoot, ".git"));
        _fileSystem.Directory.CreateDirectory(Path.Combine(repoRoot, ".github"));
        _fileSystem.Directory.CreateDirectory(workingDirectory);
        await _fileSystem.File.WriteAllTextAsync(Path.Combine(repoRoot, ".github", "copilot-instructions.md"), new string('a', 20_000));
        await _fileSystem.File.WriteAllTextAsync(Path.Combine(workingDirectory, "AGENTS.md"), new string('b', 8_000));

        var discovered = await ContextFileDiscovery.DiscoverAsync(_fileSystem, workingDirectory, CancellationToken.None);
        var totalBytes = discovered.Sum(file => Encoding.UTF8.GetByteCount(file.Content));

        totalBytes.Should().BeLessThanOrEqualTo(16 * 1024);
    }
}
