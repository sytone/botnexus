using System.IO.Abstractions.TestingHelpers;
using System.Text.Json;
using System.Text.Json.Nodes;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Configuration;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Agents;
using BotNexus.Gateway.Api.Controllers;
using BotNexus.Gateway.Api.Models;
using BotNexus.Gateway.Abstractions.Isolation;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Cron;
using BotNexus.Gateway.Configuration;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace BotNexus.Gateway.Tests;

public sealed class AgentEnabledStateTests
{
    private static AgentDescriptor Descriptor(string? enabled = null) =>
        JsonSerializer.Deserialize<AgentDescriptor>("""
            {"agentId":"assistant","displayName":"Assistant","apiProvider":"copilot","modelId":"gpt-4.1"
            """ + (enabled is null ? "}" : ",\"enabled\":" + enabled + "}"),
            new JsonSerializerOptions(JsonSerializerDefaults.Web))
        ?? throw new InvalidOperationException("Expected descriptor.");

    [Fact]
    public async Task Writer_ExplicitFalsePersists_AndOmittedSavePreservesFalseAndUnknownFields()
    {
        var fs = new MockFileSystem();
        var home = new BotNexusHome(fs, Path.Combine(Path.GetTempPath(), "enabled-state"));
        var path = Path.Combine(home.RootPath, "config.json");
        fs.Directory.CreateDirectory(home.RootPath);
        fs.File.WriteAllText(path, """{"customRoot":42,"agents":{"assistant":{"customAgent":"keep"},"other":{"enabled":false}}}""");
        var writer = new PlatformConfigAgentWriter(new PlatformConfigWriter(path, fs), home);
        await writer.SaveAsync(Descriptor("false"));
        JsonNode.Parse(fs.File.ReadAllText(path))?["agents"]?["assistant"]?["enabled"]?.GetValue<bool>().ShouldBe(false);
        await writer.SaveAsync(Descriptor() with { DisplayName = "Renamed" });
        var root = JsonNode.Parse(fs.File.ReadAllText(path));
        root?["agents"]?["assistant"]?["enabled"]?.GetValue<bool>().ShouldBe(false);
        root?["agents"]?["assistant"]?["customAgent"]?.GetValue<string>().ShouldBe("keep");
        root?["agents"]?["other"]?["enabled"]?.GetValue<bool>().ShouldBe(false);
        root?["customRoot"]?.GetValue<int>().ShouldBe(42);
        await writer.SaveAsync(Descriptor("true"));
        JsonNode.Parse(fs.File.ReadAllText(path))?["agents"]?["assistant"]?["enabled"]?.GetValue<bool>().ShouldBe(true);
    }

    [Fact]
    public async Task Endpoint_DisableRemainsEditable_OmittedUpdatePreservesState_AndReenableRestoresAdmission()
    {
        var registry = new DefaultAgentRegistry(NullLogger<DefaultAgentRegistry>.Instance);
        registry.Register(Descriptor());
        var writer = new Mock<IAgentConfigurationWriter>();
        var controller = new AgentsController(registry, Mock.Of<IAgentSupervisor>(), writer.Object);
        (await controller.Update("assistant", Descriptor("false"), CancellationToken.None)).Result.ShouldBeOfType<OkObjectResult>();
        registry.Get(AgentId.From("assistant")).ShouldBeNull();
        registry.GetAll().ShouldBeEmpty();
        registry.Contains(AgentId.From("assistant")).ShouldBeTrue();
        controller.List().Result.ShouldBeOfType<OkObjectResult>().Value.ShouldBeAssignableTo<IReadOnlyList<AgentListItem>>().ShouldBeEmpty();
        controller.List(includeDisabled: true).Result.ShouldBeOfType<OkObjectResult>().Value.ShouldBeAssignableTo<IReadOnlyList<AgentListItem>>()
            .ShouldHaveSingleItem().Enabled.ShouldBeFalse();
        var disabled = controller.Get("assistant").Result.ShouldBeOfType<OkObjectResult>().Value.ShouldBeOfType<AgentDescriptor>();
        JsonSerializer.SerializeToElement(disabled, new JsonSerializerOptions(JsonSerializerDefaults.Web)).GetProperty("enabled").GetBoolean().ShouldBeFalse();
        (await controller.Update("assistant", Descriptor() with { DisplayName = "Renamed" }, CancellationToken.None)).Result.ShouldBeOfType<OkObjectResult>();
        registry.Get(AgentId.From("assistant")).ShouldBeNull();
        (await controller.Update("assistant", Descriptor("true"), CancellationToken.None)).Result.ShouldBeOfType<OkObjectResult>();
        registry.Get(AgentId.From("assistant")).ShouldNotBeNull();
    }

    [Fact]
    public async Task Source_DisabledDescriptorSurvivesLoad_ForAdministrationAndReenable()
    {
        var config = new PlatformConfig
        {
            Agents = new() { ["assistant"] = new() { Provider = "copilot", Model = "gpt-4.1", Enabled = false } }
        };
        var monitor = new Mock<IOptionsMonitor<PlatformConfig>>();
        monitor.SetupGet(m => m.CurrentValue).Returns(config);
        var source = new PlatformConfigAgentSource(monitor.Object, Path.GetTempPath(), NullLogger<PlatformConfigAgentSource>.Instance);
        var descriptor = (await source.LoadAsync()).ShouldHaveSingleItem();
        var registry = new DefaultAgentRegistry(NullLogger<DefaultAgentRegistry>.Instance);
        registry.Register(descriptor);
        registry.Get(descriptor.AgentId).ShouldBeNull();
        var controller = new AgentsController(registry, Mock.Of<IAgentSupervisor>(), Mock.Of<IAgentConfigurationWriter>());
        controller.Get("assistant").Result.ShouldBeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task Endpoint_CreateDefaultsEnabled_AndDisabledCreateRemainsManaged()
    {
        var registry = new DefaultAgentRegistry(NullLogger<DefaultAgentRegistry>.Instance);
        var controller = new AgentsController(registry, Mock.Of<IAgentSupervisor>(), Mock.Of<IAgentConfigurationWriter>());
        var created = (await controller.Register(Descriptor(), CancellationToken.None)).ShouldBeOfType<CreatedAtActionResult>();
        var json = JsonSerializer.SerializeToElement(created.Value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        json.GetProperty("enabled").GetBoolean().ShouldBeTrue();
        registry.Get(AgentId.From("assistant")).ShouldNotBeNull();
        await controller.Unregister("assistant", CancellationToken.None);
        (await controller.Register(Descriptor("false"), CancellationToken.None)).ShouldBeOfType<CreatedAtActionResult>();
        registry.GetAll().ShouldBeEmpty();
        controller.Get("assistant").Result.ShouldBeOfType<OkObjectResult>();
        (await controller.Register(Descriptor(), CancellationToken.None)).ShouldBeOfType<ConflictObjectResult>();
    }

    [Fact]
    public async Task Writer_OmittedCreateDefaultsEnabled()
    {
        var fs = new MockFileSystem();
        var home = new BotNexusHome(fs, Path.Combine(Path.GetTempPath(), "enabled-default"));
        var path = Path.Combine(home.RootPath, "config.json");
        var writer = new PlatformConfigAgentWriter(new PlatformConfigWriter(path, fs), home);
        await writer.SaveAsync(Descriptor());
        JsonNode.Parse(fs.File.ReadAllText(path))?["agents"]?["assistant"]?["enabled"]?.GetValue<bool>().ShouldBe(true);
    }

    [Fact]
    public async Task Supervisor_DisableRejectsNewAdmission_ButKeepsExistingHandle()
    {
        var registry = new DefaultAgentRegistry(NullLogger<DefaultAgentRegistry>.Instance);
        var descriptor = Descriptor();
        registry.Register(descriptor);
        var strategy = new Mock<IIsolationStrategy>();
        strategy.SetupGet(s => s.Name).Returns("in-process");
        var handle = new Mock<IAgentHandle>();
        strategy.Setup(s => s.CreateAsync(It.IsAny<AgentDescriptor>(), It.IsAny<AgentExecutionContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);
        var supervisor = new DefaultAgentSupervisor(registry, [strategy.Object], Mock.Of<ISessionStore>(), NullLogger<DefaultAgentSupervisor>.Instance);
        var sessionId = SessionId.From("enabled-session");
        var admitted = await supervisor.GetOrCreateAsync(descriptor.AgentId, sessionId);
        registry.Update(descriptor.AgentId, descriptor with { Enabled = false }).ShouldBeTrue();
        supervisor.GetHandle(descriptor.AgentId, sessionId).ShouldBeSameAs(admitted);
        await Should.ThrowAsync<KeyNotFoundException>(() => supervisor.GetOrCreateAsync(descriptor.AgentId, sessionId));
        handle.Verify(h => h.DisposeAsync(), Times.Never);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Endpoint_ProvisionFailureRestoresPreviousEnabledState(bool initial)
    {
        var registry = new DefaultAgentRegistry(NullLogger<DefaultAgentRegistry>.Instance);
        registry.Register(Descriptor(initial ? "true" : "false"));
        var writer = new Mock<IAgentConfigurationWriter>();
        var heartbeat = new Mock<IHeartbeatProvisioner>();
        heartbeat.Setup(p => p.ProvisionAsync(It.IsAny<AgentDescriptor>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("provision failure"));
        var controller = new AgentsController(registry, Mock.Of<IAgentSupervisor>(), writer.Object, heartbeatProvisioner: heartbeat.Object);
        (await controller.Update("assistant", Descriptor(initial ? "false" : "true"), CancellationToken.None)).Result
            .ShouldBeOfType<ObjectResult>().StatusCode.ShouldBe(500);
        registry.Get(AgentId.From("assistant"), includeDisabled: true).ShouldNotBeNull().Enabled.ShouldBe(initial);
        writer.Verify(w => w.SaveAsync(It.Is<AgentDescriptor>(d => d.Enabled == initial), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Endpoint_ProvisionFailureRestoresEffectiveStateInRegistryAndPersistedConfig(bool? initial)
    {
        var fs = new MockFileSystem();
        var home = new BotNexusHome(fs, Path.Combine(Path.GetTempPath(), "enabled-rollback"));
        var path = Path.Combine(home.RootPath, "config.json");
        var writer = new PlatformConfigAgentWriter(new PlatformConfigWriter(path, fs), home);
        var original = Descriptor(initial?.ToString().ToLowerInvariant());
        var effective = initial ?? true;
        await writer.SaveAsync(original);
        var registry = new DefaultAgentRegistry(NullLogger<DefaultAgentRegistry>.Instance);
        registry.Register(original);
        var heartbeat = new Mock<IHeartbeatProvisioner>();
        heartbeat.Setup(p => p.ProvisionAsync(It.IsAny<AgentDescriptor>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("provision failure"));
        var controller = new AgentsController(registry, Mock.Of<IAgentSupervisor>(), writer, heartbeatProvisioner: heartbeat.Object);

        (await controller.Update("assistant", original with { Enabled = !effective }, CancellationToken.None)).Result
            .ShouldBeOfType<ObjectResult>().StatusCode.ShouldBe(500);

        registry.Get(original.AgentId, includeDisabled: true).ShouldNotBeNull().Enabled.ShouldBe(effective);
        (registry.Get(original.AgentId) is not null).ShouldBe(effective);
        JsonNode.Parse(fs.File.ReadAllText(path))?["agents"]?["assistant"]?["enabled"]?.GetValue<bool>().ShouldBe(effective);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Exchange_DisabledExactTargetRejectsBeforeAnyStateOrSupervisorAccess(bool crossWorldShapedId)
    {
        var registry = new DefaultAgentRegistry(NullLogger<DefaultAgentRegistry>.Instance);
        var initiator = Descriptor() with { AgentId = AgentId.From("initiator") };
        var target = Descriptor("false") with { AgentId = AgentId.From(crossWorldShapedId ? "world:disabled" : "disabled") };
        registry.Register(initiator);
        registry.Register(target);
        // A display-name match must not override an exact but disabled registration.
        registry.Register(Descriptor() with { AgentId = AgentId.From("other"), DisplayName = target.AgentId.Value });
        var sessions = new Mock<ISessionStore>(MockBehavior.Strict);
        var conversations = new Mock<BotNexus.Gateway.Abstractions.Conversations.IConversationStore>(MockBehavior.Strict);
        var supervisor = new Mock<IAgentSupervisor>(MockBehavior.Strict);
        var federation = new Mock<ICrossWorldExchangeRouter>(MockBehavior.Strict);
        var service = new AgentExchangeService(registry, supervisor.Object, sessions.Object, conversations.Object,
            Options.Create(new GatewayOptions()), NullLogger<AgentExchangeService>.Instance,
            crossWorldRouter: federation.Object);

        await Should.ThrowAsync<KeyNotFoundException>(() => service.ConverseAsync(new BotNexus.Domain.AgentExchange.AgentExchangeRequest
        {
            InitiatorId = initiator.AgentId, TargetId = target.AgentId, Message = "hello"
        }));

        sessions.VerifyNoOtherCalls();
        conversations.VerifyNoOtherCalls();
        supervisor.VerifyNoOtherCalls();
        federation.VerifyNoOtherCalls();
        registry.Contains(target.AgentId).ShouldBeTrue();
    }

    [Fact]
    public async Task Reload_DisableAndReenableUpdatesAdmission_AndRemovalDropsRegistration()
    {
        var registry = new DefaultAgentRegistry(NullLogger<DefaultAgentRegistry>.Instance);
        var source = new Mock<IAgentConfigurationSource>();
        Action<IReadOnlyList<AgentDescriptor>>? changed = null;
        source.Setup(s => s.LoadAsync(It.IsAny<CancellationToken>())).ReturnsAsync([Descriptor()]);
        source.Setup(s => s.Watch(It.IsAny<Action<IReadOnlyList<AgentDescriptor>>>()))
            .Callback<Action<IReadOnlyList<AgentDescriptor>>>(callback => changed = callback);
        using var service = new AgentConfigurationHostedService([source.Object], registry,
            NullLogger<AgentConfigurationHostedService>.Instance, (_, _) => Task.CompletedTask);
        await service.StartAsync(CancellationToken.None);
        var callback = changed ?? throw new InvalidOperationException("Expected source watcher.");
        callback([Descriptor("false")]);
        await service.PendingDebounceTask;
        registry.Get(AgentId.From("assistant")).ShouldBeNull();
        registry.GetAll(includeDisabled: true).ShouldHaveSingleItem().Enabled.ShouldBe(false);
        callback([Descriptor("true")]);
        await service.PendingDebounceTask;
        registry.Get(AgentId.From("assistant")).ShouldNotBeNull();
        callback([Descriptor("false")]);
        await service.PendingDebounceTask;
        callback([]);
        await service.PendingDebounceTask;
        registry.Contains(AgentId.From("assistant")).ShouldBeFalse();
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public void Source_WatchEmitsEnabledOnlyChanges_AndSuppressesUnchangedState()
    {
        var config = new PlatformConfig
        {
            Agents = new() { ["assistant"] = new() { Provider = "copilot", Model = "gpt-4.1" } }
        };
        var monitor = new Mock<IOptionsMonitor<PlatformConfig>>();
        monitor.SetupGet(m => m.CurrentValue).Returns(config);
        Action<PlatformConfig, string?>? notify = null;
        monitor.Setup(m => m.OnChange(It.IsAny<Action<PlatformConfig, string?>>() ))
            .Callback<Action<PlatformConfig, string?>>(callback => notify = callback);
        var source = new PlatformConfigAgentSource(monitor.Object, Path.GetTempPath(), NullLogger<PlatformConfigAgentSource>.Instance);
        var notifications = new List<IReadOnlyList<AgentDescriptor>>();
        using var watcher = source.Watch(notifications.Add);
        var callback = notify ?? throw new InvalidOperationException("Expected options watcher.");
        callback(config, null);
        notifications.ShouldBeEmpty();
        config.Agents["assistant"].Enabled = false;
        callback(config, null);
        notifications.ShouldHaveSingleItem().ShouldHaveSingleItem().Enabled.ShouldBe(false);
        callback(config, null);
        notifications.Count.ShouldBe(1);
        config.Agents["assistant"].Enabled = true;
        callback(config, null);
        notifications.Count.ShouldBe(2);
        notifications[1].ShouldHaveSingleItem().Enabled.ShouldBe(true);
    }

    [Fact]
    public async Task Endpoint_InvalidEnableRetainsDisabledState_AndDoesNotPersist()
    {
        var registry = new DefaultAgentRegistry(NullLogger<DefaultAgentRegistry>.Instance);
        registry.Register(Descriptor("false"));
        var writer = new Mock<IAgentConfigurationWriter>();
        var controller = new AgentsController(registry, Mock.Of<IAgentSupervisor>(), writer.Object);
        (await controller.Update("assistant", Descriptor("true") with { ApiProvider = "" }, CancellationToken.None)).Result
            .ShouldBeOfType<BadRequestObjectResult>();
        registry.Get(AgentId.From("assistant")).ShouldBeNull();
        registry.GetAll(includeDisabled: true).ShouldHaveSingleItem().Enabled.ShouldBe(false);
        writer.Verify(w => w.SaveAsync(It.IsAny<AgentDescriptor>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Endpoint_EnableStillValidatesRegisteredModelCapabilitiesBeforeMutation(bool invalidThinking)
    {
        var registry = new DefaultAgentRegistry(NullLogger<DefaultAgentRegistry>.Instance);
        registry.Register(Descriptor("false"));
        var models = new BotNexus.Agent.Providers.Core.Registry.ModelRegistry();
        models.Register("github-copilot", new BotNexus.Agent.Providers.Core.Models.LlmModel(
            Id: "gpt-4.1", Name: "Plain", Api: "completions", Provider: "github-copilot",
            BaseUrl: "https://example.invalid", Reasoning: false, Input: ["text"],
            Cost: new BotNexus.Agent.Providers.Core.Models.ModelCost(0m, 0m, 0m, 0m),
            ContextWindow: 128_000, MaxTokens: 16_000));
        var writer = new Mock<IAgentConfigurationWriter>();
        var controller = new AgentsController(registry, Mock.Of<IAgentSupervisor>(), writer.Object, modelRegistry: models);
        var candidate = Descriptor("true") with
        {
            Thinking = invalidThinking ? "high" : null,
            ContextWindow = invalidThinking ? null : 1_000_000
        };

        (await controller.Update("assistant", candidate, CancellationToken.None)).Result.ShouldBeOfType<BadRequestObjectResult>();

        registry.Get(AgentId.From("assistant")).ShouldBeNull();
        registry.GetAll(includeDisabled: true).ShouldHaveSingleItem().Enabled.ShouldBe(false);
        writer.Verify(w => w.SaveAsync(It.IsAny<AgentDescriptor>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private static BotNexus.Agent.Providers.Core.Registry.ModelRegistry RuntimeModels()
    {
        var models = new BotNexus.Agent.Providers.Core.Registry.ModelRegistry();
        models.Register("github-copilot", new BotNexus.Agent.Providers.Core.Models.LlmModel(
            Id: "gpt-4.1", Name: "Plain", Api: "completions", Provider: "github-copilot",
            BaseUrl: "https://example.invalid", Reasoning: false, Input: ["text"],
            Cost: new BotNexus.Agent.Providers.Core.Models.ModelCost(0m, 0m, 0m, 0m),
            ContextWindow: 128_000, MaxTokens: 16_000));
        return models;
    }

    [Theory]
    [InlineData("missing-provider", "gpt-4.1", false)]
    [InlineData("copilot", "missing-model", false)]
    [InlineData("copilot", "gpt-4.1", true)]
    public async Task Endpoint_UnresolvedEnableRejectsBeforePersistenceOrRuntimeMutation(string provider, string model, bool emptyRegistry)
    {
        var registry = new DefaultAgentRegistry(NullLogger<DefaultAgentRegistry>.Instance);
        var original = Descriptor("false") with { ApiProvider = provider, ModelId = model };
        registry.Register(original);
        var writer = new Mock<IAgentConfigurationWriter>(MockBehavior.Strict);
        var supervisor = new Mock<IAgentSupervisor>(MockBehavior.Strict);
        var heartbeat = new Mock<IHeartbeatProvisioner>(MockBehavior.Strict);
        var notifier = new Mock<IAgentChangeNotifier>(MockBehavior.Strict);
        var models = emptyRegistry ? new BotNexus.Agent.Providers.Core.Registry.ModelRegistry() : RuntimeModels();
        var controller = new AgentsController(registry, supervisor.Object, writer.Object, [notifier.Object],
            heartbeatProvisioner: heartbeat.Object, modelRegistry: models);

        var result = (await controller.Update("assistant", original with { Enabled = true, DisplayName = "Must not apply" }, CancellationToken.None)).Result
            .ShouldBeOfType<BadRequestObjectResult>();

        result.StatusCode.ShouldBe(400);
        registry.Get(original.AgentId, includeDisabled: true).ShouldBeSameAs(original);
        registry.Get(original.AgentId).ShouldBeNull();
        writer.VerifyNoOtherCalls();
        supervisor.VerifyNoOtherCalls();
        heartbeat.VerifyNoOtherCalls();
        notifier.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("missing-provider", "gpt-4.1")]
    [InlineData("copilot", "missing-model")]
    public async Task Endpoint_ExplicitEnabledCreateRequiresResolvableModelBeforePersistence(string provider, string model)
    {
        var registry = new DefaultAgentRegistry(NullLogger<DefaultAgentRegistry>.Instance);
        var writer = new Mock<IAgentConfigurationWriter>(MockBehavior.Strict);
        var controller = new AgentsController(registry, Mock.Of<IAgentSupervisor>(), writer.Object, modelRegistry: RuntimeModels());
        var candidate = Descriptor("true") with { ApiProvider = provider, ModelId = model };

        (await controller.Register(candidate, CancellationToken.None)).ShouldBeOfType<BadRequestObjectResult>().StatusCode.ShouldBe(400);

        registry.Contains(candidate.AgentId).ShouldBeFalse();
        writer.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Endpoint_ExplicitEnableResolvesCandidateProviderAlias(bool create)
    {
        var registry = new DefaultAgentRegistry(NullLogger<DefaultAgentRegistry>.Instance);
        if (!create) registry.Register(Descriptor("false") with { ApiProvider = "missing-provider", ModelId = "missing-model" });
        var writer = new Mock<IAgentConfigurationWriter>();
        var controller = new AgentsController(registry, Mock.Of<IAgentSupervisor>(), writer.Object, modelRegistry: RuntimeModels());
        var candidate = Descriptor("true");

        if (create)
            (await controller.Register(candidate, CancellationToken.None)).ShouldBeOfType<CreatedAtActionResult>();
        else
            (await controller.Update("assistant", candidate, CancellationToken.None)).Result.ShouldBeOfType<OkObjectResult>();

        registry.Get(candidate.AgentId).ShouldNotBeNull().Enabled.ShouldBe(true);
        writer.Verify(w => w.SaveAsync(It.Is<AgentDescriptor>(d => d.Enabled == true && d.ApiProvider == "copilot" && d.ModelId == "gpt-4.1"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(false)]
    public async Task Endpoint_OmittedEnableEditPreservesUnresolvedExistingState(bool? initial)
    {
        var registry = new DefaultAgentRegistry(NullLogger<DefaultAgentRegistry>.Instance);
        var original = Descriptor() with { Enabled = initial, ApiProvider = "missing-provider", ModelId = "missing-model" };
        registry.Register(original);
        var writer = new Mock<IAgentConfigurationWriter>();
        var controller = new AgentsController(registry, Mock.Of<IAgentSupervisor>(), writer.Object, modelRegistry: RuntimeModels());

        (await controller.Update("assistant", original with { Enabled = null, DisplayName = "Renamed" }, CancellationToken.None)).Result
            .ShouldBeOfType<OkObjectResult>();

        var applied = registry.Get(original.AgentId, includeDisabled: true).ShouldNotBeNull();
        applied.Enabled.ShouldBe(initial ?? true);
        applied.DisplayName.ShouldBe("Renamed");
        writer.Verify(w => w.SaveAsync(It.Is<AgentDescriptor>(d => d.Enabled == (initial ?? true)), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Endpoint_FailedPersistenceLeavesAdmissionUnchanged()
    {
        var registry = new DefaultAgentRegistry(NullLogger<DefaultAgentRegistry>.Instance);
        registry.Register(Descriptor());
        var writer = new Mock<IAgentConfigurationWriter>();
        writer.Setup(w => w.SaveAsync(It.IsAny<AgentDescriptor>(), It.IsAny<CancellationToken>())).ThrowsAsync(new IOException("disk failure"));
        var controller = new AgentsController(registry, Mock.Of<IAgentSupervisor>(), writer.Object);
        (await controller.Update("assistant", Descriptor("false"), CancellationToken.None)).Result.ShouldBeOfType<ObjectResult>().StatusCode.ShouldBe(500);
        registry.Get(AgentId.From("assistant")).ShouldNotBeNull();
    }
}
