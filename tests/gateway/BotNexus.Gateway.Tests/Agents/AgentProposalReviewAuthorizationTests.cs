using BotNexus.Gateway.Abstractions.Security;
using BotNexus.Gateway.Api;
using BotNexus.Gateway.Security;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace BotNexus.Gateway.Tests.Agents;

public sealed class AgentProposalReviewAuthorizationTests
{
    private const string Route = "/api/agent-proposals/11111111-1111-1111-1111-111111111111/review";

    [Fact]
    public async Task ReviewWithoutCredentials_IsRejectedBeforeEndpoint()
    {
        var nextCalled = false;
        var middleware = CreateMiddleware(
            new ApiKeyGatewayAuthHandler("admin-key", NullLogger<ApiKeyGatewayAuthHandler>.Instance),
            () => nextCalled = true);
        var context = CreateContext();

        await middleware.InvokeAsync(context);

        nextCalled.ShouldBeFalse();
        context.Response.StatusCode.ShouldBe(StatusCodes.Status401Unauthorized);
    }

    [Fact]
    public async Task ReviewWithAgentScopedIdentity_ReachesEndpointWithScopedProvenance()
    {
        var nextCalled = false;
        var middleware = CreateMiddleware(
            new StubAuthHandler(new GatewayCallerIdentity
            {
                CallerId = "agent-farnsworth",
                AllowedAgents = ["farnsworth"],
                IsAdmin = false,
            }),
            () => nextCalled = true);
        var context = CreateContext();

        await middleware.InvokeAsync(context);

        nextCalled.ShouldBeTrue();
        var identity = context.Items["BotNexus.Gateway.CallerIdentity"]
            .ShouldBeOfType<GatewayCallerIdentity>();
        identity.IsAdmin.ShouldBeFalse();
        identity.AllowedAgents.ShouldBe(["farnsworth"]);
    }

    private static DefaultHttpContext CreateContext()
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Path = Route;
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static GatewayAuthMiddleware CreateMiddleware(IGatewayAuthHandler handler, Action onNext)
        => new(
            _ =>
            {
                onNext();
                return Task.CompletedTask;
            },
            handler,
            CreateEnvironment(),
            NullLogger<GatewayAuthMiddleware>.Instance);

    private static IWebHostEnvironment CreateEnvironment()
    {
        var environment = new Mock<IWebHostEnvironment>(MockBehavior.Strict);
        environment.SetupGet(value => value.WebRootFileProvider).Returns(new NullFileProvider());
        return environment.Object;
    }

    private sealed class StubAuthHandler(GatewayCallerIdentity identity) : IGatewayAuthHandler
    {
        public string Scheme => "Stub";

        public Task<GatewayAuthResult> AuthenticateAsync(
            GatewayAuthContext context,
            CancellationToken cancellationToken = default)
            => Task.FromResult(GatewayAuthResult.Success(identity));
    }
}
