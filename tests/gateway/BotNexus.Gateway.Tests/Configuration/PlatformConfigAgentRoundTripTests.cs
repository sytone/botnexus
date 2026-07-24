using System.IO.Abstractions;
using System.Text.Json;
using System.Text.Json.Nodes;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Registry;
using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Security;
using BotNexus.Gateway.Agents;
using BotNexus.Gateway.Api.Controllers;
using BotNexus.Gateway.Configuration;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace BotNexus.Gateway.Tests.Configuration;

/// <summary>
/// Issue #2055 acceptance suite: proves that a portal-created / portal-edited agent survives a
/// real config write + reload with the same effective values, using the production components
/// (real <see cref="AgentsController"/>, real <see cref="PlatformConfigAgentWriter"/>, real
/// <see cref="PlatformConfigLoader"/> + <see cref="PlatformConfigAgentSource"/>) against a
/// temporary config file - not a mocked writer.
/// </summary>
public sealed class PlatformConfigAgentRoundTripTests : IDisposable
{
    private readonly string _rootPath = Path.Combine(
        Path.GetTempPath(),
        "botnexus-agent-roundtrip-tests",
        Guid.NewGuid().ToString("N"));
    private readonly string _configPath;
    private readonly IFileSystem _fileSystem = new FileSystem();
    private readonly BotNexusHome _home;

    public PlatformConfigAgentRoundTripTests()
    {
        Directory.CreateDirectory(_rootPath);
        _configPath = Path.Combine(_rootPath, "config.json");
        _home = new BotNexusHome(_fileSystem, _rootPath);
    }

    // ------------------------------------------------------------------
    // Real create -> reload parity across the full supported field surface
    // ------------------------------------------------------------------

    [Fact]
    public async Task Create_ThenReload_YieldsSameEffectiveDescriptorAcrossFullFieldSurface()
    {
        SeedConfigWithUnrelatedSections();
        var controller = CreateController(out _);

        var submitted = FullSurfaceDescriptor("portal-agent");

        var created = await controller.Register(submitted, CancellationToken.None);
        created.ShouldBeOfType<CreatedAtActionResult>();

        var effective = await ReloadEffectiveAsync("portal-agent");

        AssertEffectiveMatchesSubmitted(submitted, effective);
    }

    [Fact]
    public async Task Create_ThenReload_PreservesUnrelatedRootSectionsAndUnknownJson()
    {
        SeedConfigWithUnrelatedSections();
        var before = ReadConfigRoot();
        var controller = CreateController(out _);

        _ = await controller.Register(FullSurfaceDescriptor("portal-agent"), CancellationToken.None);

        var after = ReadConfigRoot();

        // Unrelated root sections must survive byte-for-byte (structurally unchanged).
        JsonNode.DeepEquals(after["gateway"], before["gateway"]).ShouldBeTrue();
        JsonNode.DeepEquals(after["channels"], before["channels"]).ShouldBeTrue();
        JsonNode.DeepEquals(after["providers"], before["providers"]).ShouldBeTrue();
        after["customRootField"]!.GetValue<string>().ShouldBe("preserve-me");

        // The pre-existing unrelated agent's own JSON, including extension-owned unknown keys,
        // must survive unchanged.
        JsonNode.DeepEquals(after["agents"]!["existing-agent"], before["agents"]!["existing-agent"]).ShouldBeTrue();
    }

    // ------------------------------------------------------------------
    // Real edit -> reload parity; editing one field leaves others intact
    // ------------------------------------------------------------------

    [Fact]
    public async Task Update_ChangingOneField_ThenReload_PreservesAllOtherExplicitFields()
    {
        SeedConfigWithUnrelatedSections();
        var registry = new DefaultAgentRegistry(NullLogger<DefaultAgentRegistry>.Instance);
        var controller = CreateController(registry);

        var original = FullSurfaceDescriptor("portal-agent");
        _ = await controller.Register(original, CancellationToken.None);

        // Edit exactly one field (thinking level) and persist.
        var edited = original with { Thinking = "low" };
        var updateResult = await controller.Update("portal-agent", edited, CancellationToken.None);
        updateResult.Result.ShouldBeOfType<OkObjectResult>();

        var effective = await ReloadEffectiveAsync("portal-agent");

        effective.Thinking.ShouldBe("low");
        // Every other explicit field is unchanged relative to the original submission.
        AssertEffectiveMatchesSubmitted(original with { Thinking = "low" }, effective);
    }

    [Fact]
    public async Task Update_ThenReload_LeavesUnrelatedAgentAndRootSectionsUnchanged()
    {
        SeedConfigWithUnrelatedSections();
        var registry = new DefaultAgentRegistry(NullLogger<DefaultAgentRegistry>.Instance);
        var controller = CreateController(registry);

        _ = await controller.Register(FullSurfaceDescriptor("portal-agent"), CancellationToken.None);
        var before = ReadConfigRoot();

        _ = await controller.Update(
            "portal-agent",
            FullSurfaceDescriptor("portal-agent") with { Description = "edited description" },
            CancellationToken.None);

        var after = ReadConfigRoot();

        JsonNode.DeepEquals(after["gateway"], before["gateway"]).ShouldBeTrue();
        JsonNode.DeepEquals(after["channels"], before["channels"]).ShouldBeTrue();
        JsonNode.DeepEquals(after["agents"]!["existing-agent"], before["agents"]!["existing-agent"]).ShouldBeTrue();
    }

    // ------------------------------------------------------------------
    // Delete only removes the target agent
    // ------------------------------------------------------------------

    [Fact]
    public async Task Delete_ThenReload_RemovesOnlyTargetAgentAndPreservesTheRest()
    {
        SeedConfigWithUnrelatedSections();
        var registry = new DefaultAgentRegistry(NullLogger<DefaultAgentRegistry>.Instance);
        var controller = CreateController(registry);

        _ = await controller.Register(FullSurfaceDescriptor("portal-agent"), CancellationToken.None);

        var delete = await controller.Unregister("portal-agent", CancellationToken.None);
        delete.ShouldBeOfType<NoContentResult>();

        var reloaded = await PlatformConfigLoader.LoadAsync(
            _configPath, CancellationToken.None, validateOnLoad: true, fileSystem: _fileSystem);

        reloaded.Agents.ShouldNotBeNull();
        reloaded.Agents!.ShouldContainKey("existing-agent");
        reloaded.Agents!.ShouldNotContainKey("portal-agent");

        var after = ReadConfigRoot();
        after["gateway"].ShouldNotBeNull();
        after["channels"].ShouldNotBeNull();
    }

    // ------------------------------------------------------------------
    // Restart simulation: a fresh source built from the persisted file only
    // ------------------------------------------------------------------

    [Fact]
    public async Task Create_ThenFreshSourceFromPersistedFile_ProducesEquivalentDescriptor()
    {
        SeedConfigWithUnrelatedSections();
        var controller = CreateController(out _);
        var submitted = FullSurfaceDescriptor("portal-agent");
        _ = await controller.Register(submitted, CancellationToken.None);

        // Simulate a process restart: nothing survives except the file on disk.
        var reloaded = await PlatformConfigLoader.LoadAsync(
            _configPath, CancellationToken.None, validateOnLoad: true, fileSystem: _fileSystem);
        var freshSource = new PlatformConfigAgentSource(
            new TestOptionsMonitor<PlatformConfig>(reloaded),
            _rootPath,
            new NullLogger<PlatformConfigAgentSource>(),
            locationResolver: null,
            modelRegistry: MakeModelRegistry());

        var effective = (await freshSource.LoadAsync())
            .Single(d => d.AgentId.Value == "portal-agent");

        AssertEffectiveMatchesSubmitted(submitted, effective);
    }

    // ------------------------------------------------------------------
    // Field-parity fitness function
    // ------------------------------------------------------------------

    [Fact]
    public void FieldParity_EveryDescriptorProperty_HasAnExplicitPersistenceDecision()
    {
        var settableProperties = typeof(AgentDescriptor)
            .GetProperties()
            .Where(p => p.CanWrite || HasInitAccessor(p))
            .Select(p => p.Name)
            .ToHashSet(StringComparer.Ordinal);

        var classified = new HashSet<string>(AgentDescriptorConfigMapping.Persisted, StringComparer.Ordinal);
        classified.UnionWith(AgentDescriptorConfigMapping.UnsupportedForPersistence);

        var unclassified = settableProperties.Except(classified).ToList();
        unclassified.ShouldBeEmpty(
            "Every portal-editable / persisted AgentDescriptor property must have an explicit " +
            "mapping decision in AgentDescriptorConfigMapping (either Persisted or " +
            "UnsupportedForPersistence). Newly unclassified: " + string.Join(", ", unclassified) +
            ". Add the property to the writer mapping and to the Persisted set, or record it as " +
            "UnsupportedForPersistence with a rationale.");

        var stale = classified.Except(settableProperties).ToList();
        stale.ShouldBeEmpty(
            "AgentDescriptorConfigMapping references properties that no longer exist on " +
            "AgentDescriptor: " + string.Join(", ", stale));

        // A property cannot be both persisted and unsupported.
        AgentDescriptorConfigMapping.Persisted
            .Intersect(AgentDescriptorConfigMapping.UnsupportedForPersistence)
            .ShouldBeEmpty();
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static bool HasInitAccessor(System.Reflection.PropertyInfo property)
    {
        var setMethod = property.SetMethod;
        if (setMethod is null)
            return false;
        // init-only setters carry the IsExternalInit modreq.
        return setMethod.ReturnParameter
            .GetRequiredCustomModifiers()
            .Any(t => t.FullName == "System.Runtime.CompilerServices.IsExternalInit");
    }

    private void SeedConfigWithUnrelatedSections()
    {
        const string seed = """
            {
              "version": 1,
              "customRootField": "preserve-me",
              "gateway": {
                "defaultTimezone": "America/Los_Angeles",
                "extensions": { "defaults": {} }
              },
              "channels": {
                "signalr": { "type": "signalr", "enabled": true },
                "telegram": { "type": "telegram", "enabled": true, "botToken": "secret-token" }
              },
              "providers": {
                "github-copilot": { "apiKey": "secret-key" }
              },
              "agents": {
                "existing-agent": {
                  "provider": "github-copilot",
                  "model": "reasoning-model",
                  "displayName": "Existing",
                  "enabled": true,
                  "extensions": { "botnexus-skills": { "unknownKey": "keep-me" } }
                }
              }
            }
            """;
        _fileSystem.File.WriteAllText(_configPath, seed);
    }

    private JsonObject ReadConfigRoot()
        => JsonNode.Parse(_fileSystem.File.ReadAllText(_configPath))!.AsObject();

    private AgentsController CreateController(IAgentRegistry registry)
    {
        var writer = new PlatformConfigAgentWriter(new PlatformConfigWriter(_configPath, _fileSystem), _home);
        return new AgentsController(
            registry,
            Mock.Of<IAgentSupervisor>(),
            writer,
            agentChangeNotifiers: null,
            heartbeatProvisioner: null,
            skillReviewProvisioner: null,
            modelRegistry: MakeModelRegistry(),
            logger: NullLogger<AgentsController>.Instance);
    }

    private AgentsController CreateController(out IAgentRegistry registry)
    {
        registry = new DefaultAgentRegistry(NullLogger<DefaultAgentRegistry>.Instance);
        return CreateController(registry);
    }

    private async Task<AgentDescriptor> ReloadEffectiveAsync(string agentId)
    {
        var reloaded = await PlatformConfigLoader.LoadAsync(
            _configPath, CancellationToken.None, validateOnLoad: true, fileSystem: _fileSystem);
        var source = new PlatformConfigAgentSource(
            new TestOptionsMonitor<PlatformConfig>(reloaded),
            _rootPath,
            new NullLogger<PlatformConfigAgentSource>(),
            locationResolver: null,
            modelRegistry: MakeModelRegistry());
        return (await source.LoadAsync()).Single(d => d.AgentId.Value == agentId);
    }

    private static AgentDescriptor FullSurfaceDescriptor(string agentId)
        => new()
        {
            AgentId = AgentId.From(agentId),
            DisplayName = "Portal Agent",
            ModelId = "reasoning-model",
            ApiProvider = "github-copilot",
            Emoji = "🤖",
            Description = "A portal-created agent",
            SystemPromptFile = "AGENTS.md",
            SystemPromptFiles = ["AGENTS.md", "SOUL.md"],
            ToolIds = ["read", "write"],
            AllowedModelIds = ["reasoning-model", "plain-model"],
            SubAgentIds = ["helper"],
            SubAgentRoles = ["coder"],
            IsolationStrategy = "in-process",
            CacheRetentionMode = "long",
            Thinking = "high",
            ContextWindow = 200000,
            MaxConcurrentSessions = 5,
            Metadata = new Dictionary<string, object?> { ["owner"] = "team-gateway" },
            IsolationOptions = new Dictionary<string, object?> { ["timeoutMs"] = 1000L },
            Memory = new MemoryAgentConfig
            {
                Enabled = true,
                Indexing = "auto",
                Path = "memory/custom.md",
                PromptInjection = "summary",
                Search = new MemorySearchAgentConfig
                {
                    DefaultTopK = 7,
                    TemporalDecay = new TemporalDecayAgentConfig { Enabled = true, HalfLifeDays = 21 }
                }
            },
            Soul = new SoulAgentConfig
            {
                Enabled = true,
                Timezone = "Europe/London",
                DayBoundary = "04:00",
                ReflectionOnSeal = true,
                ReflectionPrompt = "Reflect."
            },
            Heartbeat = new HeartbeatAgentConfig
            {
                Enabled = true,
                IntervalMinutes = 45,
                Prompt = "Check tasks.",
                QuietHours = new QuietHoursConfig { Enabled = true, Start = "22:00", End = "07:00", Timezone = "UTC" }
            },
            DateTimeInjection = new DateTimeInjectionConfig { Enabled = true, Timezone = "UTC", Format = "iso8601" },
            SessionAccessLevel = "allowlist",
            SessionAllowedAgents = ["existing-agent"],
            ConversationAccessLevel = "allowlist",
            ConversationAllowedAgents = ["existing-agent"],
            FileAccess = new FileAccessPolicy
            {
                AllowedReadPaths = [Path.Combine(Path.GetTempPath(), "read")],
                AllowedWritePaths = [Path.Combine(Path.GetTempPath(), "write")],
                DeniedPaths = [Path.Combine(Path.GetTempPath(), "deny")]
            },
            ExtensionConfig = new Dictionary<string, JsonElement>
            {
                ["botnexus-exec"] = JsonSerializer.Deserialize<JsonElement>("""{"shell":"pwsh"}""")
            },
            ShellCommand = ["pwsh", "-NoProfile", "-Command"]
        };

    private static void AssertEffectiveMatchesSubmitted(AgentDescriptor submitted, AgentDescriptor effective)
    {
        effective.DisplayName.ShouldBe(submitted.DisplayName);
        effective.Emoji.ShouldBe(submitted.Emoji);
        effective.Description.ShouldBe(submitted.Description);
        effective.ModelId.ShouldBe(submitted.ModelId);
        effective.ApiProvider.ShouldBe(submitted.ApiProvider);
        effective.SystemPromptFile.ShouldBe(submitted.SystemPromptFile);
        effective.SystemPromptFiles.ShouldBe(submitted.SystemPromptFiles);
        effective.ToolIds.ShouldBe(submitted.ToolIds);
        effective.AllowedModelIds.ShouldBe(submitted.AllowedModelIds);
        effective.SubAgentIds.ShouldBe(submitted.SubAgentIds);
        effective.SubAgentRoles.ShouldBe(submitted.SubAgentRoles);
        effective.IsolationStrategy.ShouldBe(submitted.IsolationStrategy);
        effective.CacheRetentionMode.ShouldBe(submitted.CacheRetentionMode);
        effective.Thinking.ShouldBe(submitted.Thinking);
        effective.ContextWindow.ShouldBe(submitted.ContextWindow);
        effective.MaxConcurrentSessions.ShouldBe(submitted.MaxConcurrentSessions);
        effective.Metadata["owner"].ShouldBe("team-gateway");
        effective.IsolationOptions["timeoutMs"].ShouldBe(1000L);
        effective.Kind.ShouldBe(AgentKind.Named);
        effective.ShellCommand.ShouldBe(submitted.ShellCommand);

        effective.Memory.ShouldNotBeNull();
        effective.Memory!.Enabled.ShouldBe(submitted.Memory!.Enabled);
        effective.Memory.Path.ShouldBe(submitted.Memory.Path);
        effective.Memory.Indexing.ShouldBe(submitted.Memory.Indexing);
        effective.Memory.PromptInjection.ShouldBe(submitted.Memory.PromptInjection);
        effective.Memory.Search!.DefaultTopK.ShouldBe(submitted.Memory.Search!.DefaultTopK);
        effective.Memory.Search.TemporalDecay!.HalfLifeDays.ShouldBe(submitted.Memory.Search.TemporalDecay!.HalfLifeDays);

        effective.Soul.ShouldNotBeNull();
        effective.Soul!.Enabled.ShouldBe(submitted.Soul!.Enabled);
        effective.Soul.Timezone.ShouldBe(submitted.Soul.Timezone);
        effective.Soul.DayBoundary.ShouldBe(submitted.Soul.DayBoundary);
        effective.Soul.ReflectionOnSeal.ShouldBe(submitted.Soul.ReflectionOnSeal);
        effective.Soul.ReflectionPrompt.ShouldBe(submitted.Soul.ReflectionPrompt);

        effective.Heartbeat.ShouldNotBeNull();
        effective.Heartbeat!.Enabled.ShouldBe(submitted.Heartbeat!.Enabled);
        effective.Heartbeat.IntervalMinutes.ShouldBe(submitted.Heartbeat.IntervalMinutes);
        effective.Heartbeat.Prompt.ShouldBe(submitted.Heartbeat.Prompt);
        effective.Heartbeat.QuietHours!.Start.ShouldBe(submitted.Heartbeat.QuietHours!.Start);
        effective.Heartbeat.QuietHours.End.ShouldBe(submitted.Heartbeat.QuietHours.End);

        effective.DateTimeInjection.ShouldNotBeNull();
        effective.DateTimeInjection!.Enabled.ShouldBe(submitted.DateTimeInjection!.Enabled);
        effective.DateTimeInjection.Timezone.ShouldBe(submitted.DateTimeInjection.Timezone);
        effective.DateTimeInjection.Format.ShouldBe(submitted.DateTimeInjection.Format);

        effective.SessionAccessLevel.ShouldBe(submitted.SessionAccessLevel);
        effective.SessionAllowedAgents.ShouldBe(submitted.SessionAllowedAgents);
        effective.ConversationAccessLevel.ShouldBe(submitted.ConversationAccessLevel);
        effective.ConversationAllowedAgents.ShouldBe(submitted.ConversationAllowedAgents);

        effective.FileAccess.ShouldNotBeNull();
        effective.FileAccess!.AllowedReadPaths.ShouldBe(submitted.FileAccess!.AllowedReadPaths);
        effective.FileAccess.AllowedWritePaths.ShouldBe(submitted.FileAccess.AllowedWritePaths);
        effective.FileAccess.DeniedPaths.ShouldBe(submitted.FileAccess.DeniedPaths);

        effective.ExtensionConfig.ShouldContainKey("botnexus-exec");
        JsonNode.DeepEquals(
            JsonNode.Parse(effective.ExtensionConfig["botnexus-exec"].GetRawText()),
            JsonNode.Parse("""{"shell":"pwsh"}""")).ShouldBeTrue();
    }

    private static ModelRegistry MakeModelRegistry()
    {
        var registry = new ModelRegistry();
        registry.Register("github-copilot", new LlmModel(
            Id: "reasoning-model",
            Name: "Reasoning Model",
            Api: "github-copilot-responses",
            Provider: "github-copilot",
            BaseUrl: "https://example.invalid",
            Reasoning: true,
            Input: ["text"],
            Cost: new ModelCost(0m, 0m, 0m, 0m),
            ContextWindow: 200_000,
            MaxTokens: 64_000,
            SupportsExtraHighThinking: true));
        registry.Register("github-copilot", new LlmModel(
            Id: "plain-model",
            Name: "Plain Model",
            Api: "github-copilot-completions",
            Provider: "github-copilot",
            BaseUrl: "https://example.invalid",
            Reasoning: false,
            Input: ["text"],
            Cost: new ModelCost(0m, 0m, 0m, 0m),
            ContextWindow: 128_000,
            MaxTokens: 16_000));
        return registry;
    }

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
            Directory.Delete(_rootPath, recursive: true);
    }
}
