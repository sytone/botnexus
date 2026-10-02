using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Api.Controllers;
using BotNexus.Gateway.Sessions;
using BotNexus.Gateway.Streaming;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotNexus.Gateway.Tests;

public sealed class RunCompletionHistoryTests
{
    [Fact]
    public async Task ProcessAndSaveAsync_PersistsAuthoritativeCompletionForColdHistoryReadback()
    {
        using var fixture = new SqliteFixture();
        var store = fixture.CreateStore();
        var session = await store.GetOrCreateAsync(SessionId.From("s-completion-history"), AgentId.From("agent-a"));
        var completion = new RunCompletionSignal(
            "IncompleteWithoutStopReason",
            ["publish", "review"],
            null,
            "Actionable work remained after bounded continuation.",
            null,
            null,
            null,
            2);

        await StreamingSessionHelper.ProcessAndSaveAsync(
            ToAsyncEnumerable([
                new AgentStreamEvent { Type = AgentStreamEventType.RunEnded, Completion = completion }
            ]),
            session,
            store);

        var coldStore = fixture.CreateStore();
        var reloaded = await coldStore.GetAsync(session.SessionId);
        reloaded.ShouldNotBeNull();
        AssertCompletion(reloaded!.RunCompletion, completion);

        var controller = new SessionsController(coldStore);
        var action = await controller.GetHistory(session.SessionId.Value, cancellationToken: CancellationToken.None);
        var response = (action.Result as OkObjectResult)?.Value as SessionHistoryResponse;
        response.ShouldNotBeNull();
        AssertCompletion(response!.Completion, completion);
    }

    [Fact]
    public async Task ProcessAndSaveAsync_LaterCompletedRunReplacesEarlierNonSuccessCompletion()
    {
        var store = new InMemorySessionStore();
        var session = await store.GetOrCreateAsync(SessionId.From("s-completion-replaced"), AgentId.From("agent-a"));
        session.RunCompletion = new RunCompletionSignal(
            "Parked", ["decision"], "UserInput", "Waiting.", "ask_user persisted", "user", "reply", 0);

        await StreamingSessionHelper.ProcessAndSaveAsync(
            ToAsyncEnumerable([
                new AgentStreamEvent
                {
                    Type = AgentStreamEventType.RunEnded,
                    Completion = new RunCompletionSignal("Completed", [], null, null, null, null, null, 0)
                }
            ]),
            session,
            store);

        session.RunCompletion.ShouldNotBeNull();
        session.RunCompletion!.Status.ShouldBe("Completed");
        session.RunCompletion.OpenItemIds.ShouldBeEmpty();
    }

    [Fact]
    public async Task ProcessAndSaveAsync_RunWithoutCompletionClearsEarlierDisposition()
    {
        var store = new InMemorySessionStore();
        var session = await store.GetOrCreateAsync(SessionId.From("s-completion-cleared"), AgentId.From("agent-a"));
        session.RunCompletion = new RunCompletionSignal(
            "IncompleteWithoutStopReason", ["publish"], null, "Work remained.", null, null, null, 2);

        await StreamingSessionHelper.ProcessAndSaveAsync(
            ToAsyncEnumerable([
                new AgentStreamEvent { Type = AgentStreamEventType.RunEnded }
            ]),
            session,
            store);

        session.RunCompletion.ShouldBeNull();
    }

    private static void AssertCompletion(RunCompletionSignal? actual, RunCompletionSignal expected)
    {
        actual.ShouldNotBeNull();
        actual!.Status.ShouldBe(expected.Status);
        actual.OpenItemIds.ShouldBe(expected.OpenItemIds);
        actual.StopReason.ShouldBe(expected.StopReason);
        actual.Detail.ShouldBe(expected.Detail);
        actual.Evidence.ShouldBe(expected.Evidence);
        actual.ContinuationOwner.ShouldBe(expected.ContinuationOwner);
        actual.WakeCondition.ShouldBe(expected.WakeCondition);
        actual.ContinuationAttempts.ShouldBe(expected.ContinuationAttempts);
    }

    private static async IAsyncEnumerable<AgentStreamEvent> ToAsyncEnumerable(IEnumerable<AgentStreamEvent> events)
    {
        foreach (var evt in events)
        {
            yield return evt;
            await Task.Yield();
        }
    }

    private sealed class SqliteFixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "botnexus-run-completion-tests", Guid.NewGuid().ToString("N"));
        private readonly InMemoryConversationStore _conversations = new();

        public SqliteFixture() => Directory.CreateDirectory(_root);

        public SqliteSessionStore CreateStore()
            => new($"Data Source={Path.Combine(_root, "sessions.db")};Pooling=False", NullLogger<SqliteSessionStore>.Instance, conversationStore: _conversations);

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); }
            catch { }
        }
    }
}
