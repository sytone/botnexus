using BotNexus.Gateway.Abstractions.Security;
using BotNexus.Gateway.Api;
using BotNexus.Gateway.Security;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace BotNexus.Gateway.Tests;

/// <summary>
/// Acceptance clauses 5 and 6 of issue #2840: the conversation-message endpoint is guarded by
/// <see cref="GatewayAuthMiddleware"/> with <b>no</b> entry in <c>ShouldSkipAuth</c>, and the
/// middleware's existing per-agent scoping applies to it because the agent id is a route segment.
/// </summary>
/// <remarks>
/// These exercise the REAL middleware and the REAL <see cref="ApiKeyGatewayAuthHandler"/> rather than
/// asserting the absence of a string in <c>ShouldSkipAuth</c>. An allowlist test that only greps source
/// would still pass if the exemption were introduced under a different spelling (a prefix match, a
/// loopback check on <c>RemoteIpAddress</c>); driving the middleware end to end cannot be fooled that
/// way, which matters because this is a write endpoint that can make an agent act.
/// </remarks>
public sealed class ConversationMessagesAuthTests
{
    private const string Route = "/api/agents/pr-doctor/conversations/c_abc/messages";

    /// <summary>
    /// Clause 5: with an API key configured, an unauthenticated POST is rejected with 401 and never
    /// reaches the controller.
    /// </summary>
    [Fact]
    public async Task PostWithoutCredentials_Returns401AndDoesNotReachTheEndpoint()
    {
        var nextCalled = false;
        var middleware = CreateMiddleware(apiKey: "test-key", () => nextCalled = true);
        var context = CreatePostContext();

        await middleware.InvokeAsync(context);

        nextCalled.ShouldBeFalse();
        context.Response.StatusCode.ShouldBe(StatusCodes.Status401Unauthorized);
    }

    /// <summary>
    /// Clause 5: a wrong key is 401 too. Pins that the route is genuinely gated rather than merely
    /// requiring the presence of a header.
    /// </summary>
    [Fact]
    public async Task PostWithWrongApiKey_Returns401()
    {
        var nextCalled = false;
        var middleware = CreateMiddleware(apiKey: "test-key", () => nextCalled = true);
        var context = CreatePostContext();
        context.Request.Headers["X-Api-Key"] = "wrong-key";

        await middleware.InvokeAsync(context);

        nextCalled.ShouldBeFalse();
        context.Response.StatusCode.ShouldBe(StatusCodes.Status401Unauthorized);
    }

    /// <summary>
    /// Clause 5, the other half: a request bearing the configured key is admitted, so the 401 above is
    /// the gate doing its job rather than the route being unreachable for some unrelated reason.
    /// </summary>
    [Theory]
    [InlineData("X-Api-Key", "test-key")]
    [InlineData("Authorization", "Bearer test-key")]
    public async Task PostWithValidCredentials_IsAdmitted(string header, string value)
    {
        var nextCalled = false;
        var middleware = CreateMiddleware(apiKey: "test-key", () => nextCalled = true);
        var context = CreatePostContext();
        context.Request.Headers[header] = value;

        await middleware.InvokeAsync(context);

        nextCalled.ShouldBeTrue();
        context.Response.StatusCode.ShouldBe(StatusCodes.Status200OK);
    }

    /// <summary>
    /// Clause 5, anti-bypass: the request originating from loopback must NOT change the outcome. An
    /// origin-based exemption on an endpoint that can make an agent act is explicitly the wrong trade,
    /// so this pins the absence of one behaviourally.
    /// </summary>
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    public async Task PostFromLoopbackWithoutCredentials_StillReturns401(string address)
    {
        var nextCalled = false;
        var middleware = CreateMiddleware(apiKey: "test-key", () => nextCalled = true);
        var context = CreatePostContext();
        context.Connection.RemoteIpAddress = System.Net.IPAddress.Parse(address);
        context.Connection.LocalIpAddress = System.Net.IPAddress.Parse(address);

        await middleware.InvokeAsync(context);

        nextCalled.ShouldBeFalse();
        context.Response.StatusCode.ShouldBe(StatusCodes.Status401Unauthorized);
    }

    /// <summary>
    /// Clause 6: a key scoped to agent A receives 403 when posting into agent B's conversation. The
    /// scoping is inherited for free because <c>agentId</c> is a route segment the middleware already
    /// extracts — but "for free" is exactly the kind of claim that must be asserted, not assumed.
    /// </summary>
    [Fact]
    public async Task PostToAnotherAgentsConversation_Returns403()
    {
        var nextCalled = false;
        var middleware = CreateMiddleware(
            new StubAuthHandler(new GatewayCallerIdentity
            {
                CallerId = "scoped-caller",
                AllowedAgents = ["agent-a"]
            }),
            () => nextCalled = true);

        var context = CreatePostContext("/api/agents/agent-b/conversations/c_abc/messages");
        context.Request.RouteValues["agentId"] = "agent-b";

        await middleware.InvokeAsync(context);

        nextCalled.ShouldBeFalse();
        context.Response.StatusCode.ShouldBe(StatusCodes.Status403Forbidden);
    }

    /// <summary>
    /// Clause 6, positive control: the same scoped key posting into its OWN agent's conversation is
    /// admitted. Without this the 403 above could pass for a scoped key that is simply always refused.
    /// </summary>
    [Fact]
    public async Task PostToOwnAgentsConversation_IsAdmitted()
    {
        var nextCalled = false;
        var middleware = CreateMiddleware(
            new StubAuthHandler(new GatewayCallerIdentity
            {
                CallerId = "scoped-caller",
                AllowedAgents = ["agent-a"]
            }),
            () => nextCalled = true);

        var context = CreatePostContext("/api/agents/agent-a/conversations/c_abc/messages");
        context.Request.RouteValues["agentId"] = "agent-a";

        await middleware.InvokeAsync(context);

        nextCalled.ShouldBeTrue();
        context.Response.StatusCode.ShouldBe(StatusCodes.Status200OK);
    }

    /// <summary>
    /// Documents the keyless development-mode behaviour the docs warn about (clause 9): with no key
    /// configured the handler allows all requests, so a script that works against a dev gateway will
    /// meet a 401 against a keyed one. Pinned here so the caveat in the docs cannot silently rot.
    /// </summary>
    [Fact]
    public async Task PostWithNoApiKeyConfigured_IsAllowedInDevelopmentMode()
    {
        var nextCalled = false;
        var middleware = CreateMiddleware(apiKey: null, () => nextCalled = true);
        var context = CreatePostContext();

        await middleware.InvokeAsync(context);

        nextCalled.ShouldBeTrue();
        context.Response.StatusCode.ShouldBe(StatusCodes.Status200OK);
    }

    // ── helpers ────────────────────────────────────────────────────────────────────────────────

    private static DefaultHttpContext CreatePostContext(string path = Route)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Path = path;
        context.Request.RouteValues["agentId"] = "pr-doctor";
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static GatewayAuthMiddleware CreateMiddleware(string? apiKey, Action onNext)
        => CreateMiddleware(
            new ApiKeyGatewayAuthHandler(apiKey, NullLogger<ApiKeyGatewayAuthHandler>.Instance),
            onNext);

    private static GatewayAuthMiddleware CreateMiddleware(IGatewayAuthHandler handler, Action onNext)
        => new(
            _ =>
            {
                onNext();
                return Task.CompletedTask;
            },
            handler,
            CreateWebHostEnvironment(),
            NullLogger<GatewayAuthMiddleware>.Instance);

    private static IWebHostEnvironment CreateWebHostEnvironment()
    {
        var webHostEnvironment = new Mock<IWebHostEnvironment>(MockBehavior.Strict);
        webHostEnvironment.SetupGet(environment => environment.WebRootFileProvider).Returns(new NullFileProvider());
        return webHostEnvironment.Object;
    }

    /// <summary>Authenticates every request as a fixed identity, so per-agent scoping can be isolated.</summary>
    private sealed class StubAuthHandler(GatewayCallerIdentity identity) : IGatewayAuthHandler
    {
        public string Scheme => "Stub";

        public Task<GatewayAuthResult> AuthenticateAsync(
            GatewayAuthContext context, CancellationToken cancellationToken = default)
            => Task.FromResult(GatewayAuthResult.Success(identity));
    }
}
