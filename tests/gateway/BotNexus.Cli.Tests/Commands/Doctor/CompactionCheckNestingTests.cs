using BotNexus.Cli.Commands.Doctor;
using BotNexus.Gateway.Configuration;
using Shouldly;

namespace BotNexus.Cli.Tests.Commands.Doctor;

/// <summary>
/// Regression tests for #2764. Both compaction doctor checks read and wrote a ROOT-level
/// <c>compaction</c> block, but the setting is bound at <c>gateway.compaction</c>
/// (<c>GatewaySettingsConfig.Compaction</c>). Because the root lookup was always null the two
/// checks failed in opposite directions: <c>compaction-model-missing</c> reported a correctly
/// configured platform as broken on every run, while <c>compaction-model</c>'s expensive-model
/// guard was structurally incapable of firing - a silent pass that reads exactly like success.
/// <para>
/// #2887 closed the door for good: checks now address configuration only by canonical path through
/// <see cref="ConfigDocument"/>, and a path the typed graph does not bind is an explicit failure
/// rather than a null. The final two tests pin that refusal, which is what makes the whole class of
/// defect unrepresentable rather than merely fixed.
/// </para>
/// </summary>
public sealed class CompactionCheckNestingTests
{
    private const string ModelPath = "gateway.compaction.summarizationModel";

    private const string ConfiguredAtRealPath = """
        {
          "gateway": {
            "compaction": { "summarizationModel": "claude-haiku-4.5" }
          }
        }
        """;

    private static ConfigDocument Parse(string json) => ConfigDocument.Parse(json);

    [Fact]
    public void MissingCheck_IsNotApplicable_WhenModelSetAtBoundGatewayPath()
    {
        var config = Parse(ConfiguredAtRealPath);
        new CompactionModelMissingCheck().IsApplicable(config)
            .ShouldBeFalse("gateway.compaction.summarizationModel is configured, so the missing-model check must not fire");
    }

    [Fact]
    public void MissingCheck_IsApplicable_WhenModelGenuinelyUnset()
    {
        var config = Parse("""{ "gateway": { "listenUrl": "http://0.0.0.0:5005" } }""");
        new CompactionModelMissingCheck().IsApplicable(config).ShouldBeTrue();
    }

    [Fact]
    public void MissingCheck_IsApplicable_WhenGatewayCompactionPresentButModelBlank()
    {
        var config = Parse("""{ "gateway": { "compaction": { "summarizationModel": "  " } } }""");
        new CompactionModelMissingCheck().IsApplicable(config).ShouldBeTrue();
    }

    [Fact]
    public void MissingCheck_IsApplicable_ForInertRootLevelBlock()
    {
        // A root-level compaction block binds to nothing. It must not be mistaken for
        // real configuration - the model is still genuinely unset.
        var config = Parse("""{ "compaction": { "summarizationModel": "claude-haiku-4.5" } }""");
        new CompactionModelMissingCheck().IsApplicable(config).ShouldBeTrue();
    }

    [Fact]
    public void ExpensiveModelCheck_IsApplicable_WhenBoundModelIsExpensive()
    {
        var config = Parse("""{ "gateway": { "compaction": { "summarizationModel": "claude-opus-4.6" } } }""");
        new CompactionModelCheck().IsApplicable(config)
            .ShouldBeTrue("the expensive-model guard must fire when gateway.compaction.summarizationModel is an expensive reasoning model");
    }

    [Fact]
    public void ExpensiveModelCheck_IsNotApplicable_WhenBoundModelIsCheap()
    {
        var config = Parse(ConfiguredAtRealPath);
        new CompactionModelCheck().IsApplicable(config).ShouldBeFalse();
    }

    [Fact]
    public void ExpensiveModelCheck_Apply_WritesBoundPath_AndLeavesNoRootLevelBlock()
    {
        var config = Parse("""{ "gateway": { "compaction": { "summarizationModel": "claude-opus-4.6" } } }""");
        new CompactionModelCheck().Apply(config);

        config.TryGetString(ModelPath, out var model).ShouldBeTrue();
        model.ShouldBe("claude-haiku-4.5");
        config.RootKeys.ShouldNotContain("compaction",
            "Apply must never create a root-level compaction block - nothing binds it");
        new CompactionModelCheck().IsApplicable(config).ShouldBeFalse();
    }

    [Fact]
    public void MissingCheck_Apply_WritesBoundPath_AndLeavesNoRootLevelBlock()
    {
        var config = Parse("""{ "gateway": { "listenUrl": "http://0.0.0.0:5005" } }""");
        new CompactionModelMissingCheck().Apply(config);

        config.TryGetString(ModelPath, out var model).ShouldBeTrue();
        model.ShouldBe("claude-haiku-4.5");
        config.TryGetString("gateway.listenUrl", out var listenUrl).ShouldBeTrue();
        listenUrl.ShouldBe("http://0.0.0.0:5005");
        config.RootKeys.ShouldNotContain("compaction",
            "Apply must never create a root-level compaction block - nothing binds it");
        new CompactionModelMissingCheck().IsApplicable(config).ShouldBeFalse();
    }

    [Fact]
    public void MissingCheck_Apply_CreatesGatewayBlock_WhenAbsentEntirely()
    {
        var config = Parse("{}");
        new CompactionModelMissingCheck().Apply(config);

        config.TryGetString(ModelPath, out var model).ShouldBeTrue();
        model.ShouldBe("claude-haiku-4.5");
        config.RootKeys.ShouldNotContain("compaction");
    }

    /// <summary>
    /// #2887 AC3: an unrecognised path is an EXPLICIT failure, never a null that reads identically
    /// to "not configured". This is the guard that makes the root-level regression structurally
    /// impossible - the wrong traversal cannot even be requested.
    /// </summary>
    [Fact]
    public void CanonicalPath_RejectsPathThatNothingBinds()
    {
        var config = Parse("{}");

        var read = Should.Throw<InvalidOperationException>(
            () => config.TryGetString("compaction.summarizationModel", out _));
        read.Message.ShouldContain("compaction.summarizationModel");

        config.TrySet("compaction.summarizationModel", "claude-haiku-4.5", out var writeError).ShouldBeFalse();
        writeError.ShouldContain("compaction.summarizationModel");

        config.RootKeys.ShouldNotContain("compaction");
    }

    [Fact]
    public void CanonicalPath_AcceptsTheRealCompactionPath()
    {
        var config = Parse("{}");

        config.TrySet(ModelPath, "claude-haiku-4.5", out var error).ShouldBeTrue(error);
        config.TryGetString(ModelPath, out var value).ShouldBeTrue();
        value.ShouldBe("claude-haiku-4.5");
    }
}
