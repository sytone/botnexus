using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Api.Controllers;
using BotNexus.Gateway.Sessions;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using System.IO.Abstractions;

namespace BotNexus.Gateway.Tests;

public sealed class SessionStoreEdgeCaseTests
{
    [Fact]
    public async Task FileStore_PathTraversalSessionId_IsEscapedWithinStorePath()
    {
        using var fixture = new StoreFixture();
        var store = fixture.CreateStore();
        const string sessionId = "../../../etc/shadow";

        var session = await store.GetOrCreateAsync(sessionId, "agent-a");
        await store.SaveAsync(session);

        var encodedName = Uri.EscapeDataString(sessionId);
        File.Exists(Path.Combine(fixture.StorePath, $"{encodedName}.jsonl")).Should().BeTrue();
        File.Exists(Path.Combine(fixture.StorePath, $"{encodedName}.meta.json")).Should().BeTrue();

        var parentPath = Directory.GetParent(fixture.StorePath)!.FullName;
        Directory.GetFiles(parentPath, "shadow*", SearchOption.TopDirectoryOnly).Should().BeEmpty();
    }

    [Fact]
    public async Task FileStore_NullAndEmptySessionIds_AreHandledDeterministically()
    {
        using var fixture = new StoreFixture();
        var store = fixture.CreateStore();

        var nullAct = async () => await store.GetOrCreateAsync(null!, "agent-a");
        await nullAct.Should().ThrowAsync<ArgumentNullException>();

        var emptySession = await store.GetOrCreateAsync(string.Empty, "agent-a");
        await store.SaveAsync(emptySession);
        var reloaded = await fixture.CreateStore().GetAsync(string.Empty);

        reloaded.Should().NotBeNull();
        reloaded!.SessionId.Should().BeEmpty();
    }

    [Fact]
    public async Task FileStore_ConcurrentGetOrCreate_SameSessionId_ReturnsSingleSessionInstance()
    {
        using var fixture = new StoreFixture();
        var store = fixture.CreateStore();

        var sessions = await Task.WhenAll(Enumerable.Range(0, 50)
            .Select(_ => store.GetOrCreateAsync("shared-session", "agent-a")));

        var first = sessions[0];
        sessions.Should().OnlyContain(session => ReferenceEquals(session, first));

        await store.SaveAsync(first);
        var allSessions = await fixture.CreateStore().ListAsync();
        allSessions.Should().ContainSingle(session => session.SessionId == "shared-session");
    }

    [Fact]
    public async Task SessionsController_GetHistory_WithLargeHistory_PaginatesCorrectly()
    {
        var store = new InMemorySessionStore();
        var session = await store.GetOrCreateAsync("large-history", "agent-a");
        for (var i = 0; i < 1200; i++)
            session.AddEntry(new SessionEntry { Role = MessageRole.User, Content = $"m-{i}" });

        var controller = new SessionsController(store);

        var result = await controller.GetHistory("large-history", offset: 1000, limit: 500, cancellationToken: CancellationToken.None);

        var response = (result.Result as OkObjectResult)?.Value as SessionHistoryResponse;
        response.Should().NotBeNull();
        response!.Offset.Should().Be(1000);
        response.Limit.Should().Be(200);
        response.TotalCount.Should().Be(1200);
        response.Entries.Should().HaveCount(200);
        response.Entries[0].Content.Should().Be("m-1000");
        response.Entries[^1].Content.Should().Be("m-1199");
    }

    private sealed class StoreFixture : IDisposable
    {
        public StoreFixture()
        {
            StorePath = Path.Combine(
                AppContext.BaseDirectory,
                "SessionStoreEdgeCaseTests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(StorePath);
        }

        public string StorePath { get; }

        public FileSessionStore CreateStore()
            => new(StorePath, NullLogger<FileSessionStore>.Instance, new FileSystem());

        public void Dispose()
        {
            if (Directory.Exists(StorePath))
                Directory.Delete(StorePath, true);
        }
    }
}

