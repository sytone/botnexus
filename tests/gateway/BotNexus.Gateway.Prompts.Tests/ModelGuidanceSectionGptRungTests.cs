using BotNexus.Gateway.Prompts;

namespace BotNexus.Gateway.Prompts.Tests;

/// <summary>
/// The GPT rung of <see cref="ModelGuidanceSection"/> (#3375).
/// </summary>
/// <remarks>
/// <para>
/// These assertions are deliberately about the PROPERTIES the evaluation identified, not about the
/// exact prose. A test that pinned the full sentence would fail on every reword and prove nothing
/// about whether the loophole is still closed; a test that only asserted "non-empty" would pass
/// against three blank-verse rules. So each check names the observable the rule has to carry: the
/// stable id it is declared under, the cross-tool prohibition, the explicit statement that a changed
/// match count is not a new strategy, and a numeric narration threshold.
/// </para>
/// <para>
/// The overlay and mandatory-default invariants (#2433) are re-asserted here from the GPT side
/// because #3375 is the first change that makes the GPT rung non-empty -- which is exactly the
/// change that could accidentally shadow or drop a default rule.
/// </para>
/// </remarks>
public sealed class ModelGuidanceSectionGptRungTests
{
    private static PromptContext ContextFor(string modelId) => new()
    {
        WorkspaceDir = "C:/workspace",
        Extensions = new Dictionary<string, object?> { [ModelGuidanceSection.ModelIdExtensionKey] = modelId }
    };

    private static IReadOnlyList<string> GptLines => ModelGuidanceSection.Create().Build(ContextFor("gpt-5.6-sol"));

    // ---- AC1: non-empty, and every id comes from Rules rather than a literal ----

    [Fact]
    public void Gpt_ReturnsANonEmptyRuleSet()
    {
        ModelGuidanceSection.Gpt().ShouldNotBeEmpty();
    }

    [Fact]
    public void EveryGptRuleId_IsDeclaredInRules()
    {
        var declared = typeof(ModelGuidanceSection)
            .GetNestedType("Rules", System.Reflection.BindingFlags.NonPublic)!
            .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var rule in ModelGuidanceSection.Gpt())
        {
            declared.ShouldContain(rule.Id);
        }
    }

    [Fact]
    public void EveryGptRuleId_IsDistinct()
    {
        var ids = ModelGuidanceSection.Gpt().Select(r => r.Id).ToList();

        ids.Distinct(StringComparer.OrdinalIgnoreCase).Count().ShouldBe(ids.Count);
    }

    // ---- AC2: tool-schema fidelity ----

    [Fact]
    public void Gpt_CarriesAToolSchemaFidelityRule()
    {
        var text = TextOf(ModelGuidanceSection.Rules.ToolSchemaFidelity);

        text.ShouldContain("schema", Case.Insensitive);
        // The observed defect was TRANSFER from a similar tool, not invention, so the prohibition
        // has to name the transfer explicitly.
        text.ShouldContain("similar tool", Case.Insensitive);
    }

    // ---- AC3: retry circuit breaker, with the loophole named ----

    [Fact]
    public void Gpt_CarriesARetryRule_ThatClosesTheChangedMatchCountLoophole()
    {
        var text = TextOf(ModelGuidanceSection.Rules.RetryCircuitBreaker);

        text.ShouldContain("two", Case.Insensitive);
        text.ShouldContain("match count", Case.Insensitive);
        text.ShouldContain("whitespace", Case.Insensitive);
        text.ShouldContain("anchor", Case.Insensitive);
    }

    // ---- AC4: narration threshold is an observable count, not "when it helps" ----

    [Fact]
    public void Gpt_CarriesANarrationRule_ExpressedAsACountNotAJudgement()
    {
        var text = TextOf(ModelGuidanceSection.Rules.NarrationThreshold);

        // A digit or a spelled-out cardinal: the point is that the trigger is countable.
        text.ShouldMatch(@"\b(\d+|one|two|three|five|ten|twenty)\b");
        text.ShouldContain("tool calls", Case.Insensitive);
        text.ShouldNotContain("when it helps", Case.Insensitive);
    }

    // ---- AC5: mandatory default still catches an unrecognised family ----

    [Fact]
    public void UnknownFamily_StillResolvesToTheConservativeDefaultRung()
    {
        var lines = ModelGuidanceSection.Create().Build(ContextFor("some-model-nobody-has-heard-of"));

        lines.ShouldBe(ModelGuidanceSection.Default().Select(r => r.Text!).ToList());
    }

    [Fact]
    public void UnknownFamily_ReceivesNoGptRule()
    {
        var lines = ModelGuidanceSection.Create().Build(ContextFor("some-model-nobody-has-heard-of"));

        foreach (var rule in ModelGuidanceSection.Gpt())
        {
            lines.ShouldNotContain(rule.Text!);
        }
    }

    [Fact]
    public void DefaultRung_RemainsNonEmpty()
    {
        ModelGuidanceSection.Default().ShouldNotBeEmpty();
    }

    // ---- AC6: GPT resolves to default PLUS the new rules, dropping nothing ----

    [Fact]
    public void Gpt_ResolvesToTheDefaultRulesPlusTheGptRules()
    {
        var lines = GptLines;

        foreach (var rule in ModelGuidanceSection.Default())
        {
            lines.ShouldContain(rule.Text!);
        }

        foreach (var rule in ModelGuidanceSection.Gpt())
        {
            lines.ShouldContain(rule.Text!);
        }

        lines.Count.ShouldBe(ModelGuidanceSection.Default().Count + ModelGuidanceSection.Gpt().Count);
    }

    [Fact]
    public void NoGptRule_SilentlyOverlaysADefaultRuleId()
    {
        // Overlaying by id is legal and is how a family supersedes shared intent -- but #3375 adds
        // only NEW rules, so any collision here would be an accidental shadow rather than a choice.
        var defaultIds = ModelGuidanceSection.Default().Select(r => r.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

        ModelGuidanceSection.Gpt().ShouldNotContain(r => defaultIds.Contains(r.Id));
    }

    [Fact]
    public void NoGptRule_RemovesAnInheritedRule()
    {
        ModelGuidanceSection.Gpt().ShouldAllBe(r => r.Text != null);
    }

    // ---- AC7: the other rungs are untouched ----

    [Fact]
    public void ClaudeAndGemini_ReceiveNoGptRule()
    {
        var claude = ModelGuidanceSection.Create().Build(ContextFor("claude-opus-4-20250514"));
        var gemini = ModelGuidanceSection.Create().Build(ContextFor("gemini-2.5-pro"));

        foreach (var rule in ModelGuidanceSection.Gpt())
        {
            claude.ShouldNotContain(rule.Text!);
            gemini.ShouldNotContain(rule.Text!);
        }
    }

    private static string TextOf(string ruleId)
    {
        var rule = ModelGuidanceSection.Gpt().SingleOrDefault(r => string.Equals(r.Id, ruleId, StringComparison.OrdinalIgnoreCase));
        rule.ShouldNotBeNull($"The GPT rung must declare a rule with id '{ruleId}'.");
        rule.Text.ShouldNotBeNullOrWhiteSpace();
        return rule.Text!;
    }
}
