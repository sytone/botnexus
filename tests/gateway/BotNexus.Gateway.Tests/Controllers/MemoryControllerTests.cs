using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Api.Controllers;
using BotNexus.Memory;
using BotNexus.Memory.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace BotNexus.Gateway.Tests.Controllers;

public sealed class MemoryControllerTests
{
    private static readonly AgentDescriptor AgentWithMemory = new()
    {
        AgentId = AgentId.From("test-agent"),
        DisplayName = "Test Agent",
        ApiProvider = "test",
        ModelId = "test-model",
        Memory = new MemoryAgentConfig { Enabled = true }
    };

    private static readonly AgentDescriptor AgentWithoutMemory = new()
    {
        AgentId = AgentId.From("no-memory"),
        DisplayName = "No Memory",
        ApiProvider = "test",
        ModelId = "test-model",
        Memory = new MemoryAgentConfig { Enabled = false }
    };

    [Fact]
    public async Task ListMemoryStores_ReturnsStatsForEnabledAgents()
    {
        var registry = new Mock<IAgentRegistry>();
        registry.Setup(r => r.GetAll()).Returns([AgentWithMemory, AgentWithoutMemory]);

        var store = new Mock<IMemoryStore>();
        store.Setup(s => s.InitializeAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        store.Setup(s => s.GetStatsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MemoryStoreStats(42, 1024, DateTimeOffset.UtcNow));

        var factory = new Mock<IMemoryStoreFactory>();
        factory.Setup(f => f.Create("test-agent")).Returns(store.Object);

        var controller = new MemoryController(registry.Object, factory.Object, NullLogger<MemoryController>.Instance);
        var result = await controller.ListMemoryStores(CancellationToken.None);

        var ok = result.ShouldBeOfType<OkObjectResult>();
        var list = ok.Value as IEnumerable<object>;
        list.ShouldNotBeNull();
    }

    [Fact]
    public async Task GetMemoryStore_ReturnsStatsForValidAgent()
    {
        var registry = new Mock<IAgentRegistry>();
        registry.Setup(r => r.Get(AgentId.From("test-agent"))).Returns(AgentWithMemory);

        var store = new Mock<IMemoryStore>();
        store.Setup(s => s.InitializeAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        store.Setup(s => s.GetStatsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MemoryStoreStats(10, 512, null));

        var factory = new Mock<IMemoryStoreFactory>();
        factory.Setup(f => f.Create("test-agent")).Returns(store.Object);

        var controller = new MemoryController(registry.Object, factory.Object, NullLogger<MemoryController>.Instance);
        var result = await controller.GetMemoryStore("test-agent", CancellationToken.None);

        result.ShouldBeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task GetMemoryStore_Returns404ForUnknownAgent()
    {
        var registry = new Mock<IAgentRegistry>();
        registry.Setup(r => r.Get(It.IsAny<AgentId>())).Returns((AgentDescriptor?)null);

        var factory = new Mock<IMemoryStoreFactory>();
        var controller = new MemoryController(registry.Object, factory.Object, NullLogger<MemoryController>.Instance);
        var result = await controller.GetMemoryStore("unknown", CancellationToken.None);

        result.ShouldBeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public async Task GetMemoryStore_Returns404WhenMemoryDisabled()
    {
        var registry = new Mock<IAgentRegistry>();
        registry.Setup(r => r.Get(AgentId.From("no-memory"))).Returns(AgentWithoutMemory);

        var factory = new Mock<IMemoryStoreFactory>();
        var controller = new MemoryController(registry.Object, factory.Object, NullLogger<MemoryController>.Instance);
        var result = await controller.GetMemoryStore("no-memory", CancellationToken.None);

        result.ShouldBeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public async Task SearchEntries_ReturnsBadRequestWhenQueryMissing()
    {
        var registry = new Mock<IAgentRegistry>();
        registry.Setup(r => r.Get(AgentId.From("test-agent"))).Returns(AgentWithMemory);

        var factory = new Mock<IMemoryStoreFactory>();
        var controller = new MemoryController(registry.Object, factory.Object, NullLogger<MemoryController>.Instance);
        var result = await controller.SearchEntries("test-agent", query: null);

        result.ShouldBeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task SearchEntries_ReturnsMatchingEntries()
    {
        var registry = new Mock<IAgentRegistry>();
        registry.Setup(r => r.Get(AgentId.From("test-agent"))).Returns(AgentWithMemory);

        var entries = new List<MemoryEntry>
        {
            new()
            {
                Id = "entry-1",
                AgentId = "test-agent",
                SourceType = "conversation",
                Content = "Found something relevant about BotNexus architecture",
                CreatedAt = DateTimeOffset.UtcNow.AddHours(-1)
            }
        };

        var store = new Mock<IMemoryStore>();
        store.Setup(s => s.InitializeAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        store.Setup(s => s.SearchAsync("architecture", 20, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(entries);

        var factory = new Mock<IMemoryStoreFactory>();
        factory.Setup(f => f.Create("test-agent")).Returns(store.Object);

        var controller = new MemoryController(registry.Object, factory.Object, NullLogger<MemoryController>.Instance);
        var result = await controller.SearchEntries("test-agent", query: "architecture");

        result.ShouldBeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task SearchEntries_Returns404ForUnknownAgent()
    {
        var registry = new Mock<IAgentRegistry>();
        registry.Setup(r => r.Get(It.IsAny<AgentId>())).Returns((AgentDescriptor?)null);

        var factory = new Mock<IMemoryStoreFactory>();
        var controller = new MemoryController(registry.Object, factory.Object, NullLogger<MemoryController>.Instance);
        var result = await controller.SearchEntries("unknown", query: "test");

        result.ShouldBeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public async Task SearchEntries_ClampsLimitTo100()
    {
        var registry = new Mock<IAgentRegistry>();
        registry.Setup(r => r.Get(AgentId.From("test-agent"))).Returns(AgentWithMemory);

        var store = new Mock<IMemoryStore>();
        store.Setup(s => s.InitializeAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        store.Setup(s => s.SearchAsync("test", 100, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<MemoryEntry>());

        var factory = new Mock<IMemoryStoreFactory>();
        factory.Setup(f => f.Create("test-agent")).Returns(store.Object);

        var controller = new MemoryController(registry.Object, factory.Object, NullLogger<MemoryController>.Instance);
        var result = await controller.SearchEntries("test-agent", query: "test", limit: 500);

        result.ShouldBeOfType<OkObjectResult>();
        store.Verify(s => s.SearchAsync("test", 100, null, It.IsAny<CancellationToken>()), Times.Once);
    }
}
