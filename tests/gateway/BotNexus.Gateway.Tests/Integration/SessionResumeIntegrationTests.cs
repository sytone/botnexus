using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BotNexus.Gateway;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Api;
using BotNexus.Gateway.Sessions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.IO.Abstractions;
using BotNexus.Gateway.Tests.Helpers;

namespace BotNexus.Gateway.Tests.Integration;

[Trait("Category", "Integration")]
[Collection("IntegrationTests")]
public sealed class SessionResumeIntegrationTests : IDisposable
{
    private const string TestAgentId = "resume-agent";
    private readonly List<string> _cleanupDirectories = [];

    [Fact]
    public async Task SessionHistory_PersistsAcrossStoreRecreation()
    {
        var storePath = CreateStorePath();
        var firstStore = CreateFileStore(storePath);
        var session = await firstStore.GetOrCreateAsync("persist-1", "agent-alpha");
        var createdAt = session.CreatedAt;
        session.ChannelType = ChannelKey.From("signalr");
        for (var i = 0; i < 5; i++)
            session.AddEntry(new SessionEntry { Role = MessageRole.User, Content = $"entry-{i}" });
        await firstStore.SaveAsync(session);

        var secondStore = CreateFileStore(storePath);
        var reloaded = await secondStore.GetOrCreateAsync("persist-1", "different-agent");

        reloaded.History.Count().ShouldBe(5);
        reloaded.AgentId.Value.ShouldBe("agent-alpha");
        reloaded.ChannelType.ShouldBe(ChannelKey.From("signalr"));
        reloaded.CreatedAt.ShouldBe(createdAt);
    }

    [Fact]
    public async Task ExpiredSession_ReactivatedOnJoin_HistoryPreserved()
    {
        var storePath = CreateStorePath();
        {
            var seedStore = CreateFileStore(storePath);
            {
                var session = await seedStore.GetOrCreateAsync("expired-join", TestAgentId);
                session.AddEntry(new SessionEntry { Role = MessageRole.User, Content = "persisted-message" });
                session.Status = SessionStatus.Expired;
                session.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-2);
                await seedStore.SaveAsync(session);
            }
        }

        await using var factory = CreateTestFactory(storePath);
        using var cts = CreateTimeout();
        await RegisterAgentAsync(factory, cts.Token);
        await using var connection = await CreateStartedConnection(factory, cts.Token);

        var result = await connection.InvokeAsync<JsonElement>("JoinSession", TestAgentId, "expired-join", cts.Token);
        var store = factory.Services.GetRequiredService<ISessionStore>();
        var reloaded = await store.GetAsync("expired-join", cts.Token);

        result.GetProperty("isResumed").GetBoolean().ShouldBeTrue();
        result.GetProperty("status").GetString().ShouldBe(SessionStatus.Active.ToString());
        reloaded.ShouldNotBeNull();
        reloaded!.Status.ShouldBe(SessionStatus.Active);
        reloaded.ExpiresAt.ShouldBeNull();
        reloaded.History.Where(e => e.Content == "persisted-message").ShouldHaveSingleItem();
    }

    [Fact]
    public async Task ResetSession_ArchivesOldSession_NewSessionIsEmpty()
    {
        var storePath = CreateStorePath();
        var sessionId = "reset-session";
        var encodedSessionId = Uri.EscapeDataString(sessionId);
        await using var factory = CreateTestFactory(storePath);
        using var cts = CreateTimeout();
        await RegisterAgentAsync(factory, cts.Token);

        var store = factory.Services.GetRequiredService<ISessionStore>();
        var seeded = await store.GetOrCreateAsync(sessionId, TestAgentId, cts.Token);
        for (var i = 0; i < 10; i++)
            seeded.AddEntry(new SessionEntry { Role = MessageRole.User, Content = $"old-{i}" });
        await store.SaveAsync(seeded, cts.Token);

        await using var connection = await CreateStartedConnection(factory, cts.Token);
        await connection.InvokeAsync("ResetSession", TestAgentId, sessionId, cts.Token);
        var newSession = await store.GetOrCreateAsync(sessionId, TestAgentId, cts.Token);

        newSession.History.ShouldBeEmpty();
        Directory.GetFiles(storePath, $"{encodedSessionId}.jsonl.archived.*").ShouldHaveSingleItem();
        Directory.GetFiles(storePath, $"{encodedSessionId}.meta.json.archived.*").ShouldHaveSingleItem();
    }

    [Fact]
    public async Task MultipleResets_CreateMultipleArchives()
    {
        var storePath = CreateStorePath();
        var store = CreateFileStore(storePath);
        var sessionId = "multi-reset";
        var encodedSessionId = Uri.EscapeDataString(sessionId);

        var first = await store.GetOrCreateAsync(sessionId, "agent-a");
        first.AddEntry(new SessionEntry { Role = MessageRole.User, Content = "first-history" });
        await store.SaveAsync(first);
        await store.ArchiveAsync(sessionId);

        await Task.Delay(1100);

        var second = await store.GetOrCreateAsync(sessionId, "agent-a");
        second.AddEntry(new SessionEntry { Role = MessageRole.Assistant, Content = "second-history" });
        await store.SaveAsync(second);
        await store.ArchiveAsync(sessionId);

        var active = await store.GetOrCreateAsync(sessionId, "agent-a");
        active.History.ShouldBeEmpty();
        Directory.GetFiles(storePath, $"{encodedSessionId}.jsonl.archived.*").Count().ShouldBe(2);
        Directory.GetFiles(storePath, $"{encodedSessionId}.meta.json.archived.*").Count().ShouldBe(2);
    }

    [Fact]
    public async Task ConcurrentSessions_IndependentLifecycles()
    {
        var storePath = CreateStorePath();
        {
            var firstStore = CreateFileStore(storePath);
            var alpha = await firstStore.GetOrCreateAsync("session-a", "alpha");
            var beta = await firstStore.GetOrCreateAsync("session-b", "beta");
            for (var i = 0; i < 3; i++)
                alpha.AddEntry(new SessionEntry { Role = MessageRole.User, Content = $"a-{i}" });
            for (var i = 0; i < 5; i++)
                beta.AddEntry(new SessionEntry { Role = MessageRole.Assistant, Content = $"b-{i}" });
            await firstStore.SaveAsync(alpha);
            await firstStore.SaveAsync(beta);
        }

        var secondStore = CreateFileStore(storePath);
        var reloadedA = await secondStore.GetAsync("session-a");
        var reloadedB = await secondStore.GetAsync("session-b");
        await secondStore.ArchiveAsync("session-a");
        var unaffectedB = await secondStore.GetAsync("session-b");

        reloadedA!.History.Count().ShouldBe(3);
        reloadedB!.History.Count().ShouldBe(5);
        unaffectedB!.History.Count().ShouldBe(5);
        unaffectedB.AgentId.Value.ShouldBe("beta");
    }

    [Fact]
    public async Task SessionCleanup_PreservesActiveSessionsAfterRestart()
    {
        var storePath = CreateStorePath();
        {
            var firstStore = CreateFileStore(storePath);
            var active = await firstStore.GetOrCreateAsync("active-1", "agent-a");
            active.Status = SessionStatus.Active;
            active.AddEntry(new SessionEntry { Role = MessageRole.User, Content = "active-history" });
            await firstStore.SaveAsync(active);

            var expired = await firstStore.GetOrCreateAsync("expired-1", "agent-b");
            expired.Status = SessionStatus.Expired;
            expired.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-10);
            expired.AddEntry(new SessionEntry { Role = MessageRole.Assistant, Content = "expired-history" });
            await firstStore.SaveAsync(expired);

            var closed = await firstStore.GetOrCreateAsync("closed-1", "agent-c");
            closed.Status = SessionStatus.Sealed;
            closed.AddEntry(new SessionEntry { Role = MessageRole.Tool, Content = "closed-history" });
            await firstStore.SaveAsync(closed);
        }

        var secondStore = CreateFileStore(storePath);
        var sessions = await secondStore.ListAsync();
        var activeReloaded = sessions.Single(s => s.SessionId == "active-1");
        var expiredReloaded = sessions.Single(s => s.SessionId == "expired-1");
        var closedReloaded = sessions.Single(s => s.SessionId == "closed-1");

        sessions.Count().ShouldBe(3);
        activeReloaded.Status.ShouldBe(SessionStatus.Active);
        expiredReloaded.Status.ShouldBe(SessionStatus.Expired);
        closedReloaded.Status.ShouldBe(SessionStatus.Sealed);
        activeReloaded.History.Where(e => e.Content == "active-history").ShouldHaveSingleItem();
    }

    [Fact]
    public async Task LargeSessionHistory_RoundTripsCorrectly()
    {
        var storePath = CreateStorePath();
        {
            var firstStore = CreateFileStore(storePath);
            var session = await firstStore.GetOrCreateAsync("large-history", "agent-large");
            for (var i = 0; i < 500; i++)
            {
                var role = (i % 4) switch
                {
                    0 => "user",
                    1 => "assistant",
                    2 => "tool",
                    _ => "system"
                };
                session.History.Add(new SessionEntry
                {
                    Role = role,
                    Content = $"content-{i}",
                    Timestamp = DateTimeOffset.UtcNow.AddSeconds(-i),
                    ToolName = role == "tool" ? $"tool-{i % 5}" : null,
                    ToolCallId = role == "tool" ? $"call-{i}" : null
                });
            }
            await firstStore.SaveAsync(session);
        }

        var secondStore = CreateFileStore(storePath);
        var reloaded = await secondStore.GetAsync("large-history");

        reloaded.ShouldNotBeNull();
        reloaded!.History.Count().ShouldBe(500);
        reloaded.History[2].Role.ShouldBe(MessageRole.Tool);
        reloaded.History[2].ToolName.ShouldBe("tool-2");
        reloaded.History[2].ToolCallId.ShouldBe("call-2");
        reloaded.History[499].Content.ShouldBe("content-499");
    }

    [Fact]
    public async Task ArchiveAsync_ThenDelete_FullLifecycle()
    {
        var storePath = CreateStorePath();
        var sessionId = "archive-delete";
        var encodedSessionId = Uri.EscapeDataString(sessionId);
        var store = CreateFileStore(storePath);
        var session = await store.GetOrCreateAsync(sessionId, "agent-a");
        session.AddEntry(new SessionEntry { Role = MessageRole.User, Content = "hello" });
        await store.SaveAsync(session);

        await store.ArchiveAsync(sessionId);
        Func<Task> act = async () => await store.DeleteAsync(sessionId);

        await act.ShouldNotThrowAsync();
        File.Exists(Path.Combine(storePath, $"{encodedSessionId}.jsonl")).ShouldBeFalse();
        File.Exists(Path.Combine(storePath, $"{encodedSessionId}.meta.json")).ShouldBeFalse();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task SessionResume_WithCorruptedJsonlLine_SkipsCorruptEntry()
    {
        var storePath = CreateStorePath();
        var sessionId = "corrupt-line";
        var encoded = Uri.EscapeDataString(sessionId);
        var historyPath = Path.Combine(storePath, $"{encoded}.jsonl");
        var metaPath = Path.Combine(storePath, $"{encoded}.meta.json");
        var createdAt = DateTimeOffset.UtcNow.AddMinutes(-15);
        var updatedAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        await File.WriteAllTextAsync(metaPath, $$"""
            {"agentId":"agent-a","channelType":"signalr","callerId":"caller-1","createdAt":"{{createdAt:O}}","updatedAt":"{{updatedAt:O}}","status":0,"expiresAt":null,"nextSequenceId":1,"streamEvents":[]}
            """);
        await File.WriteAllLinesAsync(historyPath,
        [
            """{"role":"user","content":"valid-1","timestamp":"2025-01-01T00:00:00.0000000+00:00"}""",
            """{ definitely-not-json }""",
            """{"role":"assistant","content":"valid-2","timestamp":"2025-01-01T00:00:01.0000000+00:00"}"""
        ]);

        var store = CreateFileStore(storePath);
        var loaded = await store.GetAsync(sessionId);

        loaded.ShouldNotBeNull();
        loaded!.History.Count().ShouldBe(2);
        loaded.History.Select(h => h.Content).ToList().ShouldBe(new[] { "valid-1", "valid-2" });
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task SessionResume_EmptyJsonlFile_ReturnsSessionWithNoHistory()
    {
        var storePath = CreateStorePath();
        var sessionId = "empty-history";
        var encoded = Uri.EscapeDataString(sessionId);
        var historyPath = Path.Combine(storePath, $"{encoded}.jsonl");
        var metaPath = Path.Combine(storePath, $"{encoded}.meta.json");
        var createdAt = DateTimeOffset.UtcNow.AddMinutes(-30);
        var updatedAt = DateTimeOffset.UtcNow.AddMinutes(-10);
        await File.WriteAllTextAsync(metaPath, $$"""
            {"agentId":"agent-a","channelType":"signalr","callerId":"caller-1","createdAt":"{{createdAt:O}}","updatedAt":"{{updatedAt:O}}","status":2,"expiresAt":"{{DateTimeOffset.UtcNow.AddMinutes(-1):O}}","nextSequenceId":1,"streamEvents":[]}
            """);
        await File.WriteAllTextAsync(historyPath, string.Empty);

        var store = CreateFileStore(storePath);
        var loaded = await store.GetAsync(sessionId);

        loaded.ShouldNotBeNull();
        loaded!.History.ShouldBeEmpty();
        loaded.AgentId.Value.ShouldBe("agent-a");
        loaded.ChannelType.ShouldBe(ChannelKey.From("signalr"));
        loaded.CreatedAt.ShouldBe(createdAt);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task SessionResume_MissingMetaFile_ReturnsNull()
    {
        var storePath = CreateStorePath();
        var sessionId = "missing-meta";
        var encoded = Uri.EscapeDataString(sessionId);
        await File.WriteAllTextAsync(Path.Combine(storePath, $"{encoded}.jsonl"), """{"role":"user","content":"orphan"}""");

        var store = CreateFileStore(storePath);
        var loaded = await store.GetAsync(sessionId);

        loaded.ShouldBeNull();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task SessionResume_SpecialCharactersInSessionId_HandledCorrectly()
    {
        var storePath = CreateStorePath();
        const string sessionId = "agent:nova:main/workspace";
        var encoded = Uri.EscapeDataString(sessionId);
        var store = CreateFileStore(storePath);
        var session = await store.GetOrCreateAsync(sessionId, "agent-nova");
        session.AddEntry(new SessionEntry { Role = MessageRole.User, Content = "hello 🌍" });
        await store.SaveAsync(session);

        var reloaded = await CreateFileStore(storePath).GetAsync(sessionId);

        File.Exists(Path.Combine(storePath, $"{encoded}.jsonl")).ShouldBeTrue();
        File.Exists(Path.Combine(storePath, $"{encoded}.meta.json")).ShouldBeTrue();
        reloaded.ShouldNotBeNull();
        reloaded!.SessionId.Value.ShouldBe(sessionId);
        reloaded.History.Where(e => e.Content == "hello 🌍").ShouldHaveSingleItem();
    }

    public void Dispose()
    {
        foreach (var directory in _cleanupDirectories.Where(Directory.Exists))
            Directory.Delete(directory, recursive: true);
    }

    private string CreateStorePath()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "SessionResumeIntegrationTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        _cleanupDirectories.Add(path);
        return path;
    }

    private static FileSessionStore CreateFileStore(string storePath)
        => new(storePath, NullLogger<FileSessionStore>.Instance, new FileSystem());

    private static WebApplicationFactory<Program> CreateTestFactory(string storePath)
        => new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Development");
                builder.UseUrls("http://127.0.0.1:0");
                builder.ConfigureServices(services =>
                {
                    var hostedServices = services
                        .Where(d => d.ServiceType == typeof(IHostedService))
                        .ToList();
                    foreach (var descriptor in hostedServices)
                        services.Remove(descriptor);

                    services.AddSignalRChannelForTests();

                    services.RemoveAll<ISessionStore>();
                    services.AddSingleton<ISessionStore>(sp =>
                        new FileSessionStore(
                            storePath,
                            sp.GetRequiredService<ILogger<FileSessionStore>>(),
                            new FileSystem()));
                });
            });

    private static HubConnection CreateHubConnection(WebApplicationFactory<Program> factory)
    {
        var server = factory.Server;
        var handler = server.CreateHandler();
        return new HubConnectionBuilder()
            .WithUrl("http://localhost/hub/gateway", options =>
            {
                options.HttpMessageHandlerFactory = _ => handler;
                options.Transports = HttpTransportType.LongPolling;
            })
            .Build();
    }

    private static async Task<HubConnection> CreateStartedConnection(WebApplicationFactory<Program> factory, CancellationToken cancellationToken)
    {
        var connection = CreateHubConnection(factory);
        await connection.StartAsync(cancellationToken);
        return connection;
    }

    private static async Task RegisterAgentAsync(WebApplicationFactory<Program> factory, CancellationToken cancellationToken)
    {
        using var client = factory.CreateClient();
        var descriptor = new AgentDescriptor
        {
            AgentId = TestAgentId,
            DisplayName = "Session Resume Test Agent",
            ModelId = "gpt-4.1",
            ApiProvider = "copilot",
            IsolationStrategy = "in-process"
        };

        var response = await client.PostAsJsonAsync("/api/agents", descriptor, cancellationToken);
        response.StatusCode.ShouldBeOneOf(HttpStatusCode.Created, HttpStatusCode.Conflict);
    }

    private static CancellationTokenSource CreateTimeout()
        => new(TimeSpan.FromSeconds(20));
}





