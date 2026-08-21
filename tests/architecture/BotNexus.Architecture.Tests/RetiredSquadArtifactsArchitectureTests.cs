using Shouldly;

namespace BotNexus.Architecture.Tests;

/// <summary>
/// Prevents the retired Squad orchestration package and its repository state from returning.
/// </summary>
public sealed class RetiredSquadArtifactsArchitectureTests : ArchitectureTest
{
    [Fact]
    public void Repository_DoesNotContainRetiredSquadArtifacts()
    {
        var retiredArtifacts = new[]
        {
            ".squad",
            Path.Combine(".copilot", "skills"),
            Path.Combine(".github", "agents", "squad.agent.md"),
            Path.Combine(".github", "prompts", "deliver-spec.prompt.md"),
        };

        var existingArtifacts = retiredArtifacts
            .Where(path => Path.Exists(Repository.Path(path)))
            .ToArray();

        existingArtifacts.ShouldBeEmpty(
            "Squad orchestration was retired; use current repository instructions, skills, and GitHub Issues instead.");
    }
}