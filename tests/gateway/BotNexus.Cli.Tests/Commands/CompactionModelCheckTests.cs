using BotNexus.Cli.Commands.Doctor;
using Shouldly;
using System.Text.Json.Nodes;

namespace BotNexus.Cli.Tests.Commands;

public sealed class CompactionModelCheckTests
{
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
        var root = JsonNode.Parse($"{{\"compaction\":{{\"summarizationModel\":\"{model}\"}}}}")!.AsObject();
        var check = new CompactionModelCheck();
        check.IsApplicable(root).ShouldBe(shouldFlag);
    }

    [Fact]
    public void CompactionModelCheck_NotApplicable_WhenNoModelSet()
    {
        var root = JsonNode.Parse("""{"compaction":{}}""")!.AsObject();
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
        var root = JsonNode.Parse("""{"compaction":{"summarizationModel":"claude-haiku-4.5"}}""")!.AsObject();
        var check = new CompactionModelMissingCheck();
        check.IsApplicable(root).ShouldBeFalse();
    }

    [Fact]
    public void CompactionModelCheck_Apply_SetsHaiku()
    {
        var root = JsonNode.Parse("""{"compaction":{"summarizationModel":"claude-opus-4.6"}}""")!.AsObject();
        var check = new CompactionModelCheck();
        check.Apply(root);
        root["compaction"]!["summarizationModel"]!.GetValue<string>().ShouldBe("claude-haiku-4.5");
    }
}