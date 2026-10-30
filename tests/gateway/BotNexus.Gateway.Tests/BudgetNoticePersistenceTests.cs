using System.Text.Json;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Api.Controllers;
using BotNexus.Gateway.Sessions;
using BotNexus.Gateway.Streaming;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotNexus.Gateway.Tests;

public sealed class BudgetNoticePersistenceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BudgetStop_PersistsOneHostNoticeWithOrWithoutSummaryAndSurvivesColdHistory(bool summary)
    {
        var root = Path.Combine(Path.GetTempPath(), "budget-notice-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var connection = $"Data Source={Path.Combine(root, "sessions.db")};Pooling=False";
            var conversations = new InMemoryConversationStore();
            var store = new SqliteSessionStore(connection, NullLogger<SqliteSessionStore>.Instance, conversationStore: conversations);
            var session = await store.GetOrCreateAsync(SessionId.From("budget-session"), AgentId.From("agent-budget"));
            var completion = JsonSerializer.Deserialize<RunCompletionSignal>("""
                {"Status":"Parked","OpenItemIds":["unfinished"],"StopReason":"ToolResultBudget","ContinuationAttempts":0,
                 "ToolResultBudget":{"Limit":128,"ExecutedResults":130,"Overshoot":2}}
                """).ShouldNotBeNull();
            var end = new AgentStreamEvent { Type = AgentStreamEventType.RunEnded, AgentRunId = AgentRunId.From("budget-run"), Completion = completion };
            var events = new List<AgentStreamEvent>();
            if (summary)
                events.Add(new AgentStreamEvent { Type = AgentStreamEventType.ContentDelta, AgentRunId = end.AgentRunId, ContentDelta = "Optional model summary" });
            events.Add(end);
            events.Add(end);
            await StreamingSessionHelper.ProcessAndSaveAsync(Stream(events), session, store);
            // A replay of the same terminal run must not add another persisted notice either.
            await StreamingSessionHelper.ProcessAndSaveAsync(Stream([end]), session, store);

            var cold = new SqliteSessionStore(connection, NullLogger<SqliteSessionStore>.Instance, conversationStore: conversations);
            var reloaded = (await cold.GetAsync(session.SessionId)).ShouldNotBeNull();
            var notice = reloaded.GetHistorySnapshot().Where(row => row.ResolveKind().Value == "tool-result-budget").ShouldHaveSingleItem();
            notice.AgentRunId.ShouldBe(end.AgentRunId);
            notice.Role.ShouldBe(MessageRole.System);
            notice.Content.ShouldContain("incomplete");
            notice.Content.ShouldContain("parked");
            notice.Content.ShouldContain("new direction");
            notice.Content.ShouldContain("Limit: 128");
            notice.Content.ShouldContain("Executed results: 130");
            notice.Content.ShouldContain("Overshoot: 2");
            reloaded.GetHistorySnapshot().Any(row => row.Content == "Optional model summary").ShouldBe(summary);
            var response = await new SessionsController(cold).GetHistory(session.SessionId.Value, cancellationToken: CancellationToken.None);
            var history = response.Result.ShouldBeOfType<OkObjectResult>().Value.ShouldBeOfType<SessionHistoryResponse>();
            history.Entries.Count(row => row.ResolveKind().Value == "tool-result-budget").ShouldBe(1);
            JsonSerializer.Serialize(history.Completion).ShouldContain("\"ExecutedResults\":130");
            var projected = ConversationHistoryProjection.Project([reloaded])
                .Where(row => row.MessageKind == "tool-result-budget").ShouldHaveSingleItem();
            JsonSerializer.Serialize(projected).ShouldContain("\"AgentRunId\":\"budget-run\"");
            projected.Content.ShouldBe(notice.Content);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("Completed", null)]
    [InlineData("Parked", "SemanticNonProgress")]
    public async Task NonBudgetCompletion_DoesNotPersistBudgetNotice(string status, string? reason)
    {
        var store = new InMemorySessionStore();
        var session = await store.GetOrCreateAsync(SessionId.From("normal-session"), AgentId.From("agent-budget"));
        await StreamingSessionHelper.ProcessAndSaveAsync(Stream([new AgentStreamEvent {
            Type = AgentStreamEventType.RunEnded, Completion = new RunCompletionSignal(status, [], reason, null, null, null, null, 0)
        }]), session, store);
        session.GetHistorySnapshot().ShouldBeEmpty();
    }

    private static async IAsyncEnumerable<AgentStreamEvent> Stream(IEnumerable<AgentStreamEvent> events)
    {
        foreach (var evt in events)
        {
            yield return evt;
            await Task.Yield();
        }
    }
}
