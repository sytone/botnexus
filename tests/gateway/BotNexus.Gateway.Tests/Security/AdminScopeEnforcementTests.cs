using BotNexus.Gateway.Abstractions.Security;
using BotNexus.Gateway.Api;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace BotNexus.Gateway.Tests.Security;

/// <summary>
/// Enforcement tests for the admin-scope gate on platform-mutating endpoints (issue #506).
/// </summary>
/// <remarks>
/// <para>
/// #506 asked for an ASP.NET <c>[Authorize(Policy = "AdminScope")]</c> attribute. The gateway has
/// no ASP.NET authentication stack at all -- <c>GatewayAuthMiddleware</c> is the single
/// authentication and authorization seam for every <c>/api/*</c> path, and it already resolves a
/// <see cref="GatewayCallerIdentity"/> carrying an <see cref="GatewayCallerIdentity.IsAdmin"/>
/// flag. Introducing a parallel policy stack would create two competing gates; the admin scope is
/// therefore enforced in the existing middleware against the existing flag, which is the same
/// admin gate <c>SecurityDiagnosticsController</c> already uses.
/// </para>
/// <para>
/// The admin scope is deliberately keyed on the HTTP METHOD as well as the path: reads of
/// <c>/api/config</c> stay open to any authenticated caller (the portal and the CLI both depend on
/// them), while the mutating verbs are admin-only. This is the least-privilege split #506 asks for
/// without breaking the read paths.
/// </para>
/// </remarks>
public sealed class AdminScopeEnforcementTests
{
    // AC5: an agent-scoped (non-admin) session must be refused on every config-write verb.
    [Theory]
    [InlineData("PUT", "/api/config/gateway")]
    [InlineData("PUT", "/api/config/gateway/port")]
    [InlineData("POST", "/api/config/gateway")]
    [InlineData("DELETE", "/api/config/gateway/port")]
    [InlineData("PATCH", "/api/config")]
    public async Task ConfigWrite_WithNonAdminIdentity_Returns403AndDoesNotReachController(string method, string path)
    {
        var nextCalled = false;
        var middleware = CreateMiddleware(NonAdminIdentity(), () => nextCalled = true);

        var context = CreateContext(method, path);
        await middleware.InvokeAsync(context);

        context.Response.StatusCode.ShouldBe(StatusCodes.Status403Forbidden);
        nextCalled.ShouldBeFalse();
    }

    // AC6: an admin session (human portal / admin key) must pass through to the controller.
    [Theory]
    [InlineData("PUT", "/api/config/gateway")]
    [InlineData("PUT", "/api/config/gateway/port")]
    [InlineData("POST", "/api/config/gateway")]
    [InlineData("DELETE", "/api/config/gateway/port")]
    [InlineData("PATCH", "/api/config")]
    public async Task ConfigWrite_WithAdminIdentity_ReachesController(string method, string path)
    {
        var nextCalled = false;
        var middleware = CreateMiddleware(AdminIdentity(), () => nextCalled = true);

        var context = CreateContext(method, path);
        await middleware.InvokeAsync(context);

        nextCalled.ShouldBeTrue();
        context.Response.StatusCode.ShouldBe(StatusCodes.Status200OK);
    }

    // Reads must stay open to any authenticated caller: the gate is scoped to mutating verbs only.
    [Theory]
    [InlineData("/api/config")]
    [InlineData("/api/config/raw")]
    [InlineData("/api/config/schema")]
    [InlineData("/api/config/validate")]
    public async Task ConfigRead_WithNonAdminIdentity_IsStillAllowed(string path)
    {
        var nextCalled = false;
        var middleware = CreateMiddleware(NonAdminIdentity(), () => nextCalled = true);

        var context = CreateContext("GET", path);
        await middleware.InvokeAsync(context);

        nextCalled.ShouldBeTrue();
        context.Response.StatusCode.ShouldBe(StatusCodes.Status200OK);
    }

    // The gate must not leak onto unrelated write paths -- an agent posting a chat message is not
    // performing platform administration.
    [Fact]
    public async Task NonAdminWrite_ToNonAdminPath_IsAllowed()
    {
        var nextCalled = false;
        var middleware = CreateMiddleware(NonAdminIdentity(), () => nextCalled = true);

        var context = CreateContext("POST", "/api/chat");
        await middleware.InvokeAsync(context);

        nextCalled.ShouldBeTrue();
        context.Response.StatusCode.ShouldBe(StatusCodes.Status200OK);
    }

    // A path that merely starts with the same characters must not be captured by the gate.
    [Fact]
    public async Task NonAdminWrite_ToPathWithAdminPrefixButDifferentSegment_IsAllowed()
    {
        var nextCalled = false;
        var middleware = CreateMiddleware(NonAdminIdentity(), () => nextCalled = true);

        var context = CreateContext("POST", "/api/configuration-notes");
        await middleware.InvokeAsync(context);

        nextCalled.ShouldBeTrue();
        context.Response.StatusCode.ShouldBe(StatusCodes.Status200OK);
    }

    // AC3: the keyless/dev identity the human portal runs under carries IsAdmin, so the portal
    // satisfies admin scope by default with no extra configuration.
    [Fact]
    public async Task DevModeIdentity_SatisfiesAdminScopeByDefault()
    {
        var devIdentity = new GatewayCallerIdentity
        {
            CallerId = "gateway-dev",
            DisplayName = "Gateway Development Caller",
            TenantId = "development",
            Permissions = ["*"],
            IsAdmin = true
        };

        var nextCalled = false;
        var middleware = CreateMiddleware(devIdentity, () => nextCalled = true);

        var context = CreateContext("PUT", "/api/config/gateway");
        await middleware.InvokeAsync(context);

        nextCalled.ShouldBeTrue();
    }

    // AC4: an agent-scoped key is non-admin unless the operator explicitly sets isAdmin, even when
    // the request targets an agent that key is allowed to act for.
    [Fact]
    public async Task AgentScopedIdentity_DoesNotSatisfyAdminScope_EvenForItsOwnAgent()
    {
        var identity = new GatewayCallerIdentity
        {
            CallerId = "gateway-key:agent-key",
            DisplayName = "Agent Key",
            TenantId = "default",
            AllowedAgents = ["farnsworth"],
            IsAdmin = false
        };

        var nextCalled = false;
        var middleware = CreateMiddleware(identity, () => nextCalled = true);

        var context = CreateContext("PUT", "/api/config/gateway");
        context.Request.QueryString = new QueryString("?agent=farnsworth");
        await middleware.InvokeAsync(context);

        context.Response.StatusCode.ShouldBe(StatusCodes.Status403Forbidden);
        nextCalled.ShouldBeFalse();
    }

    // AC4 (grant path): an explicitly granted admin key does satisfy the scope.
    [Fact]
    public async Task ExplicitlyGrantedAdminKey_SatisfiesAdminScope()
    {
        var identity = new GatewayCallerIdentity
        {
            CallerId = "gateway-key:ops",
            DisplayName = "Ops Key",
            TenantId = "default",
            IsAdmin = true
        };

        var nextCalled = false;
        var middleware = CreateMiddleware(identity, () => nextCalled = true);

        var context = CreateContext("DELETE", "/api/config/gateway/port");
        await middleware.InvokeAsync(context);

        nextCalled.ShouldBeTrue();
    }

    // The denial must be an authorization refusal, not an authentication one: the caller IS
    // authenticated, so a 401 would send them off to re-present credentials that cannot help.
    [Fact]
    public async Task AdminScopeDenial_IsForbiddenNotUnauthenticated()
    {
        var middleware = CreateMiddleware(NonAdminIdentity(), () => { });

        var context = CreateContext("PUT", "/api/config/gateway");
        await middleware.InvokeAsync(context);

        context.Response.StatusCode.ShouldNotBe(StatusCodes.Status401Unauthorized);
        context.Response.StatusCode.ShouldBe(StatusCodes.Status403Forbidden);

        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body);
        var payload = await reader.ReadToEndAsync();
        payload.ShouldContain("forbidden");
    }

    private static GatewayCallerIdentity NonAdminIdentity() => new()
    {
        CallerId = "gateway-key:restricted",
        DisplayName = "Restricted Agent Key",
        TenantId = "default",
        IsAdmin = false
    };

    private static GatewayCallerIdentity AdminIdentity() => new()
    {
        CallerId = "gateway-key:admin",
        DisplayName = "Admin Key",
        TenantId = "default",
        IsAdmin = true
    };

    private static DefaultHttpContext CreateContext(string method, string path)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = path;
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static GatewayAuthMiddleware CreateMiddleware(GatewayCallerIdentity identity, Action onNext)
    {
        var handler = new Mock<IGatewayAuthHandler>();
        handler.SetupGet(h => h.Scheme).Returns("Stub");
        handler
            .Setup(h => h.AuthenticateAsync(It.IsAny<GatewayAuthContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(GatewayAuthResult.Success(identity));

        var webHostEnvironment = new Mock<IWebHostEnvironment>(MockBehavior.Strict);
        webHostEnvironment.SetupGet(e => e.WebRootFileProvider).Returns(new NullFileProvider());

        return new GatewayAuthMiddleware(
            _ =>
            {
                onNext();
                return Task.CompletedTask;
            },
            handler.Object,
            webHostEnvironment.Object,
            NullLogger<GatewayAuthMiddleware>.Instance);
    }
}
