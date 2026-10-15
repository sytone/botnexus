using System.Text.Json.Nodes;
using BotNexus.Cli.Commands;
using BotNexus.Gateway.Abstractions.Configuration;
using BotNexus.Gateway.Configuration;
using Spectre.Console;

namespace BotNexus.Cli.Tests;

/// <summary>Isolated CLI regressions for the named-agent precondition in issue #4916.</summary>
[Collection("AnsiConsole")]
public sealed class ConfigSetAgentExistenceTests : IDisposable
{
    private const string Seed = """
        {
          "version": 1,
          "unknownRootField": "keep-me",
          "gateway": {"listenUrl":"http://localhost:5099","defaultAgentId":"assistant"},
          "providers": {"copilot":{"enabled":true,"defaultModel":"gpt-4.1","apiKey":"seed-secret"}},
          "agents": {
            "defaults":{"provider":"copilot","model":"gpt-4.1"},
            "assistant":{"provider":"copilot","model":"gpt-4.1","heartbeat":null,"unknownAgentField":"canary"}
          }
        }
        """;
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "botnexus-4916", Guid.NewGuid().ToString("N"));
    private readonly string _path;
    private readonly string _storePath;
    private readonly StringWriter _output = new();
    private readonly IAnsiConsole _original = AnsiConsole.Console;

    public ConfigSetAgentExistenceTests()
    {
        Directory.CreateDirectory(_directory);
        _path = Path.Combine(_directory, "config.json");
        _storePath = ConfigStoreBootstrap.ResolveStorePath(_path, new System.IO.Abstractions.FileSystem());
        File.WriteAllText(_path, Seed);
        AnsiConsole.Console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Out = new AnsiConsoleOutput(_output), Interactive = InteractionSupport.No
        });
        AnsiConsole.Console.Profile.Width = 240;
    }

    public void Dispose()
    {
        AnsiConsole.Console = _original;
        ConfigStoreBootstrap.ReleaseConnections(_storePath);
        Directory.Delete(_directory, recursive: true);
        _output.Dispose();
    }

    private async Task EnableStoreAsync(bool sqlite)
    {
        if (sqlite)
            await ConfigStoreBootstrap.PopulateAsync(_storePath, JsonNode.Parse(Seed).ShouldBeOfType<JsonObject>());
    }

    private async Task<(string Document, string Revision, byte[] Projection)> SnapshotAsync()
    {
        var writer = CliConfigMutation.CreateWriter(_path);
        var document = await writer.ReadDocumentAsync();
        var (_, revision) = await writer.ReadPlatformConfigWithRevisionAsync();
        return (document.ToJsonString(), revision, await File.ReadAllBytesAsync(_path));
    }

    private async Task AssertUnchangedAsync((string Document, string Revision, byte[] Projection) before)
    {
        var after = await SnapshotAsync();
        after.Document.ShouldBe(before.Document);
        after.Revision.ShouldBe(before.Revision);
        after.Projection.ShouldBe(before.Projection);
        _output.ToString().ShouldNotContain("Config saved to:");
        _output.ToString().ShouldNotContain("Updated configuration backends:");
        _output.ToString().ShouldNotContain("Set ");
    }

    public static TheoryData<bool, string, string> UnknownAgents => new()
    {
        { false, "agents.assistant-typo.thinking", "high" },
        { true, "agents.assistant-typo.thinking", "high" },
        { false, "agents.ghost.provider", "copilot" },
        { true, "agents.ghost.provider", "copilot" },
        { false, "agents.ghost", "{\"provider\":\"copilot\",\"model\":\"gpt-4.1\"}" },
        { true, "agents.ghost", "{\"provider\":\"copilot\",\"model\":\"gpt-4.1\"}" },
        { false, "agents.ghost.heartbeat", "{\"enabled\":true}" },
        { true, "agents.ghost.heartbeat", "{\"enabled\":true}" },
        { false, " AGENTS . ghost . thinking ", "null" },
        { true, " AGENTS . ghost . thinking ", "null" },
        { false, "agents", "{\"assistant\":{\"provider\":\"copilot\",\"model\":\"gpt-4.1\"},\"ghost\":{\"provider\":\"copilot\",\"model\":\"gpt-4.1\"}}" },
        { true, "agents", "{\"assistant\":{\"provider\":\"copilot\",\"model\":\"gpt-4.1\"},\"ghost\":{\"provider\":\"copilot\",\"model\":\"gpt-4.1\"}}" }
    };

    [Theory]
    [MemberData(nameof(UnknownAgents))]
    public async Task Set_unknown_agent_rejects_before_typed_creation(bool sqlite, string path, string value)
    {
        await EnableStoreAsync(sqlite);
        var before = await SnapshotAsync();
        var resolver = new ObservingResolver();
        (await new ConfigCommands(resolver).ExecuteSetAsync(path, value, _path, false, CancellationToken.None)).ShouldBe(1);
        resolver.SetCalls.ShouldBe(0);
        _output.ToString().ShouldContain(path);
        _output.ToString().ShouldContain(path.Contains("assistant-typo", StringComparison.Ordinal) ? "assistant-typo" : "ghost");
        _output.ToString().ShouldContain("agents list");
        _output.ToString().ShouldContain("agents add");
        await AssertUnchangedAsync(before);
    }

    public static TheoryData<bool, string, string> InvalidSettings => new()
    {
        { false, "agents.assistant.notASetting", "true" },
        { true, "agents.assistant.notASetting", "true" },
        { false, "agents.assistant.contextWindow", "not-an-integer" },
        { true, "agents.assistant.contextWindow", "not-an-integer" },
        { false, "agents.assistant.enabled", "null" },
        { true, "agents.assistant.enabled", "null" },
        { false, "agents.assistant.compactionTokenCeiling", "-1" },
        { true, "agents.assistant.compactionTokenCeiling", "-1" }
    };

    [Theory]
    [MemberData(nameof(InvalidSettings))]
    public async Task Set_invalid_setting_preserves_authority_projection_and_revision(bool sqlite, string path, string value)
    {
        await EnableStoreAsync(sqlite);
        var before = await SnapshotAsync();
        (await new ConfigCommands(new ConfigPathResolver()).ExecuteSetAsync(path, value, _path, false, CancellationToken.None)).ShouldBe(1);
        await AssertUnchangedAsync(before);
    }

    public static TheoryData<bool, string, string> ValidSettings => new()
    {
        { false, "agents.assistant.thinking", "high" },
        { true, "agents.assistant.thinking", "high" },
        { false, "agents.assistant.compactionTokenCeiling", "null" },
        { true, "agents.assistant.compactionTokenCeiling", "null" },
        { false, "agents.assistant.heartbeat.enabled", "true" },
        { true, "agents.assistant.heartbeat.enabled", "true" },
        { false, "agents.assistant.heartbeat", "{\"enabled\":true}" },
        { true, "agents.assistant.heartbeat", "{\"enabled\":true}" },
        { false, "agents.defaults.thinking", "high" },
        { true, "agents.defaults.thinking", "high" },
        { false, "agents.assistant.extensions.acme", "{\"arbitrary\":{\"nested\":[1,null,true]}}" },
        { true, "agents.assistant.extensions.acme", "{\"arbitrary\":{\"nested\":[1,null,true]}}" }
    };

    [Theory]
    [MemberData(nameof(ValidSettings))]
    public async Task Set_existing_agent_or_defaults_preserves_optional_and_opaque_settings(bool sqlite, string path, string value)
    {
        await EnableStoreAsync(sqlite);
        var before = await SnapshotAsync();
        (await new ConfigCommands(new ConfigPathResolver()).ExecuteSetAsync(path, value, _path, false, CancellationToken.None)).ShouldBe(0);
        var document = await CliConfigMutation.ReadAsync(_path, CancellationToken.None);
        document.Exists(path).ShouldBeTrue();
        document.TryGetString("providers.copilot.apiKey", out var secret).ShouldBeTrue();
        secret.ShouldBe("seed-secret");
        var root = JsonNode.Parse(document.ToJsonString()).ShouldBeOfType<JsonObject>();
        root["unknownRootField"].ShouldNotBeNull().GetValue<string>().ShouldBe("keep-me");
        root["agents"].ShouldNotBeNull()["assistant"].ShouldNotBeNull()["unknownAgentField"].ShouldNotBeNull().GetValue<string>().ShouldBe("canary");
        var expected = JsonNode.Parse(before.Document).ShouldBeOfType<JsonObject>();
        var segments = path.Split('.');
        JsonNode cursor = expected;
        foreach (var segment in segments[..^1])
        {
            if (cursor[segment] is null)
                cursor[segment] = new JsonObject();
            cursor = cursor[segment].ShouldNotBeNull();
        }
        // Whole typed heartbeat replacement materializes its declared non-null defaults.
        // A leaf edit still only introduces that leaf under the previously null parent.
        cursor[segments[^1]] = path == "agents.assistant.heartbeat"
            ? JsonNode.Parse("{\"enabled\":true,\"intervalMinutes\":30,\"ackMaxChars\":300}")
            : value == "high" ? JsonValue.Create(value) : JsonNode.Parse(value);
        JsonNode.DeepEquals(root, expected).ShouldBeTrue();
        PlatformConfigLoader.ValidateRawJson(document.ToJsonString()).ShouldBeEmpty();
    }

    [Theory]
    [InlineData(false, "agents.assistant.provider", "copilot")]
    [InlineData(true, "agents.assistant.provider", "copilot")]
    [InlineData(false, "agents.assistant", "{\"provider\":\"copilot\",\"model\":\"gpt-4.1\"}")]
    [InlineData(true, "agents.assistant", "{\"provider\":\"copilot\",\"model\":\"gpt-4.1\"}")]
    [InlineData(false, "agents", "{\"assistant\":{\"provider\":\"copilot\",\"model\":\"gpt-4.1\"}}")]
    [InlineData(true, "agents", "{\"assistant\":{\"provider\":\"copilot\",\"model\":\"gpt-4.1\"}}")]
    public async Task Set_agent_removed_after_snapshot_does_not_recreate_it(bool sqlite, string path, string value)
    {
        await EnableStoreAsync(sqlite);
        using var resolver = new PausingResolver();
        var pending = Task.Run(() => new ConfigCommands(resolver).ExecuteSetAsync(path, value, _path, false, CancellationToken.None));
        try
        {
            await resolver.Coerced.Task.WaitAsync(TimeSpan.FromSeconds(10));
            // The CLI has already loaded and coerced the old typed snapshot. Commit the removal
            // through the real canonical writer before allowing the CLI's locked callback to run.
            (await CliConfigMutation.CreateWriter(_path).MutateDocumentValidatedAsync(document =>
            {
                return document.TryRemoveEntry("agents", "assistant", out var error) ? null : error;
            }, "test-concurrent-removal", CancellationToken.None)).ShouldBeEmpty();
            var removed = await SnapshotAsync();
            resolver.Resume.Set();
            (await pending.WaitAsync(TimeSpan.FromSeconds(10))).ShouldBe(1);
            _output.ToString().ShouldContain(path);
            _output.ToString().ShouldContain("assistant");
            _output.ToString().ShouldContain("agents list");
            _output.ToString().ShouldContain("agents add");
            await AssertUnchangedAsync(removed);
            (await CliConfigMutation.ReadAsync(_path, CancellationToken.None)).FindEntryKey("agents", "assistant").ShouldBeNull();
        }
        finally
        {
            resolver.Resume.Set();
            await pending.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Set_agents_collection_allows_existing_agents_and_reserved_defaults(bool sqlite)
    {
        await EnableStoreAsync(sqlite);
        const string value = "{\"assistant\":{\"provider\":\"copilot\",\"model\":\"gpt-4.1\"},\"defaults\":{\"thinking\":\"high\"}}";
        (await new ConfigCommands(new ConfigPathResolver()).ExecuteSetAsync("agents", value, _path, false, CancellationToken.None)).ShouldBe(0);
        var document = await CliConfigMutation.ReadAsync(_path, CancellationToken.None);
        document.GetEntryKeys("agents").Order().ShouldBe(new[] { "assistant", "defaults" });
        document.TryGetString("agents.defaults.thinking", out var thinking).ShouldBeTrue();
        thinking.ShouldBe("high");
        document.TryGetString("providers.copilot.apiKey", out var secret).ShouldBeTrue();
        secret.ShouldBe("seed-secret");
        PlatformConfigLoader.ValidateRawJson(document.ToJsonString()).ShouldBeEmpty();
    }

    private class ObservingResolver : IConfigPathResolver
    {
        private readonly ConfigPathResolver _inner = new();
        public int SetCalls { get; private set; }
        public bool TrySetValue(object config, string path, object? value, out string error)
        {
            SetCalls++;
            return _inner.TrySetValue(config, path, value, out error);
        }
        public virtual bool TryGetValue(object config, string path, out object? value, out string error)
            => _inner.TryGetValue(config, path, out value, out error);
        public IReadOnlyList<string> GetAvailablePaths(object config) => _inner.GetAvailablePaths(config);
    }

    private sealed class PausingResolver : ObservingResolver, IDisposable
    {
        public TaskCompletionSource<bool> Coerced { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim Resume { get; } = new(false);
        public override bool TryGetValue(object config, string path, out object? value, out string error)
        {
            var result = base.TryGetValue(config, path, out value, out error);
            Coerced.TrySetResult(true);
            if (!Resume.Wait(TimeSpan.FromSeconds(10)))
                throw new TimeoutException("Test did not release the CLI after concurrent removal.");
            return result;
        }
        public void Dispose() => Resume.Dispose();
    }
}
