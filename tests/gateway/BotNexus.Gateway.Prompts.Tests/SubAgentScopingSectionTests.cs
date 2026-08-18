using BotNexus.Gateway.Prompts;

namespace BotNexus.Gateway.Prompts.Tests;

/// <summary>
/// Covers the sub-agent scoping section (#2444): its identity, its conditional emission, and the
/// individual clauses whose loss would silently return the prompt to the unscoped-dispatch state
/// that produced an hour of uncommitted work.
/// </summary>
public sealed class SubAgentScopingSectionTests
{
    private static PromptContext ContextWithNoTools => new() { WorkspaceDir = "C:/workspace" };

    private static PromptContext ContextWithSpawnTool => new()
    {
        WorkspaceDir = "C:/workspace",
        AvailableTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "spawn_subagent", "read", "write" }
    };

    private static PromptContext ContextWithoutSpawnTool => new()
    {
        WorkspaceDir = "C:/workspace",
        AvailableTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "read", "write", "shell", "skills" }
    };

    private static IReadOnlyList<string> BuiltLines => SubAgentScopingSection.Create().Build(ContextWithSpawnTool);

    private static string BuiltText => string.Join("\n", BuiltLines);

    [Fact]
    public void SectionId_IsSubAgentScoping()
    {
        SubAgentScopingSection.Id.ShouldBe("subagent-scoping");
    }

    [Fact]
    public void Create_SectionCarriesIdOrderAndXmlTag()
    {
        var section = SubAgentScopingSection.Create();

        section.ShouldBeOfType<LambdaPromptSection>();
        section.SectionId.ShouldBe(SubAgentScopingSection.Id);
        section.Order.ShouldBe(SubAgentScopingSection.SectionOrder);
        section.XmlTag.ShouldBe("subagent_scoping");
    }

    [Fact]
    public void OrderIsAfterToolEnforcementAndSkillsGuidance()
    {
        SubAgentScopingSection.SectionOrder.ShouldBeGreaterThan(ToolEnforcementSection.SectionOrder);
        SubAgentScopingSection.SectionOrder.ShouldBeGreaterThan(SkillsGuidanceSection.SectionOrder);
    }

    // ---- conditional emission (happy / sad) ----

    [Fact]
    public void ShouldInclude_WhenSpawnToolAvailable_ReturnsTrue()
    {
        SubAgentScopingSection.Create().ShouldInclude(ContextWithSpawnTool).ShouldBeTrue();
    }

    [Fact]
    public void ShouldInclude_WhenSpawnToolAbsent_ReturnsFalse()
    {
        // An agent with no dispatch capability must not pay tokens for advice it cannot act on.
        SubAgentScopingSection.Create().ShouldInclude(ContextWithoutSpawnTool).ShouldBeFalse();
    }

    [Fact]
    public void ShouldInclude_WhenNoToolsAtAll_ReturnsFalse()
    {
        SubAgentScopingSection.Create().ShouldInclude(ContextWithNoTools).ShouldBeFalse();
    }

    // ---- content clauses ----

    [Fact]
    public void Build_ReturnsMultipleNonBlankLines()
    {
        BuiltLines.Count.ShouldBeGreaterThan(5);
        BuiltLines.ShouldAllBe(static line => !string.IsNullOrWhiteSpace(line));
    }

    [Fact]
    public void Build_StatesTheTotalContextLossConsequence()
    {
        BuiltText.ShouldContain("timeoutSeconds");
        BuiltLines.ShouldContain(l =>
            l.Contains("ENTIRE accumulated context", StringComparison.Ordinal)
            && l.Contains("commits nothing", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Build_SetsTheDefaultBudgetTo1500NotTheMaximum()
    {
        // The whole point of the clause is that 3600 is a CEILING, not a default. A line that named
        // only 1500 would leave the maximum looking like a reasonable starting choice.
        BuiltLines.ShouldContain(l =>
            l.Contains("1500", StringComparison.Ordinal) && l.Contains("3600", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_ForbidsBundlingAllFiveStagesIntoOneDispatch()
    {
        var bundling = BuiltLines
            .Where(static l => l.Contains("Never bundle", StringComparison.Ordinal))
            .ToList()
            .ShouldHaveSingleItem();

        bundling.ShouldContain("implement");
        bundling.ShouldContain("build");
        bundling.ShouldContain("test");
        bundling.ShouldContain("visual evidence");
        bundling.ShouldContain("docs");
    }

    [Fact]
    public void Build_RequiresMeasuringBeforeScoping()
    {
        BuiltLines.ShouldContain(l =>
            l.Contains("Measure before scoping", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_RequiresStatingAlreadyDoneWorkWithRealCounts()
    {
        BuiltLines.ShouldContain(l =>
            l.Contains("ALREADY DONE", StringComparison.Ordinal)
            && l.Contains("real counts", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Build_RequiresCommittingBetweenStages()
    {
        BuiltLines.ShouldContain(l =>
            l.Contains("Commit between stages", StringComparison.Ordinal)
            && l.Contains("snapshot", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Build_TellsWorkersToFailFastOnInfrastructure()
    {
        BuiltLines.ShouldContain(l =>
            l.Contains("fail fast", StringComparison.OrdinalIgnoreCase)
            && l.Contains("STOP EARLY", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_RequiresSequentialDispatchWhenWorkersShareAWorktree()
    {
        BuiltLines.ShouldContain(l =>
            l.Contains("sequentially", StringComparison.OrdinalIgnoreCase)
            && l.Contains("worktree", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Build_ForbidsWeakeningAnAssertionToGoGreen()
    {
        BuiltLines.ShouldContain(l =>
            l.Contains("assertion", StringComparison.OrdinalIgnoreCase)
            && l.Contains("weaken", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Build_DoesNotRecommendTheThreeThousandSixHundredSecondCeilingAsADefault()
    {
        // Sad path on the content itself: no line may present 3600 as the value to use.
        BuiltLines.ShouldNotContain(l =>
            l.Contains("3600", StringComparison.Ordinal)
            && l.Contains("default `timeoutSeconds` is 3600", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Build_IsDeterministic()
    {
        var section = SubAgentScopingSection.Create();

        section.Build(ContextWithSpawnTool).ShouldBe(section.Build(ContextWithSpawnTool));
    }
}
