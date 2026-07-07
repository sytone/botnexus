using System.Text.Json;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Memory;
using Moq;

namespace BotNexus.Extensions.Qmd.Tests;

public sealed class QmdToolContributorMemoryTests
{
    [Fact]
    public async Task ContributeAsync_WithMemoryStoresEnabled_CreatesCompositeBackend()
    {
        var registry = new Mock<ISharedMemoryStoreRegistry>();
        registry.Setup(r => r.GetReadableStores("test-agent")).Returns(["shared-store"]);

        var configJson = JsonSerializer.SerializeToElement(new { enabled = true, includeMemoryStores = true });
        var descriptor = new AgentDescriptor
        {
            AgentId = AgentId.From("test-agent"),
            DisplayName = "Test Agent",
            ModelId = "test-model",
            ApiProvider = "test-provider",
            ExtensionConfig = new Dictionary<string, JsonElement> { ["botnexus-qmd"] = configJson }
        };

        var context = new AgentToolContributionContext(
            descriptor,
            new AgentExecutionContext { SessionId = BotNexus.Domain.Primitives.SessionId.From("sess1") },
            "/tmp/workspace",
            Mock.Of<BotNexus.Gateway.Abstractions.Security.IPathValidator>(),
            null,
            (_, _) => Task.FromResult<string?>(null));

        var contributor = new QmdToolContributor(null, registry.Object);
        var contribution = await contributor.ContributeAsync(context);

        Assert.Equal(3, contribution.Tools.Count); // search, stores, get
        Assert.NotNull(contribution.ResourcesToDispose);
        Assert.Single(contribution.ResourcesToDispose);
        // The backend should be a CompositeQmdBackend
        Assert.IsType<CompositeQmdBackend>(contribution.ResourcesToDispose[0]);
    }

    [Fact]
    public async Task ContributeAsync_WithMemoryStoresDisabled_CreatesCliBackendOnly()
    {
        var registry = new Mock<ISharedMemoryStoreRegistry>();

        var configJson = JsonSerializer.SerializeToElement(new { enabled = true, includeMemoryStores = false });
        var descriptor = new AgentDescriptor
        {
            AgentId = AgentId.From("test-agent"),
            DisplayName = "Test Agent",
            ModelId = "test-model",
            ApiProvider = "test-provider",
            ExtensionConfig = new Dictionary<string, JsonElement> { ["botnexus-qmd"] = configJson }
        };

        var context = new AgentToolContributionContext(
            descriptor,
            new AgentExecutionContext { SessionId = BotNexus.Domain.Primitives.SessionId.From("sess1") },
            "/tmp/workspace",
            Mock.Of<BotNexus.Gateway.Abstractions.Security.IPathValidator>(),
            null,
            (_, _) => Task.FromResult<string?>(null));

        var contributor = new QmdToolContributor(null, registry.Object);
        var contribution = await contributor.ContributeAsync(context);

        Assert.Equal(3, contribution.Tools.Count);
        Assert.NotNull(contribution.ResourcesToDispose);
        Assert.Single(contribution.ResourcesToDispose);
        Assert.IsType<QmdCliBackend>(contribution.ResourcesToDispose[0]);
    }

    [Fact]
    public async Task ContributeAsync_WithNoRegistry_CreatesCliBackendOnly()
    {
        var configJson = JsonSerializer.SerializeToElement(new { enabled = true, includeMemoryStores = true });
        var descriptor = new AgentDescriptor
        {
            AgentId = AgentId.From("test-agent"),
            DisplayName = "Test Agent",
            ModelId = "test-model",
            ApiProvider = "test-provider",
            ExtensionConfig = new Dictionary<string, JsonElement> { ["botnexus-qmd"] = configJson }
        };

        var context = new AgentToolContributionContext(
            descriptor,
            new AgentExecutionContext { SessionId = BotNexus.Domain.Primitives.SessionId.From("sess1") },
            "/tmp/workspace",
            Mock.Of<BotNexus.Gateway.Abstractions.Security.IPathValidator>(),
            null,
            (_, _) => Task.FromResult<string?>(null));

        // No registry passed
        var contributor = new QmdToolContributor(null, null);
        var contribution = await contributor.ContributeAsync(context);

        Assert.Equal(3, contribution.Tools.Count);
        Assert.IsType<QmdCliBackend>(contribution.ResourcesToDispose![0]);
    }
}
