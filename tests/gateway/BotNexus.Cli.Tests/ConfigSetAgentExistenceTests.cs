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
        // false exercises legacy bootstrap; true starts with prepopulated SQLite authority.
        File.Exists(_storePath).ShouldBe(sqlite);
        var phases = new List<string>();
        (string Document, string Revision, byte[] Projection)? removed = null;
        var resolver = new AfterCoercionResolver(async () =>
        {
            removed = await RemoveAgentAndSnapshotAsync(phases);
        }, phases);

        // The resolver callback completes on this call stack before ApplyAsync can acquire its
        // locks. No second CLI task, release signal, diagnostic clock, or cleanup await is needed.
        (await new ConfigCommands(resolver).ExecuteSetAsync(path, value, _path, false, CancellationToken.None)).ShouldBe(1);
        phases.Add("cli-rejected");
        resolver.SetCalls.ShouldBe(1);
        resolver.CallbackCalls.ShouldBe(1);
        phases.ShouldBe(new[] { "coerced", "removal-committed", "removed-snapshot", "callback-returned", "cli-rejected" });
        _output.ToString().ShouldContain(path);
        _output.ToString().ShouldContain("assistant");
        _output.ToString().ShouldContain("agents list");
        _output.ToString().ShouldContain("agents add");
        await AssertUnchangedAsync(removed ?? throw new InvalidOperationException("Removal snapshot was not captured."));
        (await CliConfigMutation.ReadAsync(_path, CancellationToken.None)).FindEntryKey("agents", "assistant").ShouldBeNull();
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

    private async Task<(string Document, string Revision, byte[] Projection)> RemoveAgentAndSnapshotAsync(List<string> phases)
    {
        var writer = CliConfigMutation.CreateWriter(_path);
        var backend = writer.Backends.ShouldHaveSingleItem();
        backend.Name.ShouldBe("sqlite");
        Path.GetFullPath(backend.Location).ShouldBe(Path.GetFullPath(_storePath));
        backend.WinsOnRead.ShouldBeTrue();
        (await writer.ReadDocumentAsync()).FindEntryKey("agents", "assistant").ShouldNotBeNull();
        (await writer.MutateDocumentValidatedAsync(document =>
        {
            return document.TryRemoveEntry("agents", "assistant", out var error) ? null : error;
        }, "test-concurrent-removal", CancellationToken.None)).ShouldBeEmpty();
        phases.Add("removal-committed");
        (await writer.ReadDocumentAsync()).FindEntryKey("agents", "assistant").ShouldBeNull();
        var removed = await SnapshotAsync();
        phases.Add("removed-snapshot");
        return removed;
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public async Task Set_after_coercion_callback_failure_or_abort_preserves_state(
        bool sqlite, bool removeFirst, bool cancel)
    {
        await EnableStoreAsync(sqlite);
        File.Exists(_storePath).ShouldBe(sqlite);
        var before = await SnapshotAsync();
        var phases = new List<string>();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Exception primary = cancel
            ? new OperationCanceledException("Callback aborted before CLI mutation.", cancellation.Token)
            : new InvalidOperationException("Primary callback failure before CLI mutation.");
        var resolver = new AfterCoercionResolver(async () =>
        {
            if (removeFirst)
                before = await RemoveAgentAndSnapshotAsync(phases);
            phases.Add("callback-failed");
            // Return a faulted task as well as covering the real async writer/snapshot above.
            // The synchronous bridge must unwrap this exact exception, not AggregateException.
            await Task.FromException(primary);
        }, phases);

        Func<Task> command = () => new ConfigCommands(resolver).ExecuteSetAsync(
            "agents.assistant.provider", "copilot", _path, false, CancellationToken.None);
        var observed = cancel
            ? await Should.ThrowAsync<OperationCanceledException>(command)
            : (Exception)await Should.ThrowAsync<InvalidOperationException>(command);
        ReferenceEquals(observed, primary).ShouldBeTrue();
        if (observed is OperationCanceledException aborted)
            aborted.CancellationToken.ShouldBe(cancellation.Token);
        resolver.SetCalls.ShouldBe(1);
        resolver.CallbackCalls.ShouldBe(1);
        phases.ShouldBe(removeFirst
            ? new[] { "coerced", "removal-committed", "removed-snapshot", "callback-failed" }
            : new[] { "coerced", "callback-failed" });
        await AssertUnchangedAsync(before);
        var agent = (await CliConfigMutation.ReadAsync(_path, CancellationToken.None)).FindEntryKey("agents", "assistant");
        if (removeFirst)
            agent.ShouldBeNull();
        else
            agent.ShouldBe("assistant");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task After_coercion_callback_is_one_shot_and_restores_context_on_success_or_failure(bool fail)
    {
        var config = await CliConfigMutation.CreateWriter(_path).ReadPlatformConfigAsync();
        var phases = new List<string>();
        var primary = new InvalidOperationException("Primary callback failure.");
        var resolver = new AfterCoercionResolver(() =>
        {
            SynchronizationContext.Current.ShouldBeNull();
            TaskScheduler.Current.ShouldBe(TaskScheduler.Default);
            return fail ? Task.FromException(primary) : Task.CompletedTask;
        }, phases);
        resolver.TrySetValue(config, "agents.assistant.provider", "copilot", out var error).ShouldBeTrue(error);
        var original = SynchronizationContext.Current;
        var callerContext = new SynchronizationContext();
        try
        {
            SynchronizationContext.SetSynchronizationContext(callerContext);
            Action read = () => resolver.TryGetValue(config, "agents.assistant.provider", out _, out _);
            if (fail)
                ReferenceEquals(Should.Throw<InvalidOperationException>(read), primary).ShouldBeTrue();
            else
                read();
            ReferenceEquals(SynchronizationContext.Current, callerContext).ShouldBeTrue();
            Should.Throw<InvalidOperationException>(read).Message.ShouldContain("one-shot");
            resolver.CallbackCalls.ShouldBe(1);
            ReferenceEquals(SynchronizationContext.Current, callerContext).ShouldBeTrue();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(original);
        }
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

    private sealed class AfterCoercionResolver(Func<Task> afterCoercion, List<string> phases) : ObservingResolver
    {
        private Func<Task>? _afterCoercion = afterCoercion;
        public int CallbackCalls { get; private set; }

        public override bool TryGetValue(object config, string path, out object? value, out string error)
        {
            var result = base.TryGetValue(config, path, out value, out error);
            result.ShouldBeTrue(error);
            SetCalls.ShouldBe(1);
            phases.Add("coerced");
            var callback = Interlocked.Exchange(ref _afterCoercion, null)
                ?? throw new InvalidOperationException("The after-coercion callback is one-shot.");

            // IConfigPathResolver is synchronous, but the canonical writer/snapshot are async.
            // Start the entire callback with no SynchronizationContext, not merely a final
            // ConfigureAwait(false): nested writer awaits must not capture xUnit's blocked
            // context. A custom TaskScheduler could pose the same cycle, so fail before calling
            // the callback in that unsupported environment. No Task.Run or second worker is
            // queued; I/O continuations use the default scheduler and need no test-side resume.
            TaskScheduler.Current.ShouldBe(TaskScheduler.Default);
            var context = SynchronizationContext.Current;
            try
            {
                SynchronizationContext.SetSynchronizationContext(null);
                CallbackCalls++;
                callback().GetAwaiter().GetResult();
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(context);
            }

            // GetAwaiter preserves the original exception; finally only restores context, never
            // awaits another task that could replace it. The callback is terminal before return
            // or throw, so fixture disposal cannot race an outstanding CLI/removal operation.
            phases.Add("callback-returned");
            return result;
        }
    }
}
