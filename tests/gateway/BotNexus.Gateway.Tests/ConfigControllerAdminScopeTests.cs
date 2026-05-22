using BotNexus.Gateway.Abstractions.Security;
using BotNexus.Gateway.Api;
using BotNexus.Gateway.Api.Controllers;
using BotNexus.Gateway.Configuration;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json.Nodes;

namespace BotNexus.Gateway.Tests;

/// <summary>
/// Tests that config write endpoints enforce admin scope.
/// </summary>
public sealed class ConfigControllerAdminScopeTests
{
    private static ConfigController CreateController(GatewayCallerIdentity? identity)
    {
        var controller = new ConfigController();
        var httpContext = new DefaultHttpContext();
        if (identity is not null)
            httpContext.Items[GatewayAuthMiddleware.CallerIdentityItemKey] = identity;
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = httpContext
        };
        return controller;
    }

    private static PlatformConfigWriter CreateWriter()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"bn-admin-scope-{Guid.NewGuid():N}.json");
        File.WriteAllText(tempPath, "{}");
        return new PlatformConfigWriter(tempPath, new System.IO.Abstractions.FileSystem());
    }

    private static GatewayCallerIdentity AdminIdentity() => new()
    {
        CallerId = "admin-key",
        IsAdmin = true
    };

    private static GatewayCallerIdentity NonAdminIdentity() => new()
    {
        CallerId = "agent-key",
        IsAdmin = false
    };

    // --- UpdateSection ---

    [Fact]
    public async Task UpdateSection_WhenCallerIsAdmin_Returns200()
    {
        var controller = CreateController(AdminIdentity());
        var writer = CreateWriter();

        var result = await controller.UpdateSection("gateway", new JsonObject(), writer, CancellationToken.None);

        result.ShouldBeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task UpdateSection_WhenCallerIsNotAdmin_Returns403()
    {
        var controller = CreateController(NonAdminIdentity());
        var writer = CreateWriter();

        var result = await controller.UpdateSection("gateway", new JsonObject(), writer, CancellationToken.None);

        result.ShouldBeOfType<ObjectResult>().StatusCode.ShouldBe(StatusCodes.Status403Forbidden);
    }

    [Fact]
    public async Task UpdateSection_WhenNoIdentityPresent_Returns200()
    {
        // No identity = dev mode / auth disabled — allow through
        var controller = CreateController(identity: null);
        var writer = CreateWriter();

        var result = await controller.UpdateSection("gateway", new JsonObject(), writer, CancellationToken.None);

        result.ShouldBeOfType<OkObjectResult>();
    }

    // --- UpdateSectionEntry ---

    [Fact]
    public async Task UpdateSectionEntry_WhenCallerIsNotAdmin_Returns403()
    {
        var controller = CreateController(NonAdminIdentity());
        var writer = CreateWriter();

        var result = await controller.UpdateSectionEntry("providers", "my-provider", new JsonObject(), writer, CancellationToken.None);

        result.ShouldBeOfType<ObjectResult>().StatusCode.ShouldBe(StatusCodes.Status403Forbidden);
    }

    [Fact]
    public async Task UpdateSectionEntry_WhenCallerIsAdmin_Returns200()
    {
        var controller = CreateController(AdminIdentity());
        var writer = CreateWriter();

        var result = await controller.UpdateSectionEntry("providers", "my-provider", new JsonObject(), writer, CancellationToken.None);

        result.ShouldBeOfType<OkObjectResult>();
    }

    // --- DeleteSectionEntry ---

    [Fact]
    public async Task DeleteSectionEntry_WhenCallerIsNotAdmin_Returns403()
    {
        var controller = CreateController(NonAdminIdentity());
        var writer = CreateWriter();

        var result = await controller.DeleteSectionEntry("providers", "my-provider", writer, CancellationToken.None);

        result.ShouldBeOfType<ObjectResult>().StatusCode.ShouldBe(StatusCodes.Status403Forbidden);
    }

    [Fact]
    public async Task DeleteSectionEntry_WhenCallerIsAdmin_Returns200()
    {
        var controller = CreateController(AdminIdentity());
        var writer = CreateWriter();

        // Entry doesn't exist — that's OK, just verify the 200 path (writer will no-op)
        var result = await controller.DeleteSectionEntry("providers", "missing-provider", writer, CancellationToken.None);

        result.ShouldBeOfType<OkObjectResult>();
    }
}
