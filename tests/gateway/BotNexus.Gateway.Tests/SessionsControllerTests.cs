using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Security;
using BotNexus.Gateway.Api.Controllers;
using BotNexus.Gateway.Sessions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
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

        var actionResult = await controller.List(null, CancellationToken.None);

        var okResult = actionResult as OkObjectResult;
        okResult.ShouldNotBeNull();
        var sessions = okResult!.Value as IEnumerable<object>;
        sessions.ShouldNotBeNull();
        sessions!.Count().ShouldBe(1);
    }

    [Fact]
    public async Task Get_WithUnknownSession_ReturnsNotFound()
    {
        var controller = new SessionsController(new InMemorySessionStore());

        var actionResult = await controller.Get("missing", CancellationToken.None);

        actionResult.Result.ShouldBeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task Delete_WithAnySession_ReturnsNoContent()
    {
        var store = new InMemorySessionStore();
        await store.GetOrCreateAsync("s1", "agent-a");
        var controller = new SessionsController(store);

        var result = await controller.Delete("s1", CancellationToken.None);

        result.ShouldBeOfType<NoContentResult>();
    }

    [Fact]
    public async Task ListSubAgents_WithMissingSession_ReturnsNotFound()
    {
        var subAgentManager = new Mock<ISubAgentManager>(MockBehavior.Strict);
        var controller = new SessionsController(new InMemorySessionStore(), subAgentManager.Object);

        var result = await controller.ListSubAgents("missing", CancellationToken.None);

        result.Result.ShouldBeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task ListSubAgents_WithKnownSession_ReturnsSessionSubAgents()
    {
        var store = new InMemorySessionStore();
        await store.GetOrCreateAsync("s1", "agent-a");
        var expected = new[]
        {
            new SubAgentInfo
            {
                SubAgentId = BotNexus.Domain.Primitives.AgentId.From("sub-1"),
                ParentSessionId = BotNexus.Domain.Primitives.SessionId.From("s1"),
                ChildSessionId = BotNexus.Domain.Primitives.SessionId.From("s1::subagent::sub-1"),
                Task = "task",
                Status = SubAgentStatus.Running,
                StartedAt = DateTimeOffset.UtcNow
            }
        };

        var subAgentManager = new Mock<ISubAgentManager>();
        subAgentManager
            .Setup(manager => manager.ListAsync("s1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);

        var controller = new SessionsController(store, subAgentManager.Object);
        var result = await controller.ListSubAgents("s1", CancellationToken.None);

        var payload = (result.Result as OkObjectResult)?.Value as IReadOnlyList<SubAgentInfo>;
        payload.ShouldNotBeNull();
        payload.Where(item => item.SubAgentId == "sub-1").ShouldHaveSingleItem();
    }

    [Fact]
    public async Task KillSubAgent_WithMismatchedParent_ReturnsForbidden()
    {
        var store = new InMemorySessionStore();
        await store.GetOrCreateAsync("s1", "agent-a");
        var subAgentManager = new Mock<ISubAgentManager>();
        subAgentManager
            .Setup(manager => manager.GetAsync("sub-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SubAgentInfo
            {
                SubAgentId = BotNexus.Domain.Primitives.AgentId.From("sub-1"),
                ParentSessionId = BotNexus.Domain.Primitives.SessionId.From("different-session"),
                ChildSessionId = BotNexus.Domain.Primitives.SessionId.From("different-session::subagent::sub-1"),
                Task = "task",
                Status = SubAgentStatus.Running,
                StartedAt = DateTimeOffset.UtcNow
            });

        var controller = new SessionsController(store, subAgentManager.Object);
        var result = await controller.KillSubAgent("s1", "sub-1", CancellationToken.None);

        result.ShouldBeOfType<ObjectResult>()
            .StatusCode.ShouldBe(StatusCodes.Status403Forbidden);
    }

    [Fact]
    public async Task KillSubAgent_WithUnknownSubAgent_ReturnsNotFound()
    {
        var store = new InMemorySessionStore();
        await store.GetOrCreateAsync("s1", "agent-a");
        var subAgentManager = new Mock<ISubAgentManager>();
        subAgentManager
            .Setup(manager => manager.GetAsync("missing-sub", It.IsAny<CancellationToken>()))
            .ReturnsAsync((SubAgentInfo?)null);

        var controller = new SessionsController(store, subAgentManager.Object);
        var result = await controller.KillSubAgent("s1", "missing-sub", CancellationToken.None);

        result.ShouldBeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task KillSubAgent_WhenOwnedAndKilled_ReturnsNoContent()
    {
        var store = new InMemorySessionStore();
        await store.GetOrCreateAsync("s1", "agent-a");
        var subAgentManager = new Mock<ISubAgentManager>();
        subAgentManager
            .Setup(manager => manager.GetAsync("sub-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SubAgentInfo
            {
                SubAgentId = BotNexus.Domain.Primitives.AgentId.From("sub-1"),
                ParentSessionId = BotNexus.Domain.Primitives.SessionId.From("s1"),
                ChildSessionId = BotNexus.Domain.Primitives.SessionId.From("s1::subagent::sub-1"),
                Task = "task",
                Status = SubAgentStatus.Running,
                StartedAt = DateTimeOffset.UtcNow
            });
        subAgentManager
            .Setup(manager => manager.KillAsync("sub-1", "s1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var controller = new SessionsController(store, subAgentManager.Object);
        var result = await controller.KillSubAgent("s1", "sub-1", CancellationToken.None);

        result.ShouldBeOfType<NoContentResult>();
    }

    [Fact]
    public async Task GetHistory_WithDefaults_ReturnsPagedHistoryAndTotalCount()
    {
        var store = new InMemorySessionStore();
        var session = await store.GetOrCreateAsync("s1", "agent-a");
        for (var i = 0; i < 60; i++)
            session.AddEntry(new SessionEntry { Role = MessageRole.User, Content = $"m-{i}" });

        var controller = new SessionsController(store);

        var result = await controller.GetHistory("s1", cancellationToken: CancellationToken.None);

        var response = (result.Result as OkObjectResult)?.Value as SessionHistoryResponse;
        response.ShouldNotBeNull();
        response!.Offset.ShouldBe(0);
        response.Limit.ShouldBe(50);
        response.TotalCount.ShouldBe(60);
        response.Entries.Count().ShouldBe(50);
        response.Entries[0].Content.ShouldBe("m-0");
    }

    [Fact]
    public async Task GetHistory_WithOffsetAndLargeLimit_AppliesPaginationAndLimitCap()
    {
        var store = new InMemorySessionStore();
        var session = await store.GetOrCreateAsync("s1", "agent-a");
        for (var i = 0; i < 260; i++)
            session.AddEntry(new SessionEntry { Role = MessageRole.User, Content = $"m-{i}" });

        var controller = new SessionsController(store);

        var result = await controller.GetHistory("s1", offset: 10, limit: 500, cancellationToken: CancellationToken.None);

        var response = (result.Result as OkObjectResult)?.Value as SessionHistoryResponse;
        response.ShouldNotBeNull();
        response!.Offset.ShouldBe(10);
        response.Limit.ShouldBe(200);
        response.TotalCount.ShouldBe(260);
        response.Entries.Count().ShouldBe(200);
        response.Entries[0].Content.ShouldBe("m-10");
        response.Entries[^1].Content.ShouldBe("m-209");
    }

    [Fact]
    public async Task GetHistory_WithOffsetBeyondTotal_ReturnsEmptyPageWithTotalCount()
    {
        var store = new InMemorySessionStore();
        var session = await store.GetOrCreateAsync("s1", "agent-a");
        for (var i = 0; i < 3; i++)
            session.AddEntry(new SessionEntry { Role = MessageRole.User, Content = $"m-{i}" });

        var controller = new SessionsController(store);

        var result = await controller.GetHistory("s1", offset: 10, limit: 10, cancellationToken: CancellationToken.None);

        var response = (result.Result as OkObjectResult)?.Value as SessionHistoryResponse;
        response.ShouldNotBeNull();
        response!.Offset.ShouldBe(10);
        response.TotalCount.ShouldBe(3);
        response.Entries.ShouldBeEmpty();
    }

    [Fact]
    public async Task GetHistory_WithEmptySession_ReturnsEmptyEntriesAndZeroTotal()
    {
        var store = new InMemorySessionStore();
        await store.GetOrCreateAsync("s1", "agent-a");
        var controller = new SessionsController(store);

        var result = await controller.GetHistory("s1", cancellationToken: CancellationToken.None);

        var response = (result.Result as OkObjectResult)?.Value as SessionHistoryResponse;
        response.ShouldNotBeNull();
        response!.TotalCount.ShouldBe(0);
        response.Entries.ShouldBeEmpty();
    }

    [Fact]
    public async Task Suspend_WithActiveSession_TransitionsToSuspended()
    {
        var store = new InMemorySessionStore();
        await store.GetOrCreateAsync("s1", "agent-a");
        var controller = new SessionsController(store);

        var result = await controller.Suspend("s1", CancellationToken.None);

        var session = (result.Result as OkObjectResult)?.Value as GatewaySession;
        session.ShouldNotBeNull();
        session!.Status.ShouldBe(SessionStatus.Suspended);
    }

    [Fact]
    public async Task Suspend_WithMissingSession_ReturnsNotFound()
    {
        var controller = new SessionsController(new InMemorySessionStore());

        var result = await controller.Suspend("missing", CancellationToken.None);

        result.Result.ShouldBeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task Suspend_WithInvalidState_ReturnsConflict()
    {
        var store = new InMemorySessionStore();
        var session = await store.GetOrCreateAsync("s1", "agent-a");
        session.Status = SessionStatus.Suspended;
        var controller = new SessionsController(store);

        var result = await controller.Suspend("s1", CancellationToken.None);

        result.Result.ShouldBeOfType<ConflictObjectResult>();
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
        resumed.ShouldNotBeNull();
        resumed!.Status.ShouldBe(SessionStatus.Active);
    }

    [Fact]
    public async Task Resume_WithInvalidState_ReturnsConflict()
    {
        var store = new InMemorySessionStore();
        await store.GetOrCreateAsync("s1", "agent-a");
        var controller = new SessionsController(store);

        var result = await controller.Resume("s1", CancellationToken.None);

        result.Result.ShouldBeOfType<ConflictObjectResult>();
    }

    [Fact]
    public async Task Resume_WithMissingSession_ReturnsNotFound()
    {
        var controller = new SessionsController(new InMemorySessionStore());

        var result = await controller.Resume("missing", CancellationToken.None);

        result.Result.ShouldBeOfType<NotFoundResult>();
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
        payload.ShouldNotBeNull();
        payload.ShouldContainKey("tenantId");
        payload!["tenantId"].ShouldBe("tenant-a");
    }

    [Fact]
    public async Task GetMetadata_WithEmptyMetadata_ReturnsEmptyDictionary()
    {
        var store = new InMemorySessionStore();
        await store.GetOrCreateAsync("s1", "agent-a");
        var controller = new SessionsController(store);

        var result = await controller.GetMetadata("s1", CancellationToken.None);

        var payload = (result.Result as OkObjectResult)?.Value as Dictionary<string, object?>;
        payload.ShouldNotBeNull();
        payload.ShouldBeEmpty();
    }

    [Fact]
    public async Task GetMetadata_WithUnknownSession_ReturnsNotFound()
    {
        var controller = new SessionsController(new InMemorySessionStore());

        var result = await controller.GetMetadata("missing", CancellationToken.None);

        result.Result.ShouldBeOfType<NotFoundResult>();
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

        result.Result.ShouldBeOfType<ObjectResult>()
            .StatusCode.ShouldBe(StatusCodes.Status403Forbidden);
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
        payload.ShouldNotBeNull();
        payload.ShouldContainKeyAndValue("locale", (object?)"en-US");
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

        result.Result.ShouldBeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task PatchMetadata_WithUnknownSession_ReturnsNotFound()
    {
        var controller = new SessionsController(new InMemorySessionStore());
        using var patchDocument = JsonDocument.Parse("""{"theme":"dark"}""");

        var result = await controller.PatchMetadata("missing", patchDocument.RootElement.Clone(), CancellationToken.None);

        result.Result.ShouldBeOfType<NotFoundResult>();
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

        result.Result.ShouldBeOfType<ObjectResult>()
            .StatusCode.ShouldBe(StatusCodes.Status403Forbidden);
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
        payload.ShouldContainKeyAndValue("theme", (object?)"dark");
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

        result.Result.ShouldBeOfType<OkObjectResult>();
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
        payload.ShouldNotBeNull();
        payload.ShouldContainKey("theme");
        payload!["theme"].ShouldBe("dark");
        payload.ShouldNotContainKey("removeMe");
        payload.ShouldContainKey("nested");
        ((Dictionary<string, object?>)payload["nested"]!).ShouldContainKeyAndValue("key", (object?)"value");
    }

    [Fact]
    public async Task PatchMetadata_WithNonObjectBody_ReturnsBadRequest()
    {
        var store = new InMemorySessionStore();
        await store.GetOrCreateAsync("s1", "agent-a");
        var controller = new SessionsController(store);
        using var patchDocument = JsonDocument.Parse("""["not","an","object"]""");

        var result = await controller.PatchMetadata("s1", patchDocument.RootElement.Clone(), CancellationToken.None);

        result.Result.ShouldBeOfType<BadRequestObjectResult>();
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
        payload.ShouldNotBeNull();
        payload.ShouldContainKeyAndValue("existing", (object?)"value");
        payload.ShouldContainKeyAndValue("theme", (object?)"dark");
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
        payload.ShouldNotBeNull();
        payload.ShouldNotContainKey("removeMe");
        payload.ShouldContainKeyAndValue("keepMe", (object?)"value");
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
        savedSession.ShouldNotBeNull();
        savedSession!.Metadata.ShouldContainKeyAndValue("locale", (object?)"en-US");
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
        payload.ShouldNotBeNull();
        payload!["count"].ShouldBeOfType<long>().ShouldBe(2L);
        payload["enabled"].ShouldBe(true);
        payload["threshold"].ShouldBeOfType<decimal>().ShouldBe(1.5m);
        payload["tags"].ShouldBeAssignableTo<List<object?>>();
    }

    // ── Seal endpoint tests ──────────────────────────────────────────

    [Fact]
    public async Task Seal_ExpiredSubAgent_Returns200()
    {
        var store = new InMemorySessionStore();
        var session = await store.GetOrCreateAsync("parent::subagent::child1", "agent-a");
        session.Status = SessionStatus.Expired;
        var controller = new SessionsController(store);
        var before = DateTimeOffset.UtcNow;

        var result = await controller.Seal("parent::subagent::child1", CancellationToken.None);

        result.ShouldBeOfType<OkObjectResult>();
        session.Status.ShouldBe(SessionStatus.Sealed);
        session.UpdatedAt.ShouldBeGreaterThanOrEqualTo(before);
    }

    [Fact]
    public async Task Seal_AlreadySealed_Returns204()
    {
        var store = new InMemorySessionStore();
        var session = await store.GetOrCreateAsync("parent::subagent::child2", "agent-a");
        session.Status = SessionStatus.Sealed;
        var controller = new SessionsController(store);

        var result = await controller.Seal("parent::subagent::child2", CancellationToken.None);

        result.ShouldBeOfType<NoContentResult>();
    }

    [Fact]
    public async Task Seal_NonExistentSession_Returns404()
    {
        var controller = new SessionsController(new InMemorySessionStore());

        var result = await controller.Seal("parent::subagent::missing", CancellationToken.None);

        result.ShouldBeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task Seal_NonSubAgentSession_Returns400()
    {
        var store = new InMemorySessionStore();
        var session = await store.GetOrCreateAsync("regular-session-id", "agent-a");
        session.Status = SessionStatus.Expired;
        var controller = new SessionsController(store);

        var result = await controller.Seal("regular-session-id", CancellationToken.None);

        result.ShouldBeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Seal_ActiveSession_Returns409()
    {
        var store = new InMemorySessionStore();
        await store.GetOrCreateAsync("parent::subagent::child3", "agent-a");
        var controller = new SessionsController(store);

        var result = await controller.Seal("parent::subagent::child3", CancellationToken.None);

        result.ShouldBeOfType<ConflictObjectResult>();
    }

    [Fact]
    public async Task Seal_SuspendedSession_Returns409()
    {
        var store = new InMemorySessionStore();
        var session = await store.GetOrCreateAsync("parent::subagent::child4", "agent-a");
        session.Status = SessionStatus.Suspended;
        var controller = new SessionsController(store);

        var result = await controller.Seal("parent::subagent::child4", CancellationToken.None);

        result.ShouldBeOfType<ConflictObjectResult>();
    }

    [Fact]
    public async Task Seal_PersistsChangesInSessionStore()
    {
        var store = new InMemorySessionStore();
        var session = await store.GetOrCreateAsync("parent::subagent::child5", "agent-a");
        session.Status = SessionStatus.Expired;
        var controller = new SessionsController(store);

        await controller.Seal("parent::subagent::child5", CancellationToken.None);

        var saved = await store.GetAsync("parent::subagent::child5", CancellationToken.None);
        saved.ShouldNotBeNull();
        saved!.Status.ShouldBe(SessionStatus.Sealed);
    }

    [Fact]
    public async Task Seal_ReturnsSessionIdAndStatusInBody()
    {
        var store = new InMemorySessionStore();
        var session = await store.GetOrCreateAsync("parent::subagent::child6", "agent-a");
        session.Status = SessionStatus.Expired;
        var controller = new SessionsController(store);

        var result = await controller.Seal("parent::subagent::child6", CancellationToken.None);

        var ok = result.ShouldBeOfType<OkObjectResult>();
        var body = ok.Value;
        body.ShouldNotBeNull();
        // Verify the anonymous object has expected shape via reflection
        var sessionIdProp = body!.GetType().GetProperty("sessionId");
        sessionIdProp.ShouldNotBeNull();
        sessionIdProp!.GetValue(body).ShouldBe("parent::subagent::child6");
        var statusProp = body.GetType().GetProperty("status");
        statusProp.ShouldNotBeNull();
        statusProp!.GetValue(body).ShouldBe("Sealed");
    }

    [Fact]
    public async Task Seal_ConcurrentSealRequests_SecondReturns204()
    {
        var store = new InMemorySessionStore();
        var session = await store.GetOrCreateAsync("parent::subagent::concurrent1", "agent-a");
        session.Status = SessionStatus.Expired;
        var controller = new SessionsController(store);

        var first = await controller.Seal("parent::subagent::concurrent1", CancellationToken.None);
        var second = await controller.Seal("parent::subagent::concurrent1", CancellationToken.None);

        first.ShouldBeOfType<OkObjectResult>();
        second.ShouldBeOfType<NoContentResult>();
        session.Status.ShouldBe(SessionStatus.Sealed);
    }

    [Fact]
    public async Task Seal_PreservesOtherSessionProperties()
    {
        var store = new InMemorySessionStore();
        var session = await store.GetOrCreateAsync("parent::subagent::preserve1", "agent-b");
        session.Status = SessionStatus.Expired;
        session.Metadata["key1"] = "value1";
        var originalCreatedAt = session.CreatedAt;
        var originalAgentId = session.AgentId;
        var originalSessionType = session.SessionType;
        var controller = new SessionsController(store);

        await controller.Seal("parent::subagent::preserve1", CancellationToken.None);

        var saved = await store.GetAsync("parent::subagent::preserve1", CancellationToken.None);
        saved.ShouldNotBeNull();
        saved!.Status.ShouldBe(SessionStatus.Sealed);
        saved.CreatedAt.ShouldBe(originalCreatedAt);
        saved.AgentId.ShouldBe(originalAgentId);
        saved.SessionType.ShouldBe(originalSessionType);
        saved.Metadata["key1"].ShouldBe("value1");
    }

    [Theory]
    [InlineData("parent-session::subagent::abc123")]
    [InlineData("long-parent-id-with-dashes::subagent::short")]
    public async Task Seal_SubAgentSessionId_VariousFormats(string sessionId)
    {
        var store = new InMemorySessionStore();
        var session = await store.GetOrCreateAsync(sessionId, "agent-a");
        session.Status = SessionStatus.Expired;
        var controller = new SessionsController(store);

        var result = await controller.Seal(sessionId, CancellationToken.None);

        result.ShouldBeOfType<OkObjectResult>();
        session.Status.ShouldBe(SessionStatus.Sealed);
    }

    [Fact]
    public async Task Seal_UpdatesTimestamp()
    {
        var store = new InMemorySessionStore();
        var session = await store.GetOrCreateAsync("parent::subagent::timestamp1", "agent-a");
        session.Status = SessionStatus.Expired;
        var pastTimestamp = session.UpdatedAt.AddMinutes(-5);
        session.UpdatedAt = pastTimestamp;
        var controller = new SessionsController(store);

        await controller.Seal("parent::subagent::timestamp1", CancellationToken.None);

        session.UpdatedAt.ShouldBeGreaterThan(pastTimestamp);
    }


    [Fact]
    public async Task GetSessions_ReturnsConversationId_WhenSet()
    {
        var store = new InMemorySessionStore();
        var session = await store.GetOrCreateAsync("s-conv-1", "agent-a");
        var knownConvId = BotNexus.Domain.Primitives.ConversationId.From("c_testconvid123");
        session.Session.ConversationId = knownConvId;
        await store.SaveAsync(session);
        var controller = new SessionsController(store);

        var actionResult = await controller.List(null, CancellationToken.None);

        var okResult = actionResult.ShouldBeOfType<OkObjectResult>();
        var json = System.Text.Json.JsonSerializer.Serialize(okResult!.Value);
        var doc = System.Text.Json.JsonDocument.Parse(json);
        var first = doc.RootElement.EnumerateArray().First();
        first.TryGetProperty("conversationId", out var convIdProp).ShouldBeTrue("conversationId property should be present in List response");
        convIdProp.GetString().ShouldBe("c_testconvid123");
    }

    [Fact]
    public async Task GetSession_ById_ReturnsConversationId()
    {
        var store = new InMemorySessionStore();
        var session = await store.GetOrCreateAsync("s-conv-2", "agent-a");
        var knownConvId = BotNexus.Domain.Primitives.ConversationId.From("c_testconvid456");
        session.Session.ConversationId = knownConvId;
        await store.SaveAsync(session);
        var controller = new SessionsController(store);

        var actionResult = await controller.Get("s-conv-2", CancellationToken.None);

        var okResult = actionResult.Result.ShouldBeOfType<OkObjectResult>();
        var result = okResult!.Value.ShouldBeOfType<GatewaySession>();
        result.Session.ConversationId.ShouldNotBeNull("ConversationId must be set on the returned session");
        result.Session.ConversationId!.Value.Value.ShouldBe("c_testconvid456");
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
