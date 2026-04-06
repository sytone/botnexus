using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Api.Controllers;
using BotNexus.Gateway.Sessions;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;

namespace BotNexus.Gateway.Tests;

public sealed class SessionsControllerTests
{
    [Fact]
    public async Task List_WithExistingSessions_ReturnsSessions()
    {
        var store = new InMemorySessionStore();
        await store.GetOrCreateAsync("s1", "agent-a");
        var controller = new SessionsController(store);

        var result = await controller.List(null, CancellationToken.None);

        ((result.Result as OkObjectResult)?.Value as IReadOnlyList<GatewaySession>).Should().HaveCount(1);
    }

    [Fact]
    public async Task Get_WithUnknownSession_ReturnsNotFound()
    {
        var controller = new SessionsController(new InMemorySessionStore());

        var result = await controller.Get("missing", CancellationToken.None);

        result.Result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task Delete_WithAnySession_ReturnsNoContent()
    {
        var store = new InMemorySessionStore();
        await store.GetOrCreateAsync("s1", "agent-a");
        var controller = new SessionsController(store);

        var result = await controller.Delete("s1", CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();
    }

    [Fact]
    public async Task GetHistory_WithDefaults_ReturnsPagedHistoryAndTotalCount()
    {
        var store = new InMemorySessionStore();
        var session = await store.GetOrCreateAsync("s1", "agent-a");
        for (var i = 0; i < 60; i++)
            session.AddEntry(new SessionEntry { Role = "user", Content = $"m-{i}" });

        var controller = new SessionsController(store);

        var result = await controller.GetHistory("s1", cancellationToken: CancellationToken.None);

        var response = (result.Result as OkObjectResult)?.Value as SessionHistoryResponse;
        response.Should().NotBeNull();
        response!.Offset.Should().Be(0);
        response.Limit.Should().Be(50);
        response.TotalCount.Should().Be(60);
        response.Entries.Should().HaveCount(50);
        response.Entries[0].Content.Should().Be("m-0");
    }

    [Fact]
    public async Task GetHistory_WithOffsetAndLargeLimit_AppliesPaginationAndLimitCap()
    {
        var store = new InMemorySessionStore();
        var session = await store.GetOrCreateAsync("s1", "agent-a");
        for (var i = 0; i < 260; i++)
            session.AddEntry(new SessionEntry { Role = "user", Content = $"m-{i}" });

        var controller = new SessionsController(store);

        var result = await controller.GetHistory("s1", offset: 10, limit: 500, cancellationToken: CancellationToken.None);

        var response = (result.Result as OkObjectResult)?.Value as SessionHistoryResponse;
        response.Should().NotBeNull();
        response!.Offset.Should().Be(10);
        response.Limit.Should().Be(200);
        response.TotalCount.Should().Be(260);
        response.Entries.Should().HaveCount(200);
        response.Entries[0].Content.Should().Be("m-10");
        response.Entries[^1].Content.Should().Be("m-209");
    }

    [Fact]
    public async Task GetHistory_WithOffsetBeyondTotal_ReturnsEmptyPageWithTotalCount()
    {
        var store = new InMemorySessionStore();
        var session = await store.GetOrCreateAsync("s1", "agent-a");
        for (var i = 0; i < 3; i++)
            session.AddEntry(new SessionEntry { Role = "user", Content = $"m-{i}" });

        var controller = new SessionsController(store);

        var result = await controller.GetHistory("s1", offset: 10, limit: 10, cancellationToken: CancellationToken.None);

        var response = (result.Result as OkObjectResult)?.Value as SessionHistoryResponse;
        response.Should().NotBeNull();
        response!.Offset.Should().Be(10);
        response.TotalCount.Should().Be(3);
        response.Entries.Should().BeEmpty();
    }

    [Fact]
    public async Task GetHistory_WithEmptySession_ReturnsEmptyEntriesAndZeroTotal()
    {
        var store = new InMemorySessionStore();
        await store.GetOrCreateAsync("s1", "agent-a");
        var controller = new SessionsController(store);

        var result = await controller.GetHistory("s1", cancellationToken: CancellationToken.None);

        var response = (result.Result as OkObjectResult)?.Value as SessionHistoryResponse;
        response.Should().NotBeNull();
        response!.TotalCount.Should().Be(0);
        response.Entries.Should().BeEmpty();
    }

    [Fact]
    public async Task Suspend_WithActiveSession_TransitionsToSuspended()
    {
        var store = new InMemorySessionStore();
        await store.GetOrCreateAsync("s1", "agent-a");
        var controller = new SessionsController(store);

        var result = await controller.Suspend("s1", CancellationToken.None);

        var session = (result.Result as OkObjectResult)?.Value as GatewaySession;
        session.Should().NotBeNull();
        session!.Status.Should().Be(SessionStatus.Suspended);
    }

    [Fact]
    public async Task Suspend_WithMissingSession_ReturnsNotFound()
    {
        var controller = new SessionsController(new InMemorySessionStore());

        var result = await controller.Suspend("missing", CancellationToken.None);

        result.Result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task Suspend_WithInvalidState_ReturnsConflict()
    {
        var store = new InMemorySessionStore();
        var session = await store.GetOrCreateAsync("s1", "agent-a");
        session.Status = SessionStatus.Suspended;
        var controller = new SessionsController(store);

        var result = await controller.Suspend("s1", CancellationToken.None);

        result.Result.Should().BeOfType<ConflictObjectResult>();
    }

    [Fact]
    public async Task Resume_WithSuspendedSession_TransitionsToActive()
    {
        var store = new InMemorySessionStore();
        var session = await store.GetOrCreateAsync("s1", "agent-a");
        session.Status = SessionStatus.Suspended;
        var controller = new SessionsController(store);

        var result = await controller.Resume("s1", CancellationToken.None);

        var resumed = (result.Result as OkObjectResult)?.Value as GatewaySession;
        resumed.Should().NotBeNull();
        resumed!.Status.Should().Be(SessionStatus.Active);
    }

    [Fact]
    public async Task Resume_WithInvalidState_ReturnsConflict()
    {
        var store = new InMemorySessionStore();
        await store.GetOrCreateAsync("s1", "agent-a");
        var controller = new SessionsController(store);

        var result = await controller.Resume("s1", CancellationToken.None);

        result.Result.Should().BeOfType<ConflictObjectResult>();
    }

    [Fact]
    public async Task Resume_WithMissingSession_ReturnsNotFound()
    {
        var controller = new SessionsController(new InMemorySessionStore());

        var result = await controller.Resume("missing", CancellationToken.None);

        result.Result.Should().BeOfType<NotFoundResult>();
    }
}
