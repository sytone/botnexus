using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Persistence.Seam.Tests.Sessions;

public sealed class SubAgentReceiptSaveSeamTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StaleAggregateSave_AndConsumption_PreserveOneOriginalResultAndReceipt(bool saveFirst)
    {
        using var fixture = new SessionSeamStoreFixture();
        var seeded = await fixture.SeedAsync("receipt-save-seam");
        var consumer = fixture.CreateStore();
        var run = new SubAgentInfo
        {
            SubAgentId = "receipt-run", SpawningToolCallId = "spawn-call",
            ParentSessionId = seeded.Session.SessionId, ParentAgentId = seeded.Session.AgentId.Value,
            ChildSessionId = SessionId.From("receipt-child"), ChildAgentId = "receipt-child",
            ParentConversationId = seeded.ConversationId, Task = "receipt seam",
            Status = SubAgentStatus.Completed, StartedAt = DateTimeOffset.UtcNow,
            CompletedAt = DateTimeOffset.UtcNow
        };
        await consumer.SaveSubAgentSessionAsync(run);
        var result = new SessionEntry
        {
            Role = MessageRole.Tool, Kind = MessageKind.ToolResult, ToolName = "manage_subagent",
            ToolCallId = "wait-call", Content = "original-result"
        };
        // The aggregate actor was loaded before the consumption actor committed anything.
        // Both possible commit orders are explicit; no sleeps or scheduler assumptions.
        seeded.Session.AddEntry(new SessionEntry { Role = MessageRole.Assistant, Content = "late-turn" });
        if (saveFirst) await seeded.Store.SaveAsync(seeded.Session);
        (await consumer.ConsumeSubAgentResultAsync(run.SubAgentId, seeded.Session.SessionId,
            seeded.ConversationId, result)).ShouldBe("original-result");
        if (!saveFirst) await seeded.Store.SaveAsync(seeded.Session);

        var cold = fixture.CreateStore();
        var committed = (await cold.GetAsync(seeded.Session.SessionId)).ShouldNotBeNull();
        committed.GetHistorySnapshot().ShouldContain(e => e.Content == "late-turn");
        committed.GetHistorySnapshot().Count(e => e.Kind == MessageKind.ToolResult
            && e.ToolCallId == "wait-call").ShouldBe(1);
        (await cold.ConsumeSubAgentResultAsync(run.SubAgentId, seeded.Session.SessionId,
            seeded.ConversationId, result)).ShouldBe("original-result");
        (await cold.ConsumeSubAgentResultAsync(run.SubAgentId, seeded.Session.SessionId,
            seeded.ConversationId, result with { ToolCallId = "other-call" })).ShouldBeNull();
        (await cold.FindSubAgentSpawnAsync(seeded.Session.SessionId, "spawn-call"))
            .ShouldNotBeNull().SubAgentId.ShouldBe(run.SubAgentId);
        (await cold.FindSubAgentSpawnAsync(SessionId.From("wrong-parent"), "spawn-call")).ShouldBeNull();
    }
}
