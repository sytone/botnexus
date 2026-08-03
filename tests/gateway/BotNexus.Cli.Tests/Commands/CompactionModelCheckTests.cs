using BotNexus.Cli.Commands.Doctor;
using Shouldly;
using System.Text.Json.Nodes;

namespace BotNexus.Cli.Tests.Commands;

/// <summary>
/// Behaviour of the two compaction doctor checks.
/// <para>
/// <b>#2764 history \u2014 read before changing a fixture.</b> Every fixture in this class originally
/// seeded a ROOT-level <c>compaction</c> block, because both checks read <c>root["compaction"]</c>.
/// The setting actually binds at <c>gateway.compaction</c>, so those reads were permanently null
/// and the tests passed only because production and test agreed on the same wrong shape. The model
/// inputs below are the original regression corpus and are preserved verbatim; only the nesting
/// they are seeded at moved to the path the gateway really binds. Two tests were added pinning that
/// a root-level block is inert, so the old shape can never quietly come back.
/// </para>
/// </summary>
public sealed class CompactionModelCheckTests
{
    private const string ModelPath = "gateway.compaction.summarizationModel";

    private static JsonObject AtBoundPath(string model)
        => JsonNode.Parse($"{{\"gateway\":{{\"compaction\":{{\"summarizationModel\":\"{model}\"}}}}}}")!.AsObject();

    [Theory]
    [InlineData("claude-opus-4.6", true)]
    [InlineData("claude-opus-4-6", true)]
    [InlineData("gpt-5", true)]
    [InlineData("o3", true)]
    [InlineData("claude-haiku-4.5", false)]
    [InlineData("gpt-4.1-mini", false)]
    [InlineData("claude-sonnet-4.6", false)]
    public void CompactionModelCheck_DetectsExpensiveModels(string model, bool shouldFlag)
    {
        var root = AtBoundPath(model);
        var check = new CompactionModelCheck();
        check.IsApplicable(root).ShouldBe(shouldFlag);
    }

    [Fact]
    public void CompactionModelCheck_NotApplicable_WhenNoModelSet()
    {
        var root = JsonNode.Parse("""{"gateway":{"compaction":{}}}""")!.AsObject();
        var check = new CompactionModelCheck();
        check.IsApplicable(root).ShouldBeFalse();
    }

    [Fact]
    public void CompactionModelMissingCheck_Applicable_WhenNoCompactionBlock()
    {
        var root = JsonNode.Parse("""{"gateway":{}}""")!.AsObject();
        var check = new CompactionModelMissingCheck();
        check.IsApplicable(root).ShouldBeTrue();
    }

    [Fact]
    public void CompactionModelMissingCheck_NotApplicable_WhenModelSet()
    {
        var root = AtBoundPath("claude-haiku-4.5");
        var check = new CompactionModelMissingCheck();
        check.IsApplicable(root).ShouldBeFalse();
    }

    [Fact]
    public void CompactionModelCheck_Apply_SetsHaiku()
    {
        var root = AtBoundPath("claude-opus-4.6");
        var check = new CompactionModelCheck();
        check.Apply(root);
        root["gateway"]!["compaction"]!["summarizationModel"]!.GetValue<string>().ShouldBe("claude-haiku-4.5");
    }

    // The two pins below are the #2764 regression itself, stated as behaviour: a root-level
    // compaction block binds to nothing, so neither check may treat it as configuration.

    [Fact]
    public void CompactionModelCheck_IgnoresInertRootLevelBlock()
    {
        var root = JsonNode.Parse("""{"compaction":{"summarizationModel":"claude-opus-4.6"}}""")!.AsObject();

        new CompactionModelCheck().IsApplicable(root)
            .ShouldBeFalse($"a root-level compaction block binds to nothing; only {ModelPath} is real configuration");
    }

    [Fact]
    public void CompactionModelMissingCheck_TreatsInertRootLevelBlockAsUnset()
    {
        var root = JsonNode.Parse("""{"compaction":{"summarizationModel":"claude-haiku-4.5"}}""")!.AsObject();

        new CompactionModelMissingCheck().IsApplicable(root)
            .ShouldBeTrue($"the model is not configured at {ModelPath}, so the check must still fire");
    }
}
