using BotNexus.Gateway.Abstractions.Security;
using BotNexus.Gateway.Api;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using Microsoft.FeatureManagement;
using Moq;

namespace BotNexus.Gateway.Tests.Security;

/// <summary>
/// Behavioural coverage for gateway permission enforcement (#2621).
/// <para>
/// Every assertion here is on the OBSERVABLE outcome of a request through the middleware - the
/// status code, the response payload, whether the next delegate ran, and what was logged. None of
/// them call a boolean helper and assert it returned false, because that is precisely the shape of
/// test that let <c>Permissions</c> be "covered" while never actually gating anything.
/// </para>
/// </summary>
public sealed class GatewayPermissionEnforcementTests
{
    [Fact]
    public async Task EnforcementEnabled_CallerWithoutRequiredScope_RequestIsRefusedWith403AndNextNeverRuns()
    {
        var nextCalled = false;
        var context = CreateContext("/api/agents", HttpMethods.Get);

        var middleware = CreateMiddleware(
            identity: Identity("narrow-caller", "sessions:read"),
            enforcementEnabled: true,
            next: _ => { nextCalled = true; return Task.CompletedTask; });

        await middleware.InvokeAsync(context);

        nextCalled.ShouldBeFalse("an under-scoped caller must never reach the endpoint");
        context.Response.StatusCode.ShouldBe(StatusCodes.Status403Forbidden);
        (await ReadBodyAsync(context)).ShouldContain("\"error\":\"permission_denied\"");
    }

    [Fact]
    public async Task EnforcementEnabled_CallerWithWildcard_RetainsFullAccess()
    {
        var nextCalled = false;
        var context = CreateContext("/api/agents", HttpMethods.Post);

        var middleware = CreateMiddleware(
            identity: Identity("operator", GatewayScopes.Wildcard),
            enforcementEnabled: true,
            next: _ => { nextCalled = true; return Task.CompletedTask; });

        await middleware.InvokeAsync(context);

        nextCalled.ShouldBeTrue();
        context.Response.StatusCode.ShouldBe(StatusCodes.Status200OK);
    }

    [Fact]
    public async Task EnforcementEnabled_CallerWithExactlyTheRequiredScope_IsAllowed()
    {
        var nextCalled = false;
        var context = CreateContext("/api/sessions", HttpMethods.Get);

        var middleware = CreateMiddleware(
            identity: Identity("reader", "sessions:read"),
            enforcementEnabled: true,
            next: _ => { nextCalled = true; return Task.CompletedTask; });

        await middleware.InvokeAsync(context);

        nextCalled.ShouldBeTrue();
        context.Response.StatusCode.ShouldBe(StatusCodes.Status200OK);
    }

    [Fact]
    public async Task EnforcementEnabled_ReadScopeDoesNotGrantWrite()
    {
        var nextCalled = false;
        var context = CreateContext("/api/sessions", HttpMethods.Delete);

        var middleware = CreateMiddleware(
            identity: Identity("reader", "sessions:read"),
            enforcementEnabled: true,
            next: _ => { nextCalled = true; return Task.CompletedTask; });

        await middleware.InvokeAsync(context);

        nextCalled.ShouldBeFalse("a read-only key must not be able to mutate");
        context.Response.StatusCode.ShouldBe(StatusCodes.Status403Forbidden);
    }

    [Fact]
    public async Task EnforcementEnabled_UnknownScopeGrantsNothing_AndTheRequestIsRefused()
    {
        // Constraint 3: fail CLOSED. A permission outside the vocabulary must not be treated as a
        // wildcard, nor silently skipped in a way that leaves the caller authorized by default.
        var nextCalled = false;
        var context = CreateContext("/api/agents", HttpMethods.Get);

        var middleware = CreateMiddleware(
            identity: Identity("typo-caller", "agents:reed", "definitely-not-a-scope"),
            enforcementEnabled: true,
            next: _ => { nextCalled = true; return Task.CompletedTask; });

        await middleware.InvokeAsync(context);

        nextCalled.ShouldBeFalse();
        context.Response.StatusCode.ShouldBe(StatusCodes.Status403Forbidden);
    }

    [Fact]
    public async Task EnforcementEnabled_UnmappedAuthenticatedPath_IsRefusedRatherThanWavedThrough()
    {
        var nextCalled = false;
        var context = CreateContext("/api/brand-new-surface", HttpMethods.Get);

        var middleware = CreateMiddleware(
            identity: Identity("scoped", "agents:read"),
            enforcementEnabled: true,
            next: _ => { nextCalled = true; return Task.CompletedTask; });

        await middleware.InvokeAsync(context);

        nextCalled.ShouldBeFalse("an unmapped path must fail closed, not bypass the check");
        context.Response.StatusCode.ShouldBe(StatusCodes.Status403Forbidden);
    }

    [Fact]
    public async Task EnforcementEnabled_Denial_LogsWarningNamingCallerAndRefusedScope()
    {
        var logger = new CapturingLogger();
        var context = CreateContext("/api/agents", HttpMethods.Get);

        var middleware = CreateMiddleware(
            identity: Identity("narrow-caller", "sessions:read"),
            enforcementEnabled: true,
            next: _ => Task.CompletedTask,
            logger: logger);

        await middleware.InvokeAsync(context);

        var warning = logger.Entries
            .Where(entry => entry.Level == LogLevel.Warning)
            .Select(entry => entry.Message)
            .FirstOrDefault(message => message.Contains("permission denied", StringComparison.OrdinalIgnoreCase));

        warning.ShouldNotBeNull("denial must be observable in the log, not silent");
        warning.ShouldContain("narrow-caller");
        warning.ShouldContain("agents:read");
    }

    [Fact]
    public async Task EnforcementDisabled_UnderScopedCallerIsStillServed_ButTheWouldBeDenialIsLogged()
    {
        // Constraint 1: the default posture must not break a live deployment, and must not be
        // silently permissive either. Off means "audit", not "skip".
        var nextCalled = false;
        var logger = new CapturingLogger();
        var context = CreateContext("/api/agents", HttpMethods.Get);

        var middleware = CreateMiddleware(
            identity: Identity("satellite:sat_desktop", GatewayScopes.SatelliteConnect, GatewayScopes.SatelliteHeartbeat),
            enforcementEnabled: false,
            next: _ => { nextCalled = true; return Task.CompletedTask; },
            logger: logger);

        await middleware.InvokeAsync(context);

        nextCalled.ShouldBeTrue("enforcement defaults off so existing callers keep working");
        context.Response.StatusCode.ShouldBe(StatusCodes.Status200OK);

        var audit = logger.Entries
            .Where(entry => entry.Level == LogLevel.Warning)
            .Select(entry => entry.Message)
            .FirstOrDefault(message => message.Contains("permission audit", StringComparison.OrdinalIgnoreCase));

        audit.ShouldNotBeNull("the would-be denial must be recorded so operators can size the change");
        audit.ShouldContain("satellite:sat_desktop");
        audit.ShouldContain("agents:read");
    }

    [Fact]
    public async Task EnforcementEnabled_SatelliteConnectScope_AuthorizesTheHubUpgrade()
    {
        // AC3: the satellite identity's two scopes must remain sufficient for what a satellite
        // legitimately does. Its live connection is the hub, not a REST resource route.
        var nextCalled = false;
        var context = CreateContext("/hub/gateway", "WS");

        var middleware = CreateMiddleware(
            identity: Identity("satellite:sat_desktop", GatewayScopes.SatelliteConnect, GatewayScopes.SatelliteHeartbeat),
            enforcementEnabled: true,
            next: _ => { nextCalled = true; return Task.CompletedTask; });

        await middleware.InvokeAsync(context);

        nextCalled.ShouldBeTrue();
        context.Response.StatusCode.ShouldBe(StatusCodes.Status200OK);
    }

    [Fact]
    public async Task EnforcementEnabled_EmptyPermissions_IsRefused()
    {
        var nextCalled = false;
        var context = CreateContext("/api/config", HttpMethods.Get);

        var middleware = CreateMiddleware(
            identity: Identity("no-scopes"),
            enforcementEnabled: true,
            next: _ => { nextCalled = true; return Task.CompletedTask; });

        await middleware.InvokeAsync(context);

        nextCalled.ShouldBeFalse();
        context.Response.StatusCode.ShouldBe(StatusCodes.Status403Forbidden);
    }

    [Fact]
    public async Task FeatureFlagEvaluationThrows_RequestIsServed_AndTheFaultIsLogged()
    {
        var nextCalled = false;
        var logger = new CapturingLogger();
        var context = CreateContext("/api/agents", HttpMethods.Get);

        var featureManager = new Mock<IFeatureManager>();
        featureManager
            .Setup(manager => manager.IsEnabledAsync(GatewayAuthMiddleware.PermissionEnforcementFeature))
            .ThrowsAsync(new InvalidOperationException("provider down"));

        var middleware = new GatewayAuthMiddleware(
            _ => { nextCalled = true; return Task.CompletedTask; },
            CreateAuthHandler(Identity("narrow-caller", "sessions:read")),
            CreateWebHostEnvironment(),
            logger,
            featureManager.Object);

        await middleware.InvokeAsync(context);

        nextCalled.ShouldBeTrue("a faulted flag provider must not lock an operator out");
        context.Response.StatusCode.ShouldBe(StatusCodes.Status200OK);
        logger.Entries.ShouldContain(entry =>
            entry.Level == LogLevel.Warning &&
            entry.Message.Contains("Failed to evaluate feature flag", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("/api/agents", "GET", "agents:read")]
    [InlineData("/api/agents/foo/workspace", "POST", "agents:write")]
    [InlineData("/api/nav-order", "PUT", "nav-order:write")]
    [InlineData("/api/sessions", "HEAD", "sessions:read")]
    [InlineData("/api/unknown-thing", "GET", null)]
    [InlineData("/hub/gateway", "WS", "satellite:connect")]
    public void Resolve_MapsPathAndMethodOntoTheSingleScopeVocabulary(string path, string method, string? expected)
    {
        GatewayScopes.Resolve(path, method).ShouldBe(expected);
    }

    [Fact]
    public void EveryResolvableScope_IsAMemberOfTheDeclaredVocabulary()
    {
        // A scope Resolve can produce but All does not contain would be permanently unsatisfiable:
        // IsAuthorized rejects unknown permissions, so no operator could ever grant it.
        foreach (var resource in GatewayScopes.Resources)
        {
            GatewayScopes.All.ShouldContain($"{resource}:{GatewayScopes.ReadAccess}");
            GatewayScopes.All.ShouldContain($"{resource}:{GatewayScopes.WriteAccess}");
        }

        GatewayScopes.All.ShouldContain(GatewayScopes.SatelliteConnect);
        GatewayScopes.All.ShouldContain(GatewayScopes.SatelliteHeartbeat);
        GatewayScopes.All.ShouldContain(GatewayScopes.Wildcard);
    }

    private static GatewayCallerIdentity Identity(string callerId, params string[] permissions)
        => new() { CallerId = callerId, Permissions = permissions };

    private static DefaultHttpContext CreateContext(string path, string method)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Request.Method = method == "WS" ? HttpMethods.Get : method;
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static async Task<string> ReadBodyAsync(HttpContext context)
    {
        context.Response.Body.Position = 0;
        return await new StreamReader(context.Response.Body).ReadToEndAsync();
    }

    private static GatewayAuthMiddleware CreateMiddleware(
        GatewayCallerIdentity identity,
        bool enforcementEnabled,
        RequestDelegate next,
        ILogger<GatewayAuthMiddleware>? logger = null)
    {
        var featureManager = new Mock<IFeatureManager>();
        featureManager
            .Setup(manager => manager.IsEnabledAsync(GatewayAuthMiddleware.PermissionEnforcementFeature))
            .ReturnsAsync(enforcementEnabled);

        return new GatewayAuthMiddleware(
            next,
            CreateAuthHandler(identity),
            CreateWebHostEnvironment(),
            logger ?? new CapturingLogger(),
            featureManager.Object);
    }

    private static IGatewayAuthHandler CreateAuthHandler(GatewayCallerIdentity identity)
    {
        var handler = new Mock<IGatewayAuthHandler>();
        handler
            .Setup(h => h.AuthenticateAsync(It.IsAny<GatewayAuthContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(GatewayAuthResult.Success(identity));
        return handler.Object;
    }

    private static IWebHostEnvironment CreateWebHostEnvironment()
    {
        var webHostEnvironment = new Mock<IWebHostEnvironment>(MockBehavior.Strict);
        webHostEnvironment.SetupGet(environment => environment.WebRootFileProvider).Returns(new NullFileProvider());
        return webHostEnvironment.Object;
    }

    /// <summary>Records log entries so denial observability can be asserted, not assumed.</summary>
    private sealed class CapturingLogger : ILogger<GatewayAuthMiddleware>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
    }
}
