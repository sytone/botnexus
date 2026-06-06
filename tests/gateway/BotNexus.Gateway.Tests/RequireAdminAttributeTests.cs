using BotNexus.Gateway.Abstractions.Security;
using BotNexus.Gateway.Api;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;

namespace BotNexus.Gateway.Tests;

/// <summary>
/// Tests for RequireAdminAttribute — verifies that admin scope check correctly
/// guards config write endpoints.
/// </summary>
public sealed class RequireAdminAttributeTests
{
    // --- helper ---

    private static ActionExecutingContext BuildContext(GatewayCallerIdentity? identity)
    {
        var httpContext = new DefaultHttpContext();
        if (identity is not null)
            httpContext.Items[GatewayAuthMiddleware.CallerIdentityItemKey] = identity;

        var actionContext = new ActionContext(
            httpContext,
            new RouteData(),
            new ActionDescriptor());

        return new ActionExecutingContext(
            actionContext,
            new List<IFilterMetadata>(),
            new Dictionary<string, object?>(),
            controller: new object());
    }

    [Fact]
    public void OnActionExecuting_WhenCallerIsAdmin_DoesNotShortCircuit()
    {
        var identity = new GatewayCallerIdentity { CallerId = "admin-key", IsAdmin = true };
        var ctx = BuildContext(identity);
        var attr = new RequireAdminAttribute();

        attr.OnActionExecuting(ctx);

        ctx.Result.ShouldBeNull(); // Filter did not set a result — request proceeds
    }

    [Fact]
    public void OnActionExecuting_WhenCallerIsNotAdmin_Returns403()
    {
        var identity = new GatewayCallerIdentity { CallerId = "agent-key", IsAdmin = false };
        var ctx = BuildContext(identity);
        var attr = new RequireAdminAttribute();

        attr.OnActionExecuting(ctx);

        var result = ctx.Result.ShouldBeOfType<ObjectResult>();
        result.StatusCode.ShouldBe(403);
    }

    [Fact]
    public void OnActionExecuting_WhenIdentityMissing_Returns403()
    {
        var ctx = BuildContext(identity: null); // Middleware did not set identity
        var attr = new RequireAdminAttribute();

        attr.OnActionExecuting(ctx);

        var result = ctx.Result.ShouldBeOfType<ObjectResult>();
        result.StatusCode.ShouldBe(403);
    }

    [Fact]
    public void OnActionExecuting_ErrorBody_IncludesForbiddenMessage()
    {
        var identity = new GatewayCallerIdentity { CallerId = "agent-key", IsAdmin = false };
        var ctx = BuildContext(identity);
        var attr = new RequireAdminAttribute();

        attr.OnActionExecuting(ctx);

        var result = ctx.Result.ShouldBeOfType<ObjectResult>();
        var body = result.Value?.ToString() ?? string.Empty;
        body.ShouldContain("forbidden", Case.Insensitive);
    }

    [Fact]
    public void OnActionExecuted_IsNoOp()
    {
        var identity = new GatewayCallerIdentity { CallerId = "admin-key", IsAdmin = true };
        var httpContext = new DefaultHttpContext();
        httpContext.Items[GatewayAuthMiddleware.CallerIdentityItemKey] = identity;

        var actionContext = new ActionContext(httpContext, new RouteData(), new ActionDescriptor());
        var executedCtx = new ActionExecutedContext(
            actionContext,
            new List<IFilterMetadata>(),
            controller: new object());

        var attr = new RequireAdminAttribute();
        // Should not throw
        attr.OnActionExecuted(executedCtx);
    }
}
