namespace BotNexus.Architecture.Tests;

/// <summary>
/// Keeps the Process Tool guide aligned with the agent-owned background-process lifecycle (#3915).
/// </summary>
public sealed class ProcessToolDocumentationArchitectureTests : ArchitectureTest
{
    [Fact]
    public void ProcessToolGuide_QualifiesOwnershipAndTerminationOutcomes()
    {
        var guide = File.ReadAllText(Repository.Path("docs", "extensions", "process-tool.md"));

        guide.ShouldContain("launching agent's tracked background processes");
        guide.ShouldContain("requests termination of the process tree");
        guide.ShouldContain("reports whether termination was confirmed");
        guide.ShouldContain("not proof that the command never ran or that its operating-system process has died");

        guide.ShouldNotContain("List all tracked background processes");
        guide.ShouldNotContain("Terminates a running process and its entire process tree.");
    }
}
