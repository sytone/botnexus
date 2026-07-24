using System.Text.Json;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using Moq;

namespace BotNexus.Extensions.Qmd.Tests;

public sealed class QmdToolContributorTests
{
    [Fact]
    public void ResolveConfig_WithExplicitEnabledConfig_ReturnsConfig()
    {
        var configJson = JsonSerializer.SerializeToElement(new { enabled = true, qmdPath = "/usr/bin/qmd", maxResults = 20 });
        var descriptor = CreateDescriptor(new Dictionary<string, JsonElement> { ["botnexus-qmd"] = configJson });

        var config = QmdToolContributor.ResolveConfig(descriptor);

        Assert.True(config.Enabled);
        Assert.Equal("/usr/bin/qmd", config.QmdPath);
        Assert.Equal(20, config.MaxResults);
    }

    [Fact]
    public void ResolveConfig_WithNoExtensionConfig_ReturnsDisabled()
    {
        // Absent config must fail closed to disabled (issue #2116).
        var descriptor = CreateDescriptor(new Dictionary<string, JsonElement>());
        var config = QmdToolContributor.ResolveConfig(descriptor);

        Assert.False(config.Enabled);
    }

    [Fact]
    public void ResolveConfig_WithEmptyObjectConfig_ReturnsDisabled()
    {
        // Present-but-empty config (no explicit enabled:true) must remain disabled.
        var configJson = JsonSerializer.SerializeToElement(new { });
        var descriptor = CreateDescriptor(new Dictionary<string, JsonElement> { ["botnexus-qmd"] = configJson });

        var config = QmdToolContributor.ResolveConfig(descriptor);
        Assert.False(config.Enabled);
    }

    [Fact]
    public void ResolveConfig_WithDisabledConfig_ReturnsDisabled()
    {
        var configJson = JsonSerializer.SerializeToElement(new { enabled = false });
        var descriptor = CreateDescriptor(new Dictionary<string, JsonElement> { ["botnexus-qmd"] = configJson });

        var config = QmdToolContributor.ResolveConfig(descriptor);
        Assert.False(config.Enabled);
    }

    [Fact]
    public void ResolveConfig_WithMalformedJson_FailsClosedToDisabled()
    {
        // Malformed config must fail closed to disabled rather than defaulting to enabled.
        var malformed = JsonSerializer.SerializeToElement("not an object");
        var descriptor = CreateDescriptor(new Dictionary<string, JsonElement> { ["botnexus-qmd"] = malformed });

        var config = QmdToolContributor.ResolveConfig(descriptor);
        Assert.False(config.Enabled);
    }

    [Fact]
    public void ResolveConfig_WithMalformedJson_EmitsDiagnostics()
    {
        var malformed = JsonSerializer.SerializeToElement("not an object");
        var descriptor = CreateDescriptor(new Dictionary<string, JsonElement> { ["botnexus-qmd"] = malformed });
        var logger = new CapturingLogger();

        var config = QmdToolContributor.ResolveConfig(descriptor, logger);

        Assert.False(config.Enabled);
        Assert.Contains(logger.Messages, m => m.Contains("botnexus-qmd", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ContributeAsync_WithNoConfig_ContributesNoToolsOrResources()
    {
        // No QMD config => no QMD tools, no backend/process resources.
        var descriptor = CreateDescriptor(new Dictionary<string, JsonElement>());
        var contributor = new QmdToolContributor();

        var contribution = await contributor.ContributeAsync(CreateContext(descriptor));

        Assert.Empty(contribution.Tools);
        Assert.True(contribution.ResourcesToDispose is null || contribution.ResourcesToDispose.Count == 0);
    }

    [Fact]
    public async Task ContributeAsync_WithMalformedConfig_ContributesNoTools()
    {
        var malformed = JsonSerializer.SerializeToElement("not an object");
        var descriptor = CreateDescriptor(new Dictionary<string, JsonElement> { ["botnexus-qmd"] = malformed });
        var contributor = new QmdToolContributor();

        var contribution = await contributor.ContributeAsync(CreateContext(descriptor));

        Assert.Empty(contribution.Tools);
        Assert.True(contribution.ResourcesToDispose is null || contribution.ResourcesToDispose.Count == 0);
    }

    [Fact]
    public async Task ContributeAsync_WithExplicitEnabled_ContributesKnowledgeTools()
    {
        var configJson = JsonSerializer.SerializeToElement(new { enabled = true });
        var descriptor = CreateDescriptor(new Dictionary<string, JsonElement> { ["botnexus-qmd"] = configJson });
        var contributor = new QmdToolContributor();

        var contribution = await contributor.ContributeAsync(CreateContext(descriptor));

        var toolNames = contribution.Tools.Select(t => t.Name).ToList();
        Assert.Contains("knowledge_search", toolNames);
        Assert.Contains("knowledge_stores", toolNames);
        Assert.Contains("knowledge_get", toolNames);
    }

    private static AgentDescriptor CreateDescriptor(Dictionary<string, JsonElement> extensionConfig)
    {
        return new AgentDescriptor
        {
            AgentId = AgentId.From("test-agent"),
            DisplayName = "Test Agent",
            ModelId = "test-model",
            ApiProvider = "test-provider",
            ExtensionConfig = extensionConfig
        };
    }

    private static AgentToolContributionContext CreateContext(AgentDescriptor descriptor)
    {
        return new AgentToolContributionContext(
            descriptor,
            new AgentExecutionContext { SessionId = SessionId.From("sess1") },
            "/tmp/workspace",
            Mock.Of<BotNexus.Gateway.Abstractions.Security.IPathValidator>(),
            null,
            (_, _) => Task.FromResult<string?>(null));
    }

    private sealed class CapturingLogger : Microsoft.Extensions.Logging.ILogger
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(
            Microsoft.Extensions.Logging.LogLevel logLevel,
            Microsoft.Extensions.Logging.EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Messages.Add(formatter(state, exception));
        }
    }
}
