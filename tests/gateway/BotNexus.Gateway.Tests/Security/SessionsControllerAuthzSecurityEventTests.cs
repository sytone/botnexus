using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Security;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Api.Controllers;
using BotNexus.Gateway.Sessions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace BotNexus.Gateway.Tests.Security;

/// <summary>
/// Tests for the security-event emissions wired into <see cref="SessionsController"/>
/// authorization denials (Step 3/5 of the security-event taxonomy, issue #1646 / #1526).
/// Each per-session scope-check denial (the #1493/#558 reserved-session caller guard)
/// must emit exactly one <see cref="SecurityEvent"/> with <c>category=authorization</c>,
/// <c>outcome=denied</c> to the trusted <see cref="ISecurityEventSink"/>. A matching caller
/// emits nothing; a failing sink must never break or alter the 403 decision.
/// </summary>
public sealed class SessionsControllerAuthzSecurityEventTests
{
    private const string CallerIdentityItemKey = "BotNexus.Gateway.CallerIdentity";
    private readonly RecordingSink _sink = new();

    // -- Denied --------------------------------------------------------

    [Fact]
    public async Task Suspend_WhenCallerMismatch_EmitsAuthorizationDeniedEvent()
    {
        var store = new InMemorySessionStore();
        var session = await store.GetOrCreateAsync(SessionId.From("s1"), AgentId.From("agent-a"));
        session.CallerId = "caller-a";
        await store.SaveAsync(session);
        var controller = new SessionsController(store, securityEvents: _sink)
        {
            ControllerContext = CreateControllerContext("caller-b")
        };

        var result = await controller.Suspend("s1", CancellationToken.None);

        result.Result.ShouldBeOfType<ObjectResult>()
            .StatusCode.ShouldBe(StatusCodes.Status403Forbidden);
        _sink.Events.Count.ShouldBe(1);
        var evt = _sink.Events[0];
        evt.Category.ShouldBe(SecurityEventCategory.Authorization);
        evt.Outcome.ShouldBe(SecurityEventOutcome.Denied);
        evt.Control.ShouldBe(SecurityControlFamily.Authorization);
        evt.Policy.ShouldBe(SecurityPolicyDecision.Deny);
        evt.Target!.Kind.ShouldBe(SecurityTargetKind.Session);
    }

    [Fact]
    public async Task Suspend_WhenCallerMismatch_HashesCallerAsActor()
    {
        var store = new InMemorySessionStore();
        var session = await store.GetOrCreateAsync(SessionId.From("s1"), AgentId.From("agent-a"));
        session.CallerId = "caller-a";
        await store.SaveAsync(session);
        const string Caller = "caller-secret-xyz";
        var controller = new SessionsController(store, securityEvents: _sink)
        {
            ControllerContext = CreateControllerContext(Caller)
        };

        await controller.Suspend("s1", CancellationToken.None);

        var evt = _sink.Events[0];
        evt.Actor.ShouldNotBeNull();
        evt.Actor!.Id.ShouldNotBe(Caller);
        evt.Actor.Id.ShouldNotContain(Caller);
        evt.Actor.Id.ShouldNotBeNullOrWhiteSpace();
    }

    // -- Success: matching caller emits nothing ------------------------

    [Fact]
    public async Task Suspend_WhenCallerMatches_EmitsNoAuthorizationDeniedEvent()
    {
        var store = new InMemorySessionStore();
        var session = await store.GetOrCreateAsync(SessionId.From("s1"), AgentId.From("agent-a"));
        session.CallerId = "caller-a";
        await store.SaveAsync(session);
        var controller = new SessionsController(store, securityEvents: _sink)
        {
            ControllerContext = CreateControllerContext("caller-a")
        };

        await controller.Suspend("s1", CancellationToken.None);

        _sink.Events.Count.ShouldBe(0);
    }

    // -- Non-blocking / never fails the decision -----------------------

    [Fact]
    public async Task Suspend_WhenSinkThrows_StillDenies()
    {
        var store = new InMemorySessionStore();
        var session = await store.GetOrCreateAsync(SessionId.From("s1"), AgentId.From("agent-a"));
        session.CallerId = "caller-a";
        await store.SaveAsync(session);
        var controller = new SessionsController(store, securityEvents: new ThrowingSink())
        {
            ControllerContext = CreateControllerContext("caller-b")
        };

        var result = await controller.Suspend("s1", CancellationToken.None);

        result.Result.ShouldBeOfType<ObjectResult>()
            .StatusCode.ShouldBe(StatusCodes.Status403Forbidden);
    }

    [Fact]
    public async Task Suspend_NullSink_DoesNotEmitAndStillDenies()
    {
        var store = new InMemorySessionStore();
        var session = await store.GetOrCreateAsync(SessionId.From("s1"), AgentId.From("agent-a"));
        session.CallerId = "caller-a";
        await store.SaveAsync(session);
        var controller = new SessionsController(store)
        {
            ControllerContext = CreateControllerContext("caller-b")
        };

        var result = await controller.Suspend("s1", CancellationToken.None);

        result.Result.ShouldBeOfType<ObjectResult>()
            .StatusCode.ShouldBe(StatusCodes.Status403Forbidden);
    }

    private static ControllerContext CreateControllerContext(string callerId)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Items[CallerIdentityItemKey] = new GatewayCallerIdentity { CallerId = callerId };
        return new ControllerContext { HttpContext = httpContext };
    }

    private sealed class RecordingSink : ISecurityEventSink
    {
        private readonly List<SecurityEvent> _events = [];
        public IReadOnlyList<SecurityEvent> Events => _events;
        public int Count => _events.Count;
        public void Record(SecurityEvent securityEvent) => _events.Add(securityEvent);
        public IReadOnlyList<SecurityEvent> Snapshot() => _events;
        public void Clear() => _events.Clear();
    }

    private sealed class ThrowingSink : ISecurityEventSink
    {
        public int Count => 0;
        public void Record(SecurityEvent securityEvent) => throw new InvalidOperationException("sink down");
        public IReadOnlyList<SecurityEvent> Snapshot() => [];
        public void Clear() { }
    }
}
