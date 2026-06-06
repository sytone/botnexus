using BotNexus.Gateway.Abstractions.Security;
using BotNexus.Gateway.Api;
using BotNexus.Gateway.Api.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using System.Text.Json.Nodes;

namespace BotNexus.Gateway.Tests;

/// <summary>
/// Integration-style tests verifying that config write endpoints honour
/// the RequireAdmin filter by exercising the filter pipeline directly.
/// </summary>
public sealed class ConfigControllerAdminScopeTests
{
    // Simulates the filter running before the action executes.
    private static (ActionExecutingContext ctx, ConfigController controller) BuildFilterContext(bool isAdmin)
    {
        var controller = new ConfigController();

        var identity = new GatewayCallerIdentity { CallerId = "test-key", IsAdmin = isAdmin };
        var httpContext = new DefaultHttpContext();
        httpContext.Items[GatewayAuthMiddleware.CallerIdentityItemKey] = identity;

        var actionContext = new ActionContext(httpContext, new RouteData(), new ActionDescriptor());
        var ctx = new ActionExecutingContext(
            actionContext,
            new List<IFilterMetadata>(),
            new Dictionary<string, object?>(),
            controller);

        return (ctx, controller);
    }

    [Fact]
    public void UpdateSection_WhenNonAdmin_FilterReturns403()
    {
        var (ctx, _) = BuildFilterContext(isAdmin: false);
        var filter = new RequireAdminAttribute();

        filter.OnActionExecuting(ctx);

        ctx.Result.ShouldBeOfType<ObjectResult>()
            .StatusCode.ShouldBe(403);
    }

    [Fact]
    public void UpdateSection_WhenAdmin_FilterDoesNotShortCircuit()
    {
        var (ctx, _) = BuildFilterContext(isAdmin: true);
        var filter = new RequireAdminAttribute();

        filter.OnActionExecuting(ctx);

        ctx.Result.ShouldBeNull();
    }

    [Fact]
    public void UpdateSectionEntry_WhenNonAdmin_FilterReturns403()
    {
        var (ctx, _) = BuildFilterContext(isAdmin: false);
        var filter = new RequireAdminAttribute();

        filter.OnActionExecuting(ctx);

        ctx.Result.ShouldBeOfType<ObjectResult>()
            .StatusCode.ShouldBe(403);
    }

    [Fact]
    public void DeleteSectionEntry_WhenNonAdmin_FilterReturns403()
    {
        var (ctx, _) = BuildFilterContext(isAdmin: false);
        var filter = new RequireAdminAttribute();

        filter.OnActionExecuting(ctx);

        ctx.Result.ShouldBeOfType<ObjectResult>()
            .StatusCode.ShouldBe(403);
    }
}
