using BotNexus.Gateway.Abstractions.Security;
using BotNexus.Gateway.Security;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotNexus.Gateway.Tests.Security;

/// <summary>
/// Tests for the security-event emissions wired into <see cref="ApiKeyGatewayAuthHandler"/>
/// (Step 3/5 of the security-event taxonomy, issue #1646 / #1526). Every authentication
/// handshake outcome must emit exactly one <see cref="SecurityEvent"/> with
/// <c>category=auth</c> to the trusted <see cref="ISecurityEventSink"/>: success on accept,
/// failure on reject. Nothing must reach any public diagnostic stream, and a failing sink
/// must never break or alter the authentication decision.
/// </summary>
public sealed class ApiKeyGatewayAuthHandlerSecurityEventTests
{
    private readonly RecordingSink _sink = new();

    // -- Success -------------------------------------------------------

    [Fact]
    public async Task AuthenticateAsync_DevelopmentMode_EmitsAuthSuccessEvent()
    {
        var handler = new ApiKeyGatewayAuthHandler(
            apiKey: null, NullLogger<ApiKeyGatewayAuthHandler>.Instance, _sink);

        var result = await handler.AuthenticateAsync(CreateContext());

        result.IsAuthenticated.ShouldBeTrue();
        _sink.Events.Count.ShouldBe(1);
        var evt = _sink.Events[0];
        evt.Category.ShouldBe(SecurityEventCategory.Auth);
        evt.Action.ShouldBe("gateway.auth.accepted");
        evt.Outcome.ShouldBe(SecurityEventOutcome.Success);
        evt.Control.ShouldBe(SecurityControlFamily.Auth);
        evt.Target!.Kind.ShouldBe(SecurityTargetKind.Gateway);
        evt.Target.Reference.ShouldBe("gateway");
    }

    [Fact]
    public async Task AuthenticateAsync_WithValidKey_EmitsAuthSuccessEvent()
    {
        var handler = new ApiKeyGatewayAuthHandler(
            "secret", NullLogger<ApiKeyGatewayAuthHandler>.Instance, _sink);

        var result = await handler.AuthenticateAsync(
            CreateContext(new Dictionary<string, string> { ["X-Api-Key"] = "secret" }));

        result.IsAuthenticated.ShouldBeTrue();
        _sink.Events.Count.ShouldBe(1);
        _sink.Events[0].Category.ShouldBe(SecurityEventCategory.Auth);
        _sink.Events[0].Outcome.ShouldBe(SecurityEventOutcome.Success);
    }

    // -- Failure: missing credentials ----------------------------------

    [Fact]
    public async Task AuthenticateAsync_MissingKey_EmitsAuthFailureEvent()
    {
        var handler = new ApiKeyGatewayAuthHandler(
            "secret", NullLogger<ApiKeyGatewayAuthHandler>.Instance, _sink);

        var result = await handler.AuthenticateAsync(CreateContext());

        result.IsAuthenticated.ShouldBeFalse();
        _sink.Events.Count.ShouldBe(1);
        var evt = _sink.Events[0];
        evt.Category.ShouldBe(SecurityEventCategory.Auth);
        evt.Action.ShouldBe("gateway.auth.rejected");
        evt.Outcome.ShouldBe(SecurityEventOutcome.Failure);
    }

    // -- Denied: invalid credentials -----------------------------------

    [Fact]
    public async Task AuthenticateAsync_InvalidKey_EmitsAuthFailureEvent()
    {
        var handler = new ApiKeyGatewayAuthHandler(
            "secret", NullLogger<ApiKeyGatewayAuthHandler>.Instance, _sink);

        var result = await handler.AuthenticateAsync(
            CreateContext(new Dictionary<string, string> { ["X-Api-Key"] = "wrong" }));

        result.IsAuthenticated.ShouldBeFalse();
        _sink.Events.Count.ShouldBe(1);
        var evt = _sink.Events[0];
        evt.Category.ShouldBe(SecurityEventCategory.Auth);
        evt.Outcome.ShouldBe(SecurityEventOutcome.Failure);
    }

    // -- Hashed actor --------------------------------------------------

    [Fact]
    public async Task AuthenticateAsync_InvalidKey_HashesPresentedKeyAsActor()
    {
        const string PresentedKey = "super-secret-presented-key-98765";
        var handler = new ApiKeyGatewayAuthHandler(
            "secret", NullLogger<ApiKeyGatewayAuthHandler>.Instance, _sink);

        await handler.AuthenticateAsync(
            CreateContext(new Dictionary<string, string> { ["X-Api-Key"] = PresentedKey }));

        var evt = _sink.Events[0];
        evt.Actor.ShouldNotBeNull();
        evt.Actor!.Id.ShouldNotBe(PresentedKey);
        evt.Actor.Id.ShouldNotContain(PresentedKey);
        evt.Actor.Id.ShouldNotBeNullOrWhiteSpace();
    }

    // -- Non-blocking / never fails the decision -----------------------

    [Fact]
    public async Task AuthenticateAsync_WhenSinkThrows_StillAuthenticates()
    {
        var handler = new ApiKeyGatewayAuthHandler(
            "secret", NullLogger<ApiKeyGatewayAuthHandler>.Instance, new ThrowingSink());

        var result = await handler.AuthenticateAsync(
            CreateContext(new Dictionary<string, string> { ["X-Api-Key"] = "secret" }));

        result.IsAuthenticated.ShouldBeTrue();
    }

    [Fact]
    public async Task AuthenticateAsync_NullSink_DoesNotEmitAndStillWorks()
    {
        var handler = new ApiKeyGatewayAuthHandler(
            "secret", NullLogger<ApiKeyGatewayAuthHandler>.Instance);

        var result = await handler.AuthenticateAsync(
            CreateContext(new Dictionary<string, string> { ["X-Api-Key"] = "secret" }));

        result.IsAuthenticated.ShouldBeTrue();
    }

    private static GatewayAuthContext CreateContext(IReadOnlyDictionary<string, string>? headers = null)
        => new()
        {
            Headers = headers ?? new Dictionary<string, string>(),
            QueryParameters = new Dictionary<string, string>(),
            Path = "/api/messages",
            Method = "POST"
        };

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
