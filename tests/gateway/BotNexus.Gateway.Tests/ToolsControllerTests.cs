using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Api.Controllers;
using BotNexus.Gateway.Tools;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotNexus.Gateway.Tests;

public sealed class ToolsControllerTests
{
    private static ToolsController CreateController(IToolStore store)
        => new(store, NullLogger<ToolsController>.Instance);

    private static ToolDefinition CreateTool(string id, string name = "Tool", int order = 0)
        => new()
        {
            Id = ToolId.From(id),
            Name = name,
            Url = "https://example.com/" + id,
            Icon = "??",
            Order = order,
            SandboxEnabled = true
        };

    [Fact]
    public async Task List_ReturnsAllTools()
    {
        var store = new FakeToolStore();
        await store.CreateAsync(CreateTool("tool-1", order: 1));
        await store.CreateAsync(CreateTool("tool-2", order: 2));
        var controller = CreateController(store);

        var result = await controller.List(CancellationToken.None);

        var tools = (result.Result as OkObjectResult)?.Value as IReadOnlyList<ToolDefinition>;
        tools.ShouldNotBeNull();
        tools!.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Get_ReturnsTool_WhenPresent()
    {
        var store = new FakeToolStore();
        await store.CreateAsync(CreateTool("tool-1"));
        var controller = CreateController(store);

        var result = await controller.Get("tool-1", CancellationToken.None);

        var tool = (result.Result as OkObjectResult)?.Value as ToolDefinition;
        tool.ShouldNotBeNull();
        tool!.Id.Value.ShouldBe("tool-1");
    }

    [Fact]
    public async Task Get_ReturnsNotFound_WhenMissing()
    {
        var controller = CreateController(new FakeToolStore());

        var result = await controller.Get("missing", CancellationToken.None);

        result.Result.ShouldBeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public async Task Create_ReturnsCreated_AndPersists()
    {
        var store = new FakeToolStore();
        var controller = CreateController(store);

        var result = await controller.Create(CreateTool("tool-new", "New"), CancellationToken.None);

        var created = (result.Result as CreatedAtActionResult)?.Value as ToolDefinition;
        created.ShouldNotBeNull();
        created!.Id.Value.ShouldBe("tool-new");
        (await store.GetAsync(ToolId.From("tool-new"))).ShouldNotBeNull();
    }

    [Fact]
    public async Task Update_ModifiesExisting_AndPreservesCreatedAt()
    {
        var store = new FakeToolStore();
        var original = await store.CreateAsync(CreateTool("tool-1", "Original"));
        var controller = CreateController(store);

        var request = CreateTool("ignored-id", "Renamed", order: 7) with { SandboxEnabled = false };
        var result = await controller.Update("tool-1", request, CancellationToken.None);

        var updated = (result.Result as OkObjectResult)?.Value as ToolDefinition;
        updated.ShouldNotBeNull();
        updated!.Id.Value.ShouldBe("tool-1");
        updated.Name.ShouldBe("Renamed");
        updated.Order.ShouldBe(7);
        updated.SandboxEnabled.ShouldBeFalse();
        updated.CreatedAt.ShouldBe(original.CreatedAt);
    }

    [Fact]
    public async Task Update_ReturnsNotFound_WhenMissing()
    {
        var controller = CreateController(new FakeToolStore());

        var result = await controller.Update("missing", CreateTool("missing"), CancellationToken.None);

        result.Result.ShouldBeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public async Task Delete_ReturnsNoContent_AndRemoves()
    {
        var store = new FakeToolStore();
        await store.CreateAsync(CreateTool("tool-1"));
        var controller = CreateController(store);

        var result = await controller.Delete("tool-1", CancellationToken.None);

        result.ShouldBeOfType<NoContentResult>();
        (await store.GetAsync(ToolId.From("tool-1"))).ShouldBeNull();
    }

    [Fact]
    public async Task Delete_ReturnsNotFound_WhenMissing()
    {
        var controller = CreateController(new FakeToolStore());

        var result = await controller.Delete("missing", CancellationToken.None);

        result.ShouldBeOfType<NotFoundObjectResult>();
    }

    private sealed class FakeToolStore : IToolStore
    {
        private readonly Dictionary<string, ToolDefinition> _tools = new(StringComparer.OrdinalIgnoreCase);

        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task<IReadOnlyList<ToolDefinition>> ListAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ToolDefinition>>(
                _tools.Values.OrderBy(t => t.Order).ThenBy(t => t.CreatedAt).ToList());

        public Task<ToolDefinition?> GetAsync(ToolId id, CancellationToken ct = default)
            => Task.FromResult(_tools.TryGetValue(id.Value, out var tool) ? tool : null);

        public Task<ToolDefinition> CreateAsync(ToolDefinition tool, CancellationToken ct = default)
        {
            var created = tool with { CreatedAt = tool.CreatedAt == default ? DateTimeOffset.UtcNow : tool.CreatedAt };
            _tools[created.Id.Value] = created;
            return Task.FromResult(created);
        }

        public Task<ToolDefinition> UpdateAsync(ToolDefinition tool, CancellationToken ct = default)
        {
            _tools[tool.Id.Value] = tool;
            return Task.FromResult(tool);
        }

        public Task DeleteAsync(ToolId id, CancellationToken ct = default)
        {
            _tools.Remove(id.Value);
            return Task.CompletedTask;
        }
    }
}
