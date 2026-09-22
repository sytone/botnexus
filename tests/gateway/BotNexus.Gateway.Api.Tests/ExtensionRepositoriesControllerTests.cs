using BotNexus.Gateway.Api.Controllers;
using BotNexus.Gateway.Configuration;
using Microsoft.AspNetCore.Mvc;
using System.IO.Abstractions;

namespace BotNexus.Gateway.Api.Tests;

public sealed class ExtensionRepositoriesControllerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "botnexus-repo-api-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private ExtensionRepositoriesController Create()
    {
        Directory.CreateDirectory(_root);
        return new ExtensionRepositoriesController(new ExtensionRepositoryRegistryService(Path.Combine(_root, "config.json"), new FileSystem()));
    }

    [Fact]
    public async Task List_returns_empty_then_truthful_unreconciled_projection()
    {
        var controller = Create();
        var empty = ((await controller.List()).Result as OkObjectResult)?.Value as IReadOnlyList<ExtensionRepositoryResponse>;
        empty.ShouldNotBeNull();
        empty.ShouldBeEmpty();

        await controller.Add(new("tools", "https://example.test/tools.git", "main", true, true));
        var item = (((await controller.List()).Result as OkObjectResult)?.Value as IReadOnlyList<ExtensionRepositoryResponse>).ShouldHaveSingleItem();
        item.Id.ShouldBe("tools");
        item.RequestedRef.ShouldBe("main");
        item.ReconciliationStatus.ShouldBe("not-yet-reconciled");
        item.ResolvedCommit.ShouldBeNull();
        item.ClonePath.ShouldBeNull();
        item.LastAttemptUtc.ShouldBeNull();
        item.LastSuccessUtc.ShouldBeNull();
        item.DeployedVersion.ShouldBeNull();
        item.LatestFailure.ShouldBeNull();
        item.SyncAvailable.ShouldBeFalse();
    }

    [Fact]
    public async Task Add_validation_error_returns_bad_request()
    {
        var result = await Create().Add(new("Bad ID", "not-a-url", "", true, true));
        result.Result.ShouldBeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Management_actions_update_toggle_remove_and_sync_is_not_implemented()
    {
        var controller = Create();
        await controller.Add(new("tools", "https://example.test/tools.git", "main", true, true));
        (await controller.Update("tools", new("https://example.test/tools-v2.git", "release", false))).Result.ShouldBeOfType<OkObjectResult>();
        (await controller.SetEnabled("tools", new(false))).ShouldBeOfType<NoContentResult>();
        (await controller.SyncNow("tools")).ShouldBeOfType<ObjectResult>().StatusCode.ShouldBe(501);
        (await controller.Remove("tools")).ShouldBeOfType<NoContentResult>();
        var items = ((await controller.List()).Result as OkObjectResult)?.Value as IReadOnlyList<ExtensionRepositoryResponse>;
        items.ShouldNotBeNull();
        items.ShouldBeEmpty();
    }
}
