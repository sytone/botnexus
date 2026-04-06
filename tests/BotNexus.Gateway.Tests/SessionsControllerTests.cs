using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Security;
using BotNexus.Gateway.Api.Controllers;
using BotNexus.Gateway.Sessions;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;

namespace BotNexus.Gateway.Tests;

public sealed class SessionsControllerTests
{
    private const string CallerIdentityItemKey = "BotNexus.Gateway.CallerIdentity";

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

    [Fact]
    public async Task GetMetadata_WithExistingSession_ReturnsMetadata()
    {
        var store = new InMemorySessionStore();
        var session = await store.GetOrCreateAsync("s1", "agent-a");
        session.Metadata["tenantId"] = "tenant-a";
        var controller = new SessionsController(store);

        var result = await controller.GetMetadata("s1", CancellationToken.None);

        var payload = (result.Result as OkObjectResult)?.Value as Dictionary<string, object?>;
        payload.Should().NotBeNull();
        payload.Should().ContainKey("tenantId");
        payload!["tenantId"].Should().Be("tenant-a");
    }

    [Fact]
    public async Task GetMetadata_WithEmptyMetadata_ReturnsEmptyDictionary()
    {
        var store = new InMemorySessionStore();
        await store.GetOrCreateAsync("s1", "agent-a");
        var controller = new SessionsController(store);

        var result = await controller.GetMetadata("s1", CancellationToken.None);

        var payload = (result.Result as OkObjectResult)?.Value as Dictionary<string, object?>;
        payload.Should().NotBeNull();
        payload.Should().BeEmpty();
    }

    [Fact]
    public async Task GetMetadata_WithUnknownSession_ReturnsNotFound()
    {
        var controller = new SessionsController(new InMemorySessionStore());

        var result = await controller.GetMetadata("missing", CancellationToken.None);

        result.Result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task GetMetadata_WithMismatchedCaller_ReturnsForbidden()
    {
        var store = new InMemorySessionStore();
        var session = await store.GetOrCreateAsync("s1", "agent-a");
        session.CallerId = "caller-a";
        var controller = new SessionsController(store);
        controller.ControllerContext = CreateControllerContext("caller-b");

        var result = await controller.GetMetadata("s1", CancellationToken.None);

        result.Result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
    }

    [Fact]
    public async Task GetMetadata_WithMatchingCaller_ReturnsMetadata()
    {
        var store = new InMemorySessionStore();
        var session = await store.GetOrCreateAsync("s1", "agent-a");
        session.CallerId = "caller-a";
        session.Metadata["locale"] = "en-US";

        var controller = new SessionsController(store)
        {
            ControllerContext = CreateControllerContext("caller-a")
        };

        var result = await controller.GetMetadata("s1", CancellationToken.None);

        var payload = (result.Result as OkObjectResult)?.Value as Dictionary<string, object?>;
        payload.Should().NotBeNull();
        payload.Should().Contain("locale", "en-US");
    }

    [Fact]
    public async Task GetMetadata_WithMissingCallerIdentity_SkipsAuthorizationCheck()
    {
        var store = new InMemorySessionStore();
        var session = await store.GetOrCreateAsync("s1", "agent-a");
        session.CallerId = "caller-a";
        session.Metadata["locale"] = "en-US";

        var controller = new SessionsController(store);

        var result = await controller.GetMetadata("s1", CancellationToken.None);

        result.Result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task PatchMetadata_WithUnknownSession_ReturnsNotFound()
    {
        var controller = new SessionsController(new InMemorySessionStore());
        using var patchDocument = JsonDocument.Parse("""{"theme":"dark"}""");

        var result = await controller.PatchMetadata("missing", patchDocument.RootElement.Clone(), CancellationToken.None);

        result.Result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task PatchMetadata_WithMismatchedCaller_ReturnsForbidden()
    {
        var store = new InMemorySessionStore();
        var session = await store.GetOrCreateAsync("s1", "agent-a");
        session.CallerId = "caller-a";
        var controller = new SessionsController(store);
        controller.ControllerContext = CreateControllerContext("caller-b");
        using var patchDocument = JsonDocument.Parse("""{"theme":"dark"}""");

        var result = await controller.PatchMetadata("s1", patchDocument.RootElement.Clone(), CancellationToken.None);

        result.Result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
    }

    [Fact]
    public async Task PatchMetadata_WithMatchingCaller_UpdatesMetadata()
    {
        var store = new InMemorySessionStore();
        var session = await store.GetOrCreateAsync("s1", "agent-a");
        session.CallerId = "caller-a";

        var controller = new SessionsController(store)
        {
            ControllerContext = CreateControllerContext("caller-a")
        };
        using var patchDocument = JsonDocument.Parse("""{"theme":"dark"}""");

        var result = await controller.PatchMetadata("s1", patchDocument.RootElement.Clone(), CancellationToken.None);

        var payload = (result.Result as OkObjectResult)?.Value as Dictionary<string, object?>;
        payload.Should().Contain("theme", "dark");
    }

    [Fact]
    public async Task PatchMetadata_WithMissingCallerIdentity_SkipsAuthorizationCheck()
    {
        var store = new InMemorySessionStore();
        var session = await store.GetOrCreateAsync("s1", "agent-a");
        session.CallerId = "caller-a";

        var controller = new SessionsController(store);
        using var patchDocument = JsonDocument.Parse("""{"theme":"dark"}""");

        var result = await controller.PatchMetadata("s1", patchDocument.RootElement.Clone(), CancellationToken.None);

        result.Result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task PatchMetadata_WithObjectBody_MergesAndRemovesKeys()
    {
        var store = new InMemorySessionStore();
        var session = await store.GetOrCreateAsync("s1", "agent-a");
        session.Metadata["removeMe"] = "x";
        var controller = new SessionsController(store);
        using var patchDocument = JsonDocument.Parse("""{"theme":"dark","removeMe":null,"nested":{"key":"value"}}""");

        var result = await controller.PatchMetadata("s1", patchDocument.RootElement.Clone(), CancellationToken.None);

        var payload = (result.Result as OkObjectResult)?.Value as Dictionary<string, object?>;
        payload.Should().NotBeNull();
        payload.Should().ContainKey("theme");
        payload!["theme"].Should().Be("dark");
        payload.Should().NotContainKey("removeMe");
        payload.Should().ContainKey("nested");
        ((Dictionary<string, object?>)payload["nested"]!).Should().Contain("key", "value");
    }

    [Fact]
    public async Task PatchMetadata_WithNonObjectBody_ReturnsBadRequest()
    {
        var store = new InMemorySessionStore();
        await store.GetOrCreateAsync("s1", "agent-a");
        var controller = new SessionsController(store);
        using var patchDocument = JsonDocument.Parse("""["not","an","object"]""");

        var result = await controller.PatchMetadata("s1", patchDocument.RootElement.Clone(), CancellationToken.None);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task PatchMetadata_WithObjectBody_MergesWithoutRemovingExistingKeys()
    {
        var store = new InMemorySessionStore();
        var session = await store.GetOrCreateAsync("s1", "agent-a");
        session.Metadata["existing"] = "value";
        var controller = new SessionsController(store);
        using var patchDocument = JsonDocument.Parse("""{"theme":"dark"}""");

        var result = await controller.PatchMetadata("s1", patchDocument.RootElement.Clone(), CancellationToken.None);

        var payload = (result.Result as OkObjectResult)?.Value as Dictionary<string, object?>;
        payload.Should().NotBeNull();
        payload.Should().Contain("existing", "value");
        payload.Should().Contain("theme", "dark");
    }

    [Fact]
    public async Task PatchMetadata_WithNullValue_RemovesOnlyTargetedKey()
    {
        var store = new InMemorySessionStore();
        var session = await store.GetOrCreateAsync("s1", "agent-a");
        session.Metadata["removeMe"] = "value";
        session.Metadata["keepMe"] = "value";
        var controller = new SessionsController(store);
        using var patchDocument = JsonDocument.Parse("""{"removeMe":null}""");

        var result = await controller.PatchMetadata("s1", patchDocument.RootElement.Clone(), CancellationToken.None);

        var payload = (result.Result as OkObjectResult)?.Value as Dictionary<string, object?>;
        payload.Should().NotBeNull();
        payload.Should().NotContainKey("removeMe");
        payload.Should().Contain("keepMe", "value");
    }

    [Fact]
    public async Task PatchMetadata_PersistsChangesInSessionStore()
    {
        var store = new InMemorySessionStore();
        await store.GetOrCreateAsync("s1", "agent-a");
        var controller = new SessionsController(store);
        using var patchDocument = JsonDocument.Parse("""{"locale":"en-US"}""");

        await controller.PatchMetadata("s1", patchDocument.RootElement.Clone(), CancellationToken.None);

        var savedSession = await store.GetAsync("s1", CancellationToken.None);
        savedSession.Should().NotBeNull();
        savedSession!.Metadata.Should().Contain("locale", "en-US");
    }

    [Fact]
    public async Task PatchMetadata_ConvertsJsonValuesToExpectedTypes()
    {
        var store = new InMemorySessionStore();
        await store.GetOrCreateAsync("s1", "agent-a");
        var controller = new SessionsController(store);
        using var patchDocument = JsonDocument.Parse("""{"count":2,"enabled":true,"threshold":1.5,"tags":["a","b"]}""");

        var result = await controller.PatchMetadata("s1", patchDocument.RootElement.Clone(), CancellationToken.None);

        var payload = (result.Result as OkObjectResult)?.Value as Dictionary<string, object?>;
        payload.Should().NotBeNull();
        payload!["count"].Should().BeOfType<long>().Which.Should().Be(2);
        payload["enabled"].Should().Be(true);
        payload["threshold"].Should().BeOfType<decimal>().Which.Should().Be(1.5m);
        payload["tags"].Should().BeAssignableTo<List<object?>>();
    }

    private static ControllerContext CreateControllerContext(string callerId)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Items[CallerIdentityItemKey] = new GatewayCallerIdentity
        {
            CallerId = callerId
        };

        return new ControllerContext
        {
            HttpContext = httpContext
        };
    }
}
