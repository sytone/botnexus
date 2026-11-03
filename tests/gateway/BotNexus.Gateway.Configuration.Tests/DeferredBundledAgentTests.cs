using System.IO.Abstractions;
using System.Text.Json.Nodes;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Configuration.Store;
using BotNexus.Gateway.Configuration.Writers;
using Microsoft.Extensions.Logging;

namespace BotNexus.Gateway.Configuration.Tests;

/// <summary>Exercises startup reconciliation followed by ordinary canonical validated writes (#4934).</summary>
public sealed class DeferredBundledAgentTests : IDisposable
{
    private const string GuideId = BundledPlatformAgents.TrailguideAgentId;
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"bn-deferred-{Guid.NewGuid():N}");
    private readonly FileSystem _fileSystem = new();
    private string ConfigPath => Path.Combine(_directory, "config.json");
    private string StorePath => ConfigStoreBootstrap.ResolveStorePath(ConfigPath, _fileSystem);

    public DeferredBundledAgentTests() => Directory.CreateDirectory(_directory);

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Reconcile_WithoutEligiblePair_UnrelatedValidatedWriteSucceedsAcrossRestart(bool sqlite, bool disabledBaseline)
    {
        var root = InitialDocument();
        if (disabledBaseline)
            root["agents"].ShouldBeOfType<JsonObject>()["baseline"] = CompleteAgent(false);
        var writer = await SeedAsync(root, sqlite);
        var before = await File.ReadAllBytesAsync(ConfigPath);
        var logger = new RecordingLogger();

        await ReconcileAsync(writer, logger);
        await ReconcileAsync(writer, logger);

        // Deferral is a genuine no-op, not a rewrite of an incomplete disabled placeholder.
        (await File.ReadAllBytesAsync(ConfigPath)).ShouldBe(before);
        logger.Messages.ShouldContain(message => message.Contains("deferred", StringComparison.OrdinalIgnoreCase)
            && message.Contains("provider", StringComparison.Ordinal)
            && message.Contains("restart", StringComparison.OrdinalIgnoreCase));
        var errors = await SetUnrelatedAsync(writer);
        errors.ShouldBeEmpty();

        var restarted = ConfigWriterFactory.Create(ConfigPath, _fileSystem);
        await ReconcileAsync(restarted, logger);
        var actual = await restarted.ReadAsync();
        actual["agents"].ShouldBeOfType<JsonObject>().ContainsKey(GuideId).ShouldBeFalse();
        actual["gateway"]?["walCheckpointIntervalMinutes"]?.GetValue<int>().ShouldBe(6);
        if (disabledBaseline)
            JsonNode.DeepEquals(actual["agents"]?["baseline"], root["agents"]?["baseline"]).ShouldBeTrue();
        JsonNode.DeepEquals(JsonNode.Parse(await File.ReadAllTextAsync(ConfigPath)), actual).ShouldBeTrue();
        if (sqlite)
        {
            var entries = await new SqliteConfigStore($"Data Source={StorePath}").ReadEntriesAsync();
            JsonNode.DeepEquals(ConfigDocumentRehydrator.Rehydrate(entries), actual).ShouldBeTrue();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Reconcile_LaterEligiblePair_InsertsOnceAndPreservesCanonicalState(bool sqlite)
    {
        var writer = await SeedAsync(InitialDocument(), sqlite);
        var logger = new RecordingLogger();
        await ReconcileAsync(writer, logger);
        var errors = await writer.MutateValidatedAsync(root =>
        {
            root["agents"].ShouldBeOfType<JsonObject>()["baseline"] = CompleteAgent(true);
            return null;
        }, "operator-add-agent");
        errors.ShouldBeEmpty();

        await ReconcileAsync(writer, logger);
        var afterInsert = await File.ReadAllBytesAsync(ConfigPath);
        var restarted = ConfigWriterFactory.Create(ConfigPath, _fileSystem);
        await ReconcileAsync(restarted, logger);
        (await File.ReadAllBytesAsync(ConfigPath)).ShouldBe(afterInsert);
        var guide = ((await restarted.ReadAsync())["agents"]?[GuideId]).ShouldBeOfType<JsonObject>();
        guide["provider"]?.GetValue<string>().ShouldBe("github-copilot");
        guide["model"]?.GetValue<string>().ShouldBe("gpt-4.1");
        guide["enabled"]?.GetValue<bool>().ShouldBeTrue("preserve established valid-entry behavior");
        (await SetUnrelatedAsync(restarted)).ShouldBeEmpty();
        var actual = await ConfigWriterFactory.Create(ConfigPath, _fileSystem).ReadAsync();
        JsonNode.DeepEquals(JsonNode.Parse(await File.ReadAllTextAsync(ConfigPath)), actual).ShouldBeTrue();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Reconcile_ExistingIncompleteEntry_PreservesUserFieldsAndExplainsAtomicRecovery(bool sqlite)
    {
        var root = InitialDocument();
        var existing = new JsonObject
        {
            ["enabled"] = false,
            ["displayName"] = "My guide",
            ["description"] = "My custom description, not an ownership marker",
            ["definitionVersion"] = 987,
            ["opaque"] = new JsonObject { ["keep"] = true }
        };
        root["agents"].ShouldBeOfType<JsonObject>()[GuideId] = existing;
        var writer = await SeedAsync(root, sqlite);
        var before = await File.ReadAllBytesAsync(ConfigPath);
        var logger = new RecordingLogger();

        await ReconcileAsync(writer, logger);
        (await File.ReadAllBytesAsync(ConfigPath)).ShouldBe(before);
        logger.Messages.ShouldContain(message => message.Contains(GuideId, StringComparison.Ordinal)
            && message.Contains("PATCH /api/agents/", StringComparison.Ordinal)
            && message.Contains("provider", StringComparison.Ordinal)
            && message.Contains("model", StringComparison.Ordinal)
            && message.Contains("together", StringComparison.Ordinal));
        (await SetUnrelatedAsync(writer)).ShouldNotBeEmpty("existing invalid state must not bypass whole-document validation");
        var enableErrors = await AgentConfigurationEditor.ApplyAsync(writer, AgentId.From(GuideId),
            new AgentConfigurationPatch { Enabled = true }, CancellationToken.None);
        enableErrors.ShouldNotBeEmpty();
        (await File.ReadAllBytesAsync(ConfigPath)).ShouldBe(before);
        var partialErrors = await AgentConfigurationEditor.ApplyAsync(writer, AgentId.From(GuideId),
            new AgentConfigurationPatch { Provider = "github-copilot" }, CancellationToken.None);
        partialErrors.ShouldNotBeEmpty("both missing prerequisites must be corrected atomically");
        (await File.ReadAllBytesAsync(ConfigPath)).ShouldBe(before);

        // This is the existing operator API's canonical patch path, not startup auto-repair.
        var recoveryErrors = await AgentConfigurationEditor.ApplyAsync(writer, AgentId.From(GuideId),
            new AgentConfigurationPatch { Provider = "github-copilot", Model = "gpt-4.1" }, CancellationToken.None);
        recoveryErrors.ShouldBeEmpty();
        var restarted = ConfigWriterFactory.Create(ConfigPath, _fileSystem);
        await ReconcileAsync(restarted, logger);
        var recovered = (await restarted.ReadAsync())["agents"]?[GuideId];
        var expected = existing.DeepClone().AsObject();
        expected["provider"] = "github-copilot";
        expected["model"] = "gpt-4.1";
        JsonNode.DeepEquals(recovered, expected).ShouldBeTrue("all custom fields and the opt-out must survive explicit recovery");
        (await SetUnrelatedAsync(restarted)).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("null", false)]
    [InlineData("null", true)]
    [InlineData("42", false)]
    [InlineData("42", true)]
    [InlineData("\"invalid entry\"", false)]
    [InlineData("\"invalid entry\"", true)]
    public async Task Reconcile_NonObjectEntry_PreservesValueAndDoesNotAdvertiseUnsupportedPatch(string json, bool sqlite)
    {
        var root = InitialDocument();
        root["agents"].ShouldBeOfType<JsonObject>()[GuideId] = JsonNode.Parse(json);
        var writer = await SeedAsync(root, sqlite);
        var before = await File.ReadAllBytesAsync(ConfigPath);
        var logger = new RecordingLogger();

        await ReconcileAsync(writer, logger);

        var warning = logger.Messages.Where(message => message.Contains("not a configuration object", StringComparison.Ordinal))
            .ShouldHaveSingleItem();
        warning.ShouldContain(GuideId);
        warning.ShouldContain("left unchanged");
        warning.ShouldContain("support");
        warning.ShouldContain("Do not edit the generated JSON projection");
        logger.Messages.ShouldNotContain(message => message.Contains("PATCH", StringComparison.Ordinal));
        (await File.ReadAllBytesAsync(ConfigPath)).ShouldBe(before);
        var restarted = ConfigWriterFactory.Create(ConfigPath, _fileSystem);
        var actual = await restarted.ReadAsync();
        actual["agents"].ShouldBeOfType<JsonObject>().ContainsKey(GuideId).ShouldBeTrue();
        JsonNode.DeepEquals(actual["agents"]?[GuideId], JsonNode.Parse(json)).ShouldBeTrue();
        var errors = await AgentConfigurationEditor.ApplyAsync(restarted, AgentId.From(GuideId),
            new AgentConfigurationPatch { Provider = "github-copilot", Model = "gpt-4.1" }, CancellationToken.None);
        errors.ShouldContain("Agent not found.");
        (await File.ReadAllBytesAsync(ConfigPath)).ShouldBe(before);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OrdinaryDisabledAgent_MissingPrerequisites_StillRejectsValidatedWrites(bool enabled)
    {
        var root = InitialDocument();
        root["agents"].ShouldBeOfType<JsonObject>()["ordinary"] = new JsonObject { ["enabled"] = enabled };
        var writer = await SeedAsync(root, sqlite: false);
        var before = await File.ReadAllBytesAsync(ConfigPath);
        var errors = await SetUnrelatedAsync(writer);
        errors.ShouldContain(error => error.Contains("agents.ordinary.provider is required", StringComparison.Ordinal));
        errors.ShouldContain(error => error.Contains("agents.ordinary.model is required", StringComparison.Ordinal));
        (await File.ReadAllBytesAsync(ConfigPath)).ShouldBe(before);
    }

    [Fact]
    public void BuildEntry_UnresolvedPair_DoesNotMaterializeAnIncompletePlaceholder()
        => PlatformAgentReconciliationService.BuildEntry(BundledPlatformAgents.Trailguide, null).ShouldBeNull();

    [Theory]
    [InlineData("", "gpt-4.1")]
    [InlineData("github-copilot", " ")]
    public void BuildEntry_BlankPrerequisite_DoesNotMaterializeAnIncompletePlaceholder(string provider, string model)
        => PlatformAgentReconciliationService.BuildEntry(BundledPlatformAgents.Trailguide, (provider, model)).ShouldBeNull();

    [Theory]
    [InlineData(null, null)]
    [InlineData(" ", "")]
    [InlineData("openai", "gpt-4.1")]
    public void FreshInstall_BuildEntryCaller_ProducesOnlyCompleteEntries(string? provider, string? model)
    {
        var agents = FreshInstallAgentDefaults.CreateAgents(provider, model);
        agents.ContainsKey(GuideId).ShouldBeTrue();
        foreach (var (_, node) in agents)
        {
            var entry = node.ShouldBeOfType<JsonObject>();
            entry["provider"]?.GetValue<string>().ShouldBe(string.IsNullOrWhiteSpace(provider)
                ? FreshInstallAgentDefaults.DefaultProvider : provider);
            entry["model"]?.GetValue<string>().ShouldBe(string.IsNullOrWhiteSpace(model)
                ? FreshInstallAgentDefaults.DefaultModel : model);
            entry["enabled"]?.GetValue<bool>().ShouldBeTrue();
        }
    }

    private static JsonObject InitialDocument() => new()
    {
        ["gateway"] = new JsonObject { ["listenUrl"] = "http://localhost:5000" },
        ["agents"] = new JsonObject()
    };

    private static JsonObject CompleteAgent(bool enabled) => new()
    {
        ["provider"] = "github-copilot", ["model"] = "gpt-4.1", ["enabled"] = enabled
    };

    private async Task<PlatformConfigWriter> SeedAsync(JsonObject root, bool sqlite)
    {
        await File.WriteAllTextAsync(ConfigPath, root.ToJsonString());
        if (sqlite)
            await ConfigStoreBootstrap.PopulateAsync(StorePath, root);
        return ConfigWriterFactory.Create(ConfigPath, _fileSystem);
    }

    private static Task ReconcileAsync(PlatformConfigWriter writer, ILogger logger)
        => new PlatformAgentReconciliationService(writer, BundledPlatformAgents.All, logger).StartAsync(CancellationToken.None);

    private static Task<IReadOnlyList<string>> SetUnrelatedAsync(PlatformConfigWriter writer)
        => writer.MutateDocumentValidatedAsync(document =>
        {
            document.Set("gateway.walCheckpointIntervalMinutes", 6);
            return null;
        }, "unrelated-operator-write");

    public void Dispose()
    {
        SqlitePoolCleanup.ClearPoolFor(StorePath);
        Directory.Delete(_directory, recursive: true);
    }

    private sealed class RecordingLogger : ILogger
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
    }
}
