using Shouldly;

namespace BotNexus.Architecture.Tests;

/// <summary>
/// Prevents the retired Squad orchestration package and its repository state from returning.
/// </summary>
public sealed class RetiredSquadArtifactsArchitectureTests
{
    [Fact]
    public void Repository_DoesNotContainRetiredSquadArtifacts()
    {
        var repositoryRoot = FindRepositoryRoot();
        var retiredArtifacts = new[]
        {
            ".squad",
            Path.Combine(".copilot", "skills"),
            Path.Combine(".github", "agents", "squad.agent.md"),
            Path.Combine(".github", "prompts", "deliver-spec.prompt.md"),
        };

        var existingArtifacts = retiredArtifacts
            .Where(path => Path.Exists(Path.Combine(repositoryRoot, path)))
            .ToArray();

        existingArtifacts.ShouldBeEmpty(
            "Squad orchestration was retired; use current repository instructions, skills, and GitHub Issues instead.");
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "Directory.Packages.props")))
            current = current.Parent;

        return current?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate the repository root.");
    }
}