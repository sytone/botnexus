namespace BotNexus.Gateway.Prompts.Tests;

public sealed class ModelGuidanceSectionGpt6Tests
{
    [Theory]
    [InlineData("gpt-6")]
    [InlineData("gpt-6-astra")]
    [InlineData("gpt-6.1-astra")]
    [InlineData("gpt-6-2-codex")]
    [InlineData("COPILOT/GPT-6.1-ASTRA")]
    [InlineData("openai/gpt-6.1-astra:latest")]
    public void Pipeline_Gpt6_IncludesMajorOverlayAndEveryInheritedRule(string modelId)
    {
        var context = new PromptContext
        {
            WorkspaceDir = Path.GetTempPath(),
            Extensions = new Dictionary<string, object?> { [ModelGuidanceSection.ModelIdExtensionKey] = modelId }
        };
        var lines = new PromptPipeline().Add(ModelGuidanceSection.Create()).BuildLines(context);

        foreach (var rule in ModelGuidanceSection.Default().Concat(ModelGuidanceSection.Gpt()).Concat(ModelGuidanceSection.Gpt6()))
        {
            var text = rule.Text ?? throw new InvalidOperationException($"Unexpected removal: {rule.Id}");
            lines.ShouldContain(line => line.Contains(text, StringComparison.Ordinal));
        }
    }

    [Theory]
    [InlineData("gpt-5")]
    [InlineData("gpt-5.6-sol")]
    [InlineData("gpt-7")]
    [InlineData("gpt-60")]
    [InlineData("unknown-model")]
    [InlineData("claude-6")]
    public void Section_OtherModels_DoNotReceiveGpt6Rules(string modelId)
    {
        var lines = PromptVariantRegistry.Shared.Resolve(ModelGuidanceSection.Id, ModelFamilyDetector.GetModelFamily(modelId), modelId);
        foreach (var rule in ModelGuidanceSection.Gpt6())
            lines.ShouldNotContain(rule.Text ?? throw new InvalidOperationException("Unexpected removal"));
    }

    [Fact]
    public void Gpt6_DeclaresAnAdditiveMajorRungWithSixDistinctStableIds()
    {
        var rung = PromptVariantRegistry.Shared.Declarations.Single(d => d.SectionId == ModelGuidanceSection.Id && d.MatchMajorVersion);
        rung.Family.ShouldBe(ModelFamilyDetector.Gpt);
        rung.Version?.Major.ShouldBe(6);
        rung.Replace.ShouldBeFalse();
        var rules = ModelGuidanceSection.Gpt6();
        rules.Count.ShouldBe(6);
        rules.Select(r => r.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count().ShouldBe(rules.Count);
        rules.ShouldAllBe(r => r.Text != null);
        var inheritedIds = ModelGuidanceSection.Default().Concat(ModelGuidanceSection.Gpt()).Select(r => r.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        rules.ShouldNotContain(r => inheritedIds.Contains(r.Id));
        rules.Select(r => r.Id).ShouldBe([
            ModelGuidanceSection.Rules.CompleteAuthorizedTask,
            ModelGuidanceSection.Rules.ClarifyMaterialBlockers,
            ModelGuidanceSection.Rules.ExplainSkillConstraints,
            ModelGuidanceSection.Rules.ResultFirstCommunication,
            ModelGuidanceSection.Rules.BoundedDelegation,
            ModelGuidanceSection.Rules.ProportionateVerification]);
    }

    [Fact]
    public void Gpt6_RulesPreserveAuthorityHierarchyAndRequiredChecks()
    {
        TextOf(ModelGuidanceSection.Rules.CompleteAuthorizedTask).ShouldContain("actual user request");
        TextOf(ModelGuidanceSection.Rules.CompleteAuthorizedTask).ShouldContain("authorized task");
        TextOf(ModelGuidanceSection.Rules.ClarifyMaterialBlockers).ShouldContain("safe, authorized preparatory work");
        TextOf(ModelGuidanceSection.Rules.ClarifyMaterialBlockers).ShouldContain("approval gates");
        TextOf(ModelGuidanceSection.Rules.ClarifyMaterialBlockers).ShouldContain("never infer missing authority");
        TextOf(ModelGuidanceSection.Rules.ExplainSkillConstraints).ShouldContain("exact relevant instruction");
        TextOf(ModelGuidanceSection.Rules.ExplainSkillConstraints).ShouldContain("interpretation");
        TextOf(ModelGuidanceSection.Rules.ExplainSkillConstraints).ShouldContain("instruction hierarchy");
        TextOf(ModelGuidanceSection.Rules.ResultFirstCommunication).ShouldContain("persona");
        TextOf(ModelGuidanceSection.Rules.ResultFirstCommunication).ShouldContain("plain language");
        TextOf(ModelGuidanceSection.Rules.BoundedDelegation).ShouldContain("available delegation tools");
        TextOf(ModelGuidanceSection.Rules.BoundedDelegation).ShouldContain("isolation");
        TextOf(ModelGuidanceSection.Rules.BoundedDelegation).ShouldContain("verify");
        TextOf(ModelGuidanceSection.Rules.ProportionateVerification).ShouldContain("required checks");
        TextOf(ModelGuidanceSection.Rules.ProportionateVerification).ShouldContain("never weaken");
    }

    private static string TextOf(string id) => ModelGuidanceSection.Gpt6().Single(r => r.Id == id).Text
        ?? throw new InvalidOperationException($"Missing text for {id}");
}
