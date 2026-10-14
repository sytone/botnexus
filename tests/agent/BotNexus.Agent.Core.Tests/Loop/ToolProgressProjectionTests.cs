using BotNexus.Agent.Core.ExtensionPoints.ToolResults;
using BotNexus.Agent.Core.Types;
using BotNexus.Agent.Providers.Core.Models;

namespace BotNexus.Agent.Core.Tests.Loop;

public sealed class ToolProgressProjectionTests
{
    private static Task<ToolProgressDecision?> Evaluate(string name, string text)
    {
        var call = new ToolCallContent("call", name, new Dictionary<string, object?> { ["action"] = "list" });
        var result = new ToolResultAgentMessage("call", name,
            new AgentToolResult([new AgentToolContent(AgentToolContentType.Text, text)]));
        return DefaultToolProgressPolicy.EvaluateAsync(new ToolProgressContext(call, result), CancellationToken.None);
    }

    [Fact]
    public async Task SubagentObservationCounters_DoNotHideSemanticChangesOrNestedData()
    {
        var first = (await Evaluate("list_subagents", """{"subAgents":[{"subAgentId":"child","status":"Running","elapsedSeconds":1,"remainingTimeSeconds":99,"turnsUsed":2,"result":{"elapsedSeconds":1}}]}""")).ShouldNotBeNull();
        var tick = (await Evaluate("list_subagents", """{"subAgents":[{"subAgentId":"child","status":"Running","elapsedSeconds":2,"remainingTimeSeconds":98,"turnsUsed":2,"result":{"elapsedSeconds":1}}]}""")).ShouldNotBeNull();
        first.EvidenceIdentity.ShouldBe(tick.EvidenceIdentity);
        var changed = (await Evaluate("list_subagents", """{"subAgents":[{"subAgentId":"child","status":"Running","elapsedSeconds":2,"remainingTimeSeconds":98,"turnsUsed":3,"result":{"elapsedSeconds":1}}]}""")).ShouldNotBeNull();
        changed.EvidenceIdentity.ShouldNotBe(first.EvidenceIdentity);
        var nested = (await Evaluate("list_subagents", """{"subAgents":[{"subAgentId":"child","status":"Running","elapsedSeconds":2,"remainingTimeSeconds":98,"turnsUsed":2,"result":{"elapsedSeconds":2}}]}""")).ShouldNotBeNull();
        nested.EvidenceIdentity.ShouldNotBe(first.EvidenceIdentity);
    }

    [Fact]
    public async Task ActualSubAgentRunDetailShape_ProjectsOnlyObservationTimeCounters()
    {
        // CamelCase shape serialized by SubAgentListTool from SubAgentRunDetail.FromLive.
        const string snapshot = """{"subAgents":[{"subAgentId":"child","parentSessionId":"parent","childSessionId":"session","name":"worker","task":"build","archetype":"Coder","model":"model","status":"Running","startedAt":"2026-10-09T02:00:00Z","completedAt":null,"elapsedSeconds":1,"effectiveMaxTurns":10,"effectiveTimeoutSeconds":100,"remainingTurns":8,"remainingTimeSeconds":99,"turnsUsed":2,"resultSummary":null,"result":null,"completionDelivery":null,"completionDeliveryError":null,"deliveryWarning":null,"worktreeSnapshot":null}]}""";
        var first = (await Evaluate("list_subagents", snapshot)).ShouldNotBeNull();
        var tick = snapshot.Replace("\"elapsedSeconds\":1,", "\"elapsedSeconds\":2,", StringComparison.Ordinal)
            .Replace("\"remainingTimeSeconds\":99", "\"remainingTimeSeconds\":98", StringComparison.Ordinal);
        (await Evaluate("list_subagents", tick)).ShouldNotBeNull().EvidenceIdentity.ShouldBe(first.EvidenceIdentity);
        var changed = tick.Replace("\"turnsUsed\":2", "\"turnsUsed\":3", StringComparison.Ordinal);
        (await Evaluate("list_subagents", changed)).ShouldNotBeNull().EvidenceIdentity.ShouldNotBe(first.EvidenceIdentity);
    }

    [Theory]
    [InlineData("status", "Running", "Completed")]
    [InlineData("turnsUsed", "2", "3")]
    [InlineData("remainingTurns", "8", "7")]
    [InlineData("timestamp", "one", "two")]
    public async Task SubagentSemanticAndUnknownFields_AreRetained(string field, string firstValue, string secondValue)
    {
        var firstText = System.Text.Json.JsonSerializer.Serialize(new { subAgents = new[] { new Dictionary<string, object?> { ["subAgentId"] = "child", ["status"] = "Running", [field] = firstValue } } });
        var secondText = System.Text.Json.JsonSerializer.Serialize(new { subAgents = new[] { new Dictionary<string, object?> { ["subAgentId"] = "child", ["status"] = "Running", [field] = secondValue } } });
        var first = (await Evaluate("list_subagents", firstText)).ShouldNotBeNull();
        var second = (await Evaluate("list_subagents", secondText)).ShouldNotBeNull();
        first.EvidenceIdentity.ShouldNotBe(second.EvidenceIdentity);
    }

    [Theory]
    [InlineData("{\"elapsedSeconds\":1}", "{\"elapsedSeconds\":2}")]
    [InlineData("{\"subAgents\":[{\"status\":\"Running\",\"elapsedSeconds\":1}]}", "{\"subAgents\":[{\"status\":\"Running\",\"elapsedSeconds\":2}]}")]
    [InlineData("{\"subAgents\":[{\"subAgentId\":\"child\",\"status\":\"Running\",\"elapsedSeconds\":\"one\"}]}", "{\"subAgents\":[{\"subAgentId\":\"child\",\"status\":\"Running\",\"elapsedSeconds\":\"two\"}]}")]
    [InlineData("not json one", "not json two")]
    public async Task UnsupportedSubagentShapes_DoNotStripCounters(string firstText, string secondText)
    {
        var first = (await Evaluate("list_subagents", firstText)).ShouldNotBeNull();
        var second = (await Evaluate("list_subagents", secondText)).ShouldNotBeNull();
        first.EvidenceIdentity.ShouldNotBe(second.EvidenceIdentity);
    }

    [Fact]
    public async Task ProducerTodoText_RetainsTaskStatusTextAndIdentity()
    {
        var first = (await Evaluate("todo", "[ ] Build core (id=one)")).ShouldNotBeNull();
        foreach (var text in new[] { "[x] Build core (id=one)", "[ ] Build tests (id=one)", "[ ] Build core (id=two)" })
            (await Evaluate("todo", text)).ShouldNotBeNull().EvidenceIdentity.ShouldNotBe(first.EvidenceIdentity);
    }

    [Theory]
    [InlineData("write")]
    [InlineData("shell")]
    [InlineData("opaque_probe")]
    public async Task ArbitraryWriteOrUnknownReceipt_IsNeutralRegardlessOfRepetition(string name)
    {
        for (var i = 0; i < 8; i++)
        {
            var decision = await Evaluate(name, "same success receipt");
            (decision?.Outcome ?? ToolProgressOutcome.Neutral).ShouldBe(ToolProgressOutcome.Neutral);
        }
    }

    [Fact]
    public async Task UnsupportedReceiptShape_PreservesEveryField()
    {
        var first = (await Evaluate("todo", """{"receipt":"one","data":{"timestamp":1}}""")).ShouldNotBeNull();
        var second = (await Evaluate("todo", """{"receipt":"two","data":{"timestamp":1}}""")).ShouldNotBeNull();
        first.EvidenceIdentity.ShouldNotBe(second.EvidenceIdentity);
    }
}
