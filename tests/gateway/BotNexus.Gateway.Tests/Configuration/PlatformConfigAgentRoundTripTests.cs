using System.IO.Abstractions;
using System.Text.Json;
using System.Text.Json.Nodes;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Registry;
using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Configuration;
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
    // Key-preservation fitness function (#3560)
    //
    // Distinct from FieldParity_EveryDescriptorProperty_HasAnExplicitPersistenceDecision
    // below, and BOTH are required. That fence asks "has someone made a decision about this
    // descriptor property?" - it is a property-CLASSIFICATION guarantee. These tests ask
    // "does a save preserve the keys that were already stored?" - a key-PRESERVATION
    // guarantee.
    //
    // The distinction is not academic. On 2026-08-26 a single-scalar PUT /api/agents/aurum
    // removed eleven stored keys (the whole extensions bag, maxConcurrentSessions: 0) and
    // rewrote @-aliased fileAccess paths to machine-specific absolutes. ExtensionConfig,
    // MaxConcurrentSessions and FileAccess are all in the Persisted set, so the parity fence
    // was GREEN across that defect and would be green across any future variant of it. The
    // property was classified; the round trip was lossy.
    //
    // Expectations below derive from the seeded document, not from a hardcoded property
    // list, so a key added to config in future is covered without editing these tests.
    // ------------------------------------------------------------------

    [Fact]
    public async Task Update_ChangingOneScalar_LosesNoStoredKeyUnderTheAgent()
    {
        SeedConfigWithUnmodelledAgentKeys();
        var before = FlattenAgent("fence-agent");

        await ChangeOneScalarAsync("fence-agent", "Renamed Via Portal");

        var after = FlattenAgent("fence-agent");
        var lost = before.Keys.Except(after.Keys).OrderBy(k => k, StringComparer.Ordinal).ToList();

        lost.ShouldBeEmpty(
            "A save must not remove stored configuration the AgentDescriptor does not model. " +
            "Lost keys: " + string.Join(", ", lost) +
            ". This is the #3547 defect class - see PlatformConfigAgentWriter's SetExtensions, " +
            "SetOptionalInt and SetFileAccess.");
    }

    [Fact]
    public async Task Update_ChangingOneScalar_RewritesNoUntouchedValue()
    {
        SeedConfigWithUnmodelledAgentKeys();
        var before = FlattenAgent("fence-agent");

        await ChangeOneScalarAsync("fence-agent", "Renamed Via Portal");

        var after = FlattenAgent("fence-agent");

        // displayName is the one key the edit targets; everything else must be byte-identical.
        // A normalise-on-read / write-back-resolved asymmetry (alias -> absolute path) fails
        // here rather than passing silently, because the VALUE changed even though the KEY
        // survived. Key-presence alone would not catch it.
        var rewritten = before.Keys
            .Where(k => k != "displayName")
            .Where(k => after.TryGetValue(k, out var now) && now != before[k])
            .Select(k => $"{k}: '{before[k]}' -> '{after[k]}'")
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();

        rewritten.ShouldBeEmpty(
            "A save must not rewrite stored values it was not asked to change. Rewritten: " +
            string.Join("; ", rewritten));
    }

    [Fact]
    public async Task Update_ChangingOneScalar_PreservesFalsyButLegitimateScalars()
    {
        SeedConfigWithUnmodelledAgentKeys();

        await ChangeOneScalarAsync("fence-agent", "Renamed Via Portal");

        var after = FlattenAgent("fence-agent");

        // 0 / false / "" are legitimate stored values, not "unset". SetOptionalInt's <= 0
        // sentinel treated maxConcurrentSessions: 0 as absent and deleted it (#3547).
        // enabled:true is required - PlatformConfigAgentSource skips disabled agents on load
        // (PlatformConfigAgentSource.cs:165), so a disabled seed cannot be read back to edit.
        // The falsy-scalar coverage this fence needs is carried by maxConcurrentSessions: 0 and
        // extensions.custom-ext.{emptyString,zero,flag} below.
        after.ShouldContainKeyAndValue("maxConcurrentSessions", "0");
        after.ShouldContainKeyAndValue("extensions.custom-ext.emptyString", "");
        after.ShouldContainKeyAndValue("extensions.custom-ext.zero", "0");
        after.ShouldContainKeyAndValue("extensions.custom-ext.flag", "false");
    }

    [Fact]
    public async Task Update_ChangingOneScalar_PreservesPathAliasesRatherThanResolvedPaths()
    {
        SeedConfigWithUnmodelledAgentKeys();

        await ChangeOneScalarAsync("fence-agent", "Renamed Via Portal", FenceLocationResolver.Instance);

        var after = FlattenAgent("fence-agent");

        // The source resolves @alias -> absolute on read; without preservation the writer
        // persists the resolved form, silently making a portable config machine-specific.
        after.ShouldContainKeyAndValue("fileAccess.allowedReadPaths.0", "@fence-location");
        after.ShouldContainKeyAndValue("fileAccess.allowedWritePaths.0", "@fence-location");
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

    private void SeedConfigWithUnmodelledAgentKeys()
    {
        // Deliberately contains keys AgentDescriptor does not model at all
        // (extensions.custom-ext.*, extensions.botnexus-skills.unknownNested.*), a modelled
        // section carrying an unknown child, and falsy-but-legitimate scalars. The
        // assertions above derive from THIS document rather than a property list, so adding
        // a key here extends coverage without touching the tests.
        const string seed = """
            {
              "version": 1,
              "agents": {
                "fence-agent": {
                  "provider": "github-copilot",
                  "model": "reasoning-model",
                  "displayName": "Fence Agent",
                  "enabled": true,
                  "maxConcurrentSessions": 0,
                  "fileAccess": {
                    "allowedReadPaths": [ "@fence-location" ],
                    "allowedWritePaths": [ "@fence-location" ]
                  },
                  "extensions": {
                    "custom-ext": {
                      "emptyString": "",
                      "zero": 0,
                      "flag": false,
                      "nested": { "deep": "keep-me" }
                    },
                    "botnexus-skills": {
                      "allowSkillCreation": true,
                      "unknownNested": { "alsoKeepMe": "yes" }
                    }
                  }
                }
              }
            }
            """;
        _fileSystem.File.WriteAllText(_configPath, seed);
    }

    /// <summary>
    /// Performs a real read-modify-write: loads the agent through the production
    /// <see cref="PlatformConfigAgentSource"/>, changes exactly one scalar, and saves it back
    /// through the production <see cref="PlatformConfigAgentWriter"/>. This is the shape of
    /// the portal edit that lost eleven keys on 2026-08-26.
    /// </summary>
    private async Task ChangeOneScalarAsync(
        string agentId,
        string newDisplayName,
        ILocationResolver? locationResolver = null)
    {
        var loaded = await PlatformConfigLoader.LoadAsync(
            _configPath, CancellationToken.None, validateOnLoad: false, fileSystem: _fileSystem);

        var source = new PlatformConfigAgentSource(
            new TestOptionsMonitor<PlatformConfig>(loaded),
            _rootPath,
            new NullLogger<PlatformConfigAgentSource>(),
            locationResolver: locationResolver,
            modelRegistry: MakeModelRegistry());

        var descriptor = (await source.LoadAsync()).Single(d => d.AgentId.Value == agentId)
            with { DisplayName = newDisplayName };

        var writer = new PlatformConfigAgentWriter(
            new PlatformConfigWriter(_configPath, _fileSystem), _home, locationResolver);
        await writer.SaveAsync(descriptor, CancellationToken.None);
    }

    /// <summary>
    /// Flattens one agent's stored subtree to dotted key -> raw value, so assertions compare
    /// key sets and values rather than object graphs.
    /// </summary>
    private Dictionary<string, string> FlattenAgent(string agentId)
    {
        var agent = ReadConfigRoot()["agents"]!.AsObject()[agentId]!;
        var flat = new Dictionary<string, string>(StringComparer.Ordinal);
        Flatten(agent, prefix: string.Empty, flat);
        return flat;
    }

    private static void Flatten(JsonNode? node, string prefix, Dictionary<string, string> into)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (key, child) in obj)
                    Flatten(child, prefix.Length == 0 ? key : $"{prefix}.{key}", into);
                break;
            case JsonArray arr:
                for (var i = 0; i < arr.Count; i++)
                    Flatten(arr[i], $"{prefix}.{i}", into);
                break;
            default:
                into[prefix] = node?.ToJsonString().Trim('"') ?? "null";
                break;
        }
    }

    /// <summary>
    /// Minimal <see cref="ILocationResolver"/> exposing one filesystem location, so the
    /// alias-preservation assertion exercises the real resolve-on-read path rather than a
    /// null-resolver shortcut.
    /// </summary>
    private sealed class FenceLocationResolver : ILocationResolver
    {
        internal static readonly FenceLocationResolver Instance = new();

        private static readonly string ResolvedPath =
            Path.Combine(Path.GetTempPath(), "botnexus-fence-location");

        public Location? Resolve(string locationName)
            => locationName == "fence-location"
                ? new Location
                {
                    Name = "fence-location",
                    Type = LocationType.FileSystem,
                    Path = ResolvedPath
                }
                : null;

        public string? ResolvePath(string locationName)
            => locationName == "fence-location" ? ResolvedPath : null;

        public IReadOnlyList<Location> GetAll()
            => [Resolve("fence-location")!];
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
            // #3596: the agent-owned summary must round-trip on the same path as every other field.
            Summary = "Currently triaging platform issues and shipping fixes.",
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
                // #2423: both properties must survive save -> reload -> descriptor projection.
                AckMaxChars = 175,
                QuietHours = new QuietHoursConfig { Enabled = true, Start = "22:00", End = "07:00", Timezone = "UTC" },
                ActiveHours = new ActiveHoursConfig { Start = "08:30", End = "18:45", Timezone = "Europe/London" }
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
        effective.Summary.ShouldBe(submitted.Summary);
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
        effective.Heartbeat.AckMaxChars.ShouldBe(submitted.Heartbeat.AckMaxChars);
        effective.Heartbeat.ActiveHours.ShouldNotBeNull();
        effective.Heartbeat.ActiveHours!.Start.ShouldBe(submitted.Heartbeat.ActiveHours!.Start);
        effective.Heartbeat.ActiveHours.End.ShouldBe(submitted.Heartbeat.ActiveHours.End);
        effective.Heartbeat.ActiveHours.Timezone.ShouldBe(submitted.Heartbeat.ActiveHours.Timezone);

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
