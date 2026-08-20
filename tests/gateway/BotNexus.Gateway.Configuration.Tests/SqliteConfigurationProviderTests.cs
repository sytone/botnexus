using System.Text.Json.Nodes;
using BotNexus.Gateway.Configuration;
using BotNexus.Gateway.Configuration.Shadow;
using BotNexus.Gateway.Configuration.Store;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shouldly;

namespace BotNexus.Gateway.Configuration.Tests;

/// <summary>
/// Covers the SQLite configuration provider (#3485 D1): key parity with <c>AddJsonFile</c>,
/// precedence by registration order, hot reload through <c>IOptionsMonitor</c>, fail-safe reload,
/// and the refusal to write through <c>IConfiguration</c>.
/// </summary>
public sealed class SqliteConfigurationProviderTests
{
    /// <summary>
    /// In-memory store so the tests exercise the provider rather than SQLite. Failure injection is
    /// explicit because the fail-safe path is otherwise unreachable from a healthy store.
    /// </summary>
    private sealed class FakeStore : IConfigStore
    {
        private IReadOnlyDictionary<string, ConfigEntry> _entries = new Dictionary<string, ConfigEntry>();

        public Exception? ReadFailure { get; set; }

        public void SetDocument(JsonObject document)
            => _entries = ConfigDocumentFlattener.Flatten(document);

        public Task<IReadOnlyDictionary<string, ConfigEntry>> ReadEntriesAsync(CancellationToken cancellationToken = default)
            => ReadFailure is not null
                ? Task.FromException<IReadOnlyDictionary<string, ConfigEntry>>(ReadFailure)
                : Task.FromResult(_entries);

        public Task WriteDocumentAsync(JsonObject document, CancellationToken cancellationToken = default)
        {
            SetDocument(document);
            return Task.CompletedTask;
        }
    }

    private static JsonObject Document(string json) => JsonNode.Parse(json)!.AsObject();

    /// <summary>
    /// The parity assertion this whole design rests on: the provider's key space must match what the
    /// framework's own JSON provider produces for the same document. A hand-rolled dotted-to-colon
    /// translation passes a naive test and then diverges on arrays, which is why the comparison is
    /// against <c>AddJsonStream</c> rather than against a hand-written expectation.
    /// </summary>
    [Fact]
    public void KeySpace_MatchesJsonProvider_ForTheSameDocument()
    {
        const string json = """
            {
              "gateway": { "port": 8080, "defaultAgentId": "farnsworth" },
              "agents": { "defaults": { "tools": ["read", "write"] } },
              "nested": { "empty": {}, "deep": { "a": { "b": "c" } } }
            }
            """;

        var store = new FakeStore();
        store.SetDocument(Document(json));

        var fromStore = new ConfigurationBuilder().AddSqliteConfigStore(store).Build();
        var fromJson = new ConfigurationBuilder()
            .AddJsonStream(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json)))
            .Build();

        static Dictionary<string, string?> Flatten(IConfiguration config)
            => config.AsEnumerable()
                .Where(kv => kv.Value is not null)
                .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);

        Flatten(fromStore).ShouldBe(Flatten(fromJson), ignoreOrder: true);
    }

    /// <summary>
    /// Arrays are the specific case a dotted-path translation gets wrong: the store's flattener does
    /// not descend into them, so without delegating to the framework parser they would arrive as one
    /// opaque JSON string and a <c>List&lt;T&gt;</c> binding would yield an empty list.
    /// </summary>
    [Fact]
    public void Arrays_AreIndexedAsChildKeys_NotAnOpaqueString()
    {
        var store = new FakeStore();
        store.SetDocument(Document("""{ "agents": { "defaults": { "tools": ["read", "write"] } } }"""));

        var config = new ConfigurationBuilder().AddSqliteConfigStore(store).Build();

        config["agents:defaults:tools:0"].ShouldBe("read");
        config["agents:defaults:tools:1"].ShouldBe("write");
        config.GetSection("agents:defaults:tools").Get<List<string>>().ShouldBe(["read", "write"]);
    }

    /// <summary>
    /// Precedence is registration order, which is what replaces the ConfigStoreAuthoritative flag.
    /// </summary>
    [Fact]
    public void RegisteredAfterJson_StoreValuesWin()
    {
        var store = new FakeStore();
        store.SetDocument(Document("""{ "gateway": { "port": 9090 } }"""));

        var config = new ConfigurationBuilder()
            .AddJsonStream(new MemoryStream(System.Text.Encoding.UTF8.GetBytes("""{ "gateway": { "port": 8080 } }""")))
            .AddSqliteConfigStore(store)
            .Build();

        config["gateway:port"].ShouldBe("9090");
    }

    /// <summary>
    /// The inverse, so the ordering assertion above cannot pass by accident on a provider that always
    /// wins or is always ignored.
    /// </summary>
    [Fact]
    public void RegisteredBeforeJson_FileValuesWin()
    {
        var store = new FakeStore();
        store.SetDocument(Document("""{ "gateway": { "port": 9090 } }"""));

        var config = new ConfigurationBuilder()
            .AddSqliteConfigStore(store)
            .AddJsonStream(new MemoryStream(System.Text.Encoding.UTF8.GetBytes("""{ "gateway": { "port": 8080 } }""")))
            .Build();

        config["gateway:port"].ShouldBe("8080");
    }

    /// <summary>
    /// AC3: a store change is visible through <c>IOptionsMonitor</c> with no host restart. This is the
    /// capability the previous seam could not deliver - it redirected only the startup read.
    /// </summary>
    [Fact]
    public void StoreChange_IsObservedByOptionsMonitor_WithoutRestart()
    {
        var store = new FakeStore();
        store.SetDocument(Document("""{ "gateway": { "defaultAgentId": "before" } }"""));

        var source = new SqliteConfigurationSource { Store = store };
        var builder = new ConfigurationBuilder();
        builder.Add(source);
        var config = builder.Build();

        var provider = (SqliteConfigurationProvider)config.Providers.Single();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(config);
        services.AddOptions<GatewayOptions>().Bind(config.GetSection("gateway"));
        using var sp = services.BuildServiceProvider();

        var monitor = sp.GetRequiredService<IOptionsMonitor<GatewayOptions>>();
        monitor.CurrentValue.DefaultAgentId.ShouldBe("before");

        store.SetDocument(Document("""{ "gateway": { "defaultAgentId": "after" } }"""));
        provider.NotifyChanged();

        monitor.CurrentValue.DefaultAgentId.ShouldBe("after");
    }

    /// <summary>
    /// AC7 / #2358 parity: an unreadable store retains last-known-good data and does not throw. The
    /// framework's file provider clears Data and rethrows on a reload failure, on a background thread,
    /// which terminates the process - the hazard is identical here.
    /// </summary>
    [Fact]
    public void UnreadableStore_RetainsPreviousValues_AndDoesNotThrow()
    {
        var store = new FakeStore();
        store.SetDocument(Document("""{ "gateway": { "port": 8080 } }"""));

        string? reportedReason = null;
        var provider = new SqliteConfigurationProvider(store, (reason, _) => reportedReason = reason);
        provider.Load();
        provider.TryGet("gateway:port", out var before).ShouldBeTrue();
        before.ShouldBe("8080");

        store.ReadFailure = new InvalidOperationException("database is locked");
        Should.NotThrow(() => provider.Load());

        provider.TryGet("gateway:port", out var after).ShouldBeTrue();
        after.ShouldBe("8080");
        reportedReason.ShouldNotBeNull();
    }

    /// <summary>
    /// AC4. ConfigurationRoot.SetConfiguration loops over every registered provider, so a persisting
    /// Set would turn one assignment into N durable commits with no error.
    /// </summary>
    [Fact]
    public void Set_Throws_AndDoesNotMutateData()
    {
        var store = new FakeStore();
        store.SetDocument(Document("""{ "gateway": { "port": 8080 } }"""));

        var provider = new SqliteConfigurationProvider(store);
        provider.Load();

        var ex = Should.Throw<NotSupportedException>(() => provider.Set("gateway:port", "9999"));
        ex.Message.ShouldContain("gateway:port");

        provider.TryGet("gateway:port", out var value).ShouldBeTrue();
        value.ShouldBe("8080");
    }

    /// <summary>
    /// An empty store must not erase configuration supplied by a lower-precedence provider. This is
    /// the "empty store looks healthy" failure that the previous fallback design guarded against and
    /// that provider ordering must preserve.
    /// </summary>
    [Fact]
    public void EmptyStore_DoesNotEraseLowerPrecedenceValues()
    {
        var store = new FakeStore();

        var config = new ConfigurationBuilder()
            .AddJsonStream(new MemoryStream(System.Text.Encoding.UTF8.GetBytes("""{ "gateway": { "port": 8080 } }""")))
            .AddSqliteConfigStore(store)
            .Build();

        config["gateway:port"].ShouldBe("8080");
    }

    /// <summary>
    /// Explicit null must not resurface as a value. Rehydration drops Unset and Unknown but keeps
    /// ExplicitNull, and the JSON parser records a null leaf as an empty value rather than omitting
    /// the key - so the key exists with no value, which is distinct from absent.
    /// </summary>
    [Fact]
    public void ExplicitNull_IsPreservedAsAPresentKeyWithNoValue()
    {
        var store = new FakeStore();
        store.SetDocument(Document("""{ "gateway": { "defaultAgentId": null } }"""));

        var provider = new SqliteConfigurationProvider(store);
        provider.Load();

        provider.TryGet("gateway:defaultAgentId", out var value).ShouldBeTrue();
        value.ShouldBeNull();
    }
}
