using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Api.Controllers;
using BotNexus.Memory;
using BotNexus.Memory.Embeddings;
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
            .ReturnsAsync(new MemoryStoreStats(9107, 1024, DateTimeOffset.UtcNow, EmbeddedEntryCount: 9107, VectorScanCeiling: 5000));

        var factory = new Mock<IMemoryStoreFactory>();
        factory.Setup(f => f.Create(AgentId.From("test-agent"))).Returns(store.Object);

        var controller = new MemoryController(registry.Object, factory.Object, NullLogger<MemoryController>.Instance);
        var result = await controller.ListMemoryStores(CancellationToken.None);

        var ok = result.ShouldBeOfType<OkObjectResult>();
        var list = ok.Value as IEnumerable<object>;
        list.ShouldNotBeNull();

        // #3244 AC3: the store diagnostics must actually reach the payload. This is the live
        // nexus-web-tester shape - ~9,107 embedded rows against a 5,000 ceiling.
        var dto = list.ShouldHaveSingleItem();
        var type = dto.GetType();
        type.GetProperty("EmbeddedEntryCount")!.GetValue(dto).ShouldBe(9107);
        type.GetProperty("VectorScanCeiling")!.GetValue(dto).ShouldBe(5000);
        type.GetProperty("ExceedsVectorScanCeiling")!.GetValue(dto).ShouldBe(true);
    }

    [Fact]
    public async Task ListMemoryStores_BelowCeiling_DoesNotFlagExceeded()
    {
        var registry = new Mock<IAgentRegistry>();
        registry.Setup(r => r.GetAll()).Returns([AgentWithMemory]);

        var store = new Mock<IMemoryStore>();
        store.Setup(s => s.InitializeAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        store.Setup(s => s.GetStatsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MemoryStoreStats(120, 1024, DateTimeOffset.UtcNow, EmbeddedEntryCount: 120, VectorScanCeiling: 5000));

        var factory = new Mock<IMemoryStoreFactory>();
        factory.Setup(f => f.Create(AgentId.From("test-agent"))).Returns(store.Object);

        var controller = new MemoryController(registry.Object, factory.Object, NullLogger<MemoryController>.Instance);
        var result = await controller.ListMemoryStores(CancellationToken.None);

        var ok = result.ShouldBeOfType<OkObjectResult>();
        var dto = (ok.Value as IEnumerable<object>).ShouldNotBeNull().ShouldHaveSingleItem();
        dto.GetType().GetProperty("ExceedsVectorScanCeiling")!.GetValue(dto).ShouldBe(false);
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
        factory.Setup(f => f.Create(AgentId.From("test-agent"))).Returns(store.Object);

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
        // #3244: the controller now consumes SearchWithReportAsync so the scan-coverage report can
        // reach the Memory tab. Stubbing SearchAsync here would leave the real call unstubbed.
        store.Setup(s => s.SearchWithReportAsync("architecture", 20, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MemorySearchResult(
                entries.Select(entry => new ScoredMemoryEntry(entry, 0.5d)).ToList(),
                new MemoryVectorScanReport(MemoryVectorScanStatus.PossiblyTruncated, 5000, 5000, 2)));

        var factory = new Mock<IMemoryStoreFactory>();
        factory.Setup(f => f.Create(AgentId.From("test-agent"))).Returns(store.Object);

        var controller = new MemoryController(registry.Object, factory.Object, NullLogger<MemoryController>.Instance);
        var result = await controller.SearchEntries("test-agent", query: "architecture");

        var ok = result.ShouldBeOfType<OkObjectResult>();

        // The truncation signal must actually be rendered, not merely computed: the whole point of
        // #3244 is that a caller can distinguish a bounded scan from an exhaustive one.
        var payload = ok.Value!;
        var vectorScan = payload.GetType().GetProperty("vectorScan")!.GetValue(payload)!;
        var possiblyTruncated = vectorScan.GetType().GetProperty("possiblyTruncated")!.GetValue(vectorScan);
        var scanCeiling = vectorScan.GetType().GetProperty("scanCeiling")!.GetValue(vectorScan);

        possiblyTruncated.ShouldBe(true);
        scanCeiling.ShouldBe(5000);
    }

    [Fact]
    public async Task SearchEntries_CompleteScan_RendersNoTruncationSignal()
    {
        // Sad path for the signal itself: a complete scan must NOT report truncation, or the flag
        // is an unconditional alarm and tells an operator nothing.
        var registry = new Mock<IAgentRegistry>();
        registry.Setup(r => r.Get(AgentId.From("test-agent"))).Returns(AgentWithMemory);

        var store = new Mock<IMemoryStore>();
        store.Setup(s => s.InitializeAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        store.Setup(s => s.SearchWithReportAsync("architecture", 20, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MemorySearchResult(
                [],
                new MemoryVectorScanReport(MemoryVectorScanStatus.Complete, 12, 5000, 0)));

        var factory = new Mock<IMemoryStoreFactory>();
        factory.Setup(f => f.Create(AgentId.From("test-agent"))).Returns(store.Object);

        var controller = new MemoryController(registry.Object, factory.Object, NullLogger<MemoryController>.Instance);
        var result = await controller.SearchEntries("test-agent", query: "architecture");

        var ok = result.ShouldBeOfType<OkObjectResult>();
        var payload = ok.Value!;
        var vectorScan = payload.GetType().GetProperty("vectorScan")!.GetValue(payload)!;

        vectorScan.GetType().GetProperty("possiblyTruncated")!.GetValue(vectorScan).ShouldBe(false);
        vectorScan.GetType().GetProperty("status")!.GetValue(vectorScan).ShouldBe("Complete");
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
        store.Setup(s => s.SearchWithReportAsync("test", 100, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MemorySearchResult([], MemoryVectorScanReport.NotAttempted));

        var factory = new Mock<IMemoryStoreFactory>();
        factory.Setup(f => f.Create(AgentId.From("test-agent"))).Returns(store.Object);

        var controller = new MemoryController(registry.Object, factory.Object, NullLogger<MemoryController>.Instance);
        var result = await controller.SearchEntries("test-agent", query: "test", limit: 500);

        result.ShouldBeOfType<OkObjectResult>();
        store.Verify(s => s.SearchWithReportAsync("test", 100, null, It.IsAny<CancellationToken>()), Times.Once);
    }
}
