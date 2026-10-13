using BotNexus.Agent.Core.ExtensionPoints.ToolResults;
using BotNexus.Agent.Core.Types;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Security;
using BotNexus.Gateway.Audit;
using BotNexus.Gateway.Conversations;
using BotNexus.Gateway.Sessions;
using Microsoft.Extensions.Logging.Abstractions;
using static BotNexus.Gateway.Tests.RunCorrelation4796Fixture;
using static BotNexus.Gateway.Tests.RunMeasurement4796Fixture;

namespace BotNexus.Gateway.Tests;

public sealed class AgentRunMeasurement4796Tests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), nameof(AgentRunMeasurement4796Tests), Guid.NewGuid().ToString("N"));
    private readonly InMemoryConversationStore _conversations = new();
    private static SessionId Session => SessionId.From("session-4796");
    private SqliteSessionStore Store() => new($"Data Source={Path.Combine(_directory, "sessions.db")};Pooling=False",
        NullLogger<SqliteSessionStore>.Instance, _conversations);
    public AgentRunMeasurement4796Tests() => Directory.CreateDirectory(_directory);
    private async Task<(SqliteSessionStore Store, ToolAuditWriteAhead Audit)> Initialize()
    {
        var store = Store();
        // Persist the session before the handle's start event writes run evidence.
        await store.SaveAsync(await store.GetOrCreateAsync(Session, AgentId.From("agent-4796")));
        Capability(store);
        return (store, new ToolAuditWriteAhead(store, DefaultToolAuditSink.Instance, new IdentityRedactor(),
            AgentId.From("agent-4796"), Session, NullLogger.Instance));
    }

    [Fact]
    public async Task Handle_AuditSubscriptionPersistsStartBeforeToolAndDistinctTerminalCountsAcrossPromptsAndCancellation()
    {
        var (store, audit) = await Initialize();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelling = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var executions = 0;
        var tool = new ProbeTool(async ct =>
        {
            var execution = Interlocked.Increment(ref executions);
            if (execution == 1)
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(ct);
            }
            else if (execution == 4)
            {
                cancelling.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
            return "private-payload-4796";
        });
        var provider = new ScriptedProvider((call, _) => call switch
        {
            1 => Calls(1), 3 => Calls(1, 2), 5 => Calls(1), _ => TextResponse()
        });
        var (agent, handle) = Create(provider, [tool], audit: audit, evidenceStore: store);
        await using var owned = handle;
        var ends = new List<AgentEndEvent>();
        using var subscription = agent.Subscribe((evt, _) =>
        {
            if (evt is AgentEndEvent end) ends.Add(end);
            return Task.CompletedTask;
        });
        var firstTask = handle.PromptAsync("private-payload-4796 first");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var runningPage = await Query(Store(), Session);
        var running = Rows(runningPage).ShouldHaveSingleItem();
        running.GetProperty("Outcome").GetString().ShouldBe("Running");
        running.GetProperty("CompletedAt").ValueKind.ShouldBe(System.Text.Json.JsonValueKind.Null);
        running.GetProperty("CompletedResultCount").ValueKind.ShouldBe(System.Text.Json.JsonValueKind.Null);
        Summary(runningPage, 0, 0, 0, 1, null, null, null);
        var persistedStart = (await Store().GetAsync(Session)).ShouldNotBeNull().GetHistorySnapshot().ShouldHaveSingleItem();
        persistedStart.Kind.ShouldBe(MessageKind.ToolStart);
        Id(persistedStart).ShouldBe(RunId(running));
        release.TrySetResult();
        var first = await firstTask.WaitAsync(TimeSpan.FromSeconds(10));
        Id(first).ShouldBe(RunId(running));
        var firstPage = await Query(Store(), Session);
        Summary(firstPage, 1, 0, 0, 0, 1, 1, 1);
        var originalTerminal = Rows(firstPage).ShouldHaveSingleItem().GetRawText();
        var firstMetadata = (await store.GetAsync(Session)).ShouldNotBeNull();
        firstMetadata.RunCompletion = first.Completion;
        await store.SaveAsync(firstMetadata);
        var second = await handle.PromptAsync("second");
        Id(second).ShouldNotBe(Id(first));
        var latest = (await store.GetAsync(Session)).ShouldNotBeNull();
        latest.RunCompletion = second.Completion;
        await store.SaveAsync(latest);
        (await Store().GetAsync(Session)).ShouldNotBeNull().RunCompletion.ShouldNotBeNull();
        var secondPage = await Query(Store(), Session);
        Rows(secondPage).Length.ShouldBe(2);
        Rows(secondPage)[0].GetRawText().ShouldBe(originalTerminal, "durable runs cannot be replaced by latest completion metadata");
        Number(Rows(secondPage)[1], "CompletedResultCount").ShouldBe(2);
        Summary(secondPage, 2, 0, 0, 0, 1, 2, 2);
        using var cancellation = new CancellationTokenSource();
        var cancelledTask = handle.PromptAsync("cancel", cancellation.Token);
        await cancelling.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Rows(await Query(Store(), Session)).Length.ShouldBe(3);
        cancellation.Cancel();
        var cancelled = await cancelledTask.WaitAsync(TimeSpan.FromSeconds(10));
        Id(cancelled).ShouldNotBe(Id(first));
        Id(cancelled).ShouldNotBe(Id(second));
        var final = await Query(Store(), Session);
        var rows = Rows(final);
        rows.Length.ShouldBe(3);
        rows.Select(RunId).ShouldBe([Id(first), Id(second), Id(cancelled)]);
        rows.Last().GetProperty("Outcome").GetString().ShouldBe("Cancelled");
        rows.Last().GetProperty("CompletedResultCount").ValueKind.ShouldBe(System.Text.Json.JsonValueKind.Null,
            "interrupted execution is not a complete measured run or a synthetic result");
        rows.Last().GetProperty("CompletedAt").ValueKind.ShouldBe(System.Text.Json.JsonValueKind.String);
        Summary(final, 2, 0, 0, 1, 1, 2, 2);
        ends.Count.ShouldBe(3);
        ends.Select(Id).ShouldBe(rows.Select(RunId));
        var incomplete = (await Store().GetAsync(Session)).ShouldNotBeNull().GetHistorySnapshot()
            .Where(row => row.ToolIsIncomplete).ShouldHaveSingleItem();
        Id(incomplete).ShouldBe(Id(cancelled));
        final.GetRawText().ShouldNotContain("private-payload-4796");
    }

    [Theory]
    [InlineData(false, 6)]
    [InlineData(true, 129)]
    public async Task Handle_ObservedGuardTerminal_DurablyMeasuresActualResultsAndSeparatesSemanticFromFuse(bool fuse, int expected)
    {
        var (store, audit) = await Initialize();
        var batch = fuse ? 3 : 1;
        var provider = new ScriptedProvider((call, _) => call <= 44 ? Calls(call, batch) : TextResponse());
        var tool = new ProbeTool();
        var (_, handle) = Create(provider, [tool], progress: (_, _) => Task.FromResult<ToolProgressDecision?>(
            fuse ? ToolProgressDecision.Progress : ToolProgressDecision.NoProgress("private-payload-4796", "private-payload-4796", "unchanged-read")),
            audit: audit, evidenceStore: store);
        await using var owned = handle;
        var response = await handle.PromptAsync("guard");
        response.Completion.ShouldNotBeNull().Status.ShouldBe("Parked");
        tool.Executions.ShouldBe(expected);
        var page = await Query(Store(), Session);
        var row = Rows(page).ShouldHaveSingleItem();
        RunId(row).ShouldBe(Id(response));
        Number(row, "CompletedResultCount").ShouldBe(expected);
        row.GetProperty("Outcome").GetString().ShouldBe("Parked");
        row.GetProperty("GuardObservations").GetRawText().ShouldBe(Guards(response.Completion).GetRawText());
        Summary(page, 1, fuse ? 0 : 1, fuse ? 1 : 0, 0, expected, expected, expected);
        page.GetRawText().ShouldNotContain("private-payload-4796");
    }

    [Fact]
    public async Task Handle_WarningRecoveredBeforeSuccess_DoesNotBecomeSemanticTerminalInDurableQuery()
    {
        var (store, audit) = await Initialize();
        var provider = new ScriptedProvider((call, _) => call <= 4 ? Calls(call) : TextResponse());
        var classifications = 0;
        var (_, handle) = Create(provider, [new ProbeTool()], progress: (_, _) => Task.FromResult<ToolProgressDecision?>(
            ++classifications <= 3 ? ToolProgressDecision.NoProgress("scope", "evidence", "unchanged-read") : ToolProgressDecision.Progress),
            audit: audit, evidenceStore: store);
        await using var owned = handle;
        await handle.PromptAsync("recover");
        var page = await Query(Store(), Session);
        Summary(page, 1, 0, 0, 0, 4, 4, 4);
        Rows(page).ShouldHaveSingleItem().GetProperty("GuardObservations")[0].GetProperty("Disposition").GetString().ShouldBe("recovered");
    }

    private sealed class IdentityRedactor : ISecretRedactor
    {
        public string Redact(string input) => input;
        public string RedactForExternalDelivery(string input) => input;
    }
    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
