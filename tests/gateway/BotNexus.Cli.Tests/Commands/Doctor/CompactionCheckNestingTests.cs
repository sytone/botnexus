using System.Text.Json.Nodes;
using BotNexus.Cli.Commands.Doctor;
using Shouldly;

namespace BotNexus.Cli.Tests.Commands.Doctor;

/// <summary>
/// Regression tests for #2764. Both compaction doctor checks read and wrote a ROOT-level
/// <c>compaction</c> block, but the setting is bound at <c>gateway.compaction</c>
/// (<c>GatewaySettingsConfig.Compaction</c>). Because the root lookup was always null the two
/// checks failed in opposite directions: <c>compaction-model-missing</c> reported a correctly
/// configured platform as broken on every run, while <c>compaction-model</c>'s expensive-model
/// guard was structurally incapable of firing - a silent pass that reads exactly like success.
/// The fix routes both checks through the bound configuration path, so a path that nothing
/// binds cannot be read from or written to.
/// </summary>
public sealed class CompactionCheckNestingTests
{
    private const string ConfiguredAtRealPath = """
        {
          "gateway": {
            "compaction": { "summarizationModel": "claude-haiku-4.5" }
          }
        }
        """;

    private static JsonObject Parse(string json) => JsonNode.Parse(json)!.AsObject();

    [Fact]
    public void MissingCheck_IsNotApplicable_WhenModelSetAtBoundGatewayPath()
    {
        var root = Parse(ConfiguredAtRealPath);

        new CompactionModelMissingCheck().IsApplicable(root)
            .ShouldBeFalse("gateway.compaction.summarizationModel is configured, so the missing-model check must not fire");
    }

    [Fact]
    public void MissingCheck_IsApplicable_WhenModelGenuinelyUnset()
    {
        var root = Parse("""{ "gateway": { "listenUrl": "http://0.0.0.0:5005" } }""");

        new CompactionModelMissingCheck().IsApplicable(root).ShouldBeTrue();
    }

    [Fact]
    public void MissingCheck_IsApplicable_WhenGatewayCompactionPresentButModelBlank()
    {
        var root = Parse("""{ "gateway": { "compaction": { "summarizationModel": "  " } } }""");

        new CompactionModelMissingCheck().IsApplicable(root).ShouldBeTrue();
    }

    [Fact]
    public void MissingCheck_IsApplicable_ForInertRootLevelBlock()
    {
        // A root-level compaction block binds to nothing. It must not be mistaken for
        // real configuration - the model is still genuinely unset.
        var root = Parse("""{ "compaction": { "summarizationModel": "claude-haiku-4.5" } }""");

        new CompactionModelMissingCheck().IsApplicable(root).ShouldBeTrue();
    }

    [Fact]
    public void ExpensiveModelCheck_IsApplicable_WhenBoundModelIsExpensive()
    {
        var root = Parse("""{ "gateway": { "compaction": { "summarizationModel": "claude-opus-4.6" } } }""");

        new CompactionModelCheck().IsApplicable(root)
            .ShouldBeTrue("the expensive-model guard must fire when gateway.compaction.summarizationModel is an expensive reasoning model");
    }

    [Fact]
    public void ExpensiveModelCheck_IsNotApplicable_WhenBoundModelIsCheap()
    {
        var root = Parse(ConfiguredAtRealPath);

        new CompactionModelCheck().IsApplicable(root).ShouldBeFalse();
    }

    [Fact]
    public void ExpensiveModelCheck_Apply_WritesBoundPath_AndLeavesNoRootLevelBlock()
    {
        var root = Parse("""{ "gateway": { "compaction": { "summarizationModel": "claude-opus-4.6" } } }""");

        new CompactionModelCheck().Apply(root);

        root["gateway"]!["compaction"]!["summarizationModel"]!.GetValue<string>().ShouldBe("claude-haiku-4.5");
        root.ContainsKey("compaction").ShouldBeFalse("Apply must never create a root-level compaction block - nothing binds it");
        new CompactionModelCheck().IsApplicable(root).ShouldBeFalse();
    }

    [Fact]
    public void MissingCheck_Apply_WritesBoundPath_AndLeavesNoRootLevelBlock()
    {
        var root = Parse("""{ "gateway": { "listenUrl": "http://0.0.0.0:5005" } }""");

        new CompactionModelMissingCheck().Apply(root);

        root["gateway"]!["compaction"]!["summarizationModel"]!.GetValue<string>().ShouldBe("claude-haiku-4.5");
        root.ContainsKey("compaction").ShouldBeFalse("Apply must never create a root-level compaction block - nothing binds it");
        root["gateway"]!["listenUrl"]!.GetValue<string>().ShouldBe("http://0.0.0.0:5005");
        new CompactionModelMissingCheck().IsApplicable(root).ShouldBeFalse();
    }

    [Fact]
    public void MissingCheck_Apply_CreatesGatewayBlock_WhenAbsentEntirely()
    {
        var root = Parse("{}");

        new CompactionModelMissingCheck().Apply(root);

        root["gateway"]!["compaction"]!["summarizationModel"]!.GetValue<string>().ShouldBe("claude-haiku-4.5");
        root.ContainsKey("compaction").ShouldBeFalse();
    }

    [Fact]
    public void BoundConfigPath_RejectsPathThatNothingBinds()
    {
        // The guard that makes the root-level regression structurally impossible: a path
        // absent from the typed PlatformConfig graph cannot be read or written.
        var root = Parse("{}");

        BoundConfigPath.TryReadString(root, "compaction.summarizationModel", out _).ShouldBeFalse();
        Should.Throw<InvalidOperationException>(() =>
            BoundConfigPath.WriteString(root, "compaction.summarizationModel", "claude-haiku-4.5"));
        root.ContainsKey("compaction").ShouldBeFalse();
    }

    [Fact]
    public void BoundConfigPath_AcceptsTheRealCompactionPath()
    {
        var root = Parse("{}");

        BoundConfigPath.WriteString(root, "gateway.compaction.summarizationModel", "claude-haiku-4.5");

        BoundConfigPath.TryReadString(root, "gateway.compaction.summarizationModel", out var value).ShouldBeTrue();
        value.ShouldBe("claude-haiku-4.5");
    }
}
