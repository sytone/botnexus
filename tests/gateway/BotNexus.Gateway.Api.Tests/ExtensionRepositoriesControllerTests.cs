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

    private ExtensionRepositoriesController Create(ExtensionCloneOutcome? cloneOutcome = null)
    {
        Directory.CreateDirectory(_root);
        var registry = new ExtensionRepositoryRegistryService(Path.Combine(_root, "config.json"), new FileSystem());
        var source = Directory.CreateDirectory(Path.Combine(_root, "source")).FullName;
        if (cloneOutcome is null)
            return new ExtensionRepositoriesController(registry, ExtensionLifecycleReconciler.CreateDefault(_root, source));

        var reconciler = new ExtensionLifecycleReconciler(
            _root,
            source,
            registry,
            (_, _) => Task.FromResult(cloneOutcome),
            _ => [],
            (_, _, _, _) => Task.FromResult(ExtensionBuildOutcome.Success()),
            _ => new ExtensionDeploymentResult(0, [], []),
            new ExtensionReconciliationFileLock(_root));
        return new ExtensionRepositoriesController(registry, reconciler);
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
        item.SyncAvailable.ShouldBeTrue();
    }

    [Fact]
    public async Task Add_validation_error_returns_bad_request()
    {
        var result = await Create().Add(new("Bad ID", "not-a-url", "", true, true));
        result.Result.ShouldBeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Management_actions_update_toggle_sync_and_remove()
    {
        var controller = Create();
        await controller.Add(new("tools", "https://example.test/tools.git", "main", true, true));
        (await controller.Update("tools", new("https://example.test/tools-v2.git", "release", false))).Result.ShouldBeOfType<OkObjectResult>();
        (await controller.SetEnabled("tools", new(false))).ShouldBeOfType<NoContentResult>();
        var disabled = (((await controller.List()).Result as OkObjectResult)?.Value as IReadOnlyList<ExtensionRepositoryResponse>).ShouldHaveSingleItem();
        disabled.Enabled.ShouldBeFalse();
        disabled.SyncAvailable.ShouldBeFalse();
        (await controller.SyncNow("tools")).ShouldBeOfType<ConflictObjectResult>();
        (await controller.Remove("tools")).ShouldBeOfType<NoContentResult>();
        var items = ((await controller.List()).Result as OkObjectResult)?.Value as IReadOnlyList<ExtensionRepositoryResponse>;
        items.ShouldNotBeNull();
        items.ShouldBeEmpty();
    }
    [Fact]
    public async Task SyncNow_repository_failure_returns_unprocessable_entity_with_named_repository()
    {
        var controller = Create(ExtensionCloneOutcome.Failed("broken-tools", "clone-failed", "repository unavailable"));
        await controller.Add(new("broken-tools", "https://example.test/broken-tools.git", "main", true, true));

        var result = await controller.SyncNow("broken-tools");

        var failure = result.ShouldBeOfType<UnprocessableEntityObjectResult>();
        var body = failure.Value.ShouldNotBeNull().ToString().ShouldNotBeNull();
        body.ShouldContain("broken-tools");
        body.ShouldContain("clone-failed");
    }

}
