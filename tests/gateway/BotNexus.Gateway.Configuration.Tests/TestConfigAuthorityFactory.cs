using System.IO.Abstractions;
using System.Text.Json.Nodes;
using BotNexus.Gateway.Configuration.Store;

namespace BotNexus.Gateway.Configuration.Tests;

/// <summary>Explicit nonphysical host used by connected tests; never installed as production fallback.</summary>
internal sealed class TestConfigAuthorityFactory : IConfigAuthorityFactory
{
    private readonly Dictionary<string, ConfigAuthorityBinding> _authorities = new(StringComparer.Ordinal);
    public List<(string Path, IFileSystem FileSystem)> Opens { get; } = [];

    public Task<ConfigAuthorityBinding> OpenAsync(string configPath, IFileSystem fileSystem,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_authorities)
        {
            Opens.Add((configPath, fileSystem));
            if (!_authorities.TryGetValue(configPath, out var authority))
            {
                // Explicit one-time bootstrap only. Later reads/mutations never consult projection.
                var document = fileSystem.File.Exists(configPath)
                    ? JsonNode.Parse(fileSystem.File.ReadAllText(configPath)).ShouldBeOfType<JsonObject>()
                    : new JsonObject();
                authority = new(new TestConfigStore(document), "injected:" + configPath);
                _authorities.Add(configPath, authority);
            }
            return Task.FromResult(authority);
        }
    }
}

/// <summary>A coherent complete authority with raw shape and revision semantics for test injection.</summary>
internal sealed class TestConfigStore(JsonObject document) : IConfigStore
{
    private JsonObject _document = document.DeepClone().AsObject();
    private long _revision;
    private readonly object _gate = new();

    public Task<ConfigStoreSnapshot> ReadSnapshotAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
            return Task.FromResult(new ConfigStoreSnapshot(_revision, ConfigDocumentFlattener.Flatten(_document)));
    }

    public Task WriteDocumentAsync(JsonObject document, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            _document = document.DeepClone().AsObject();
            _revision++;
        }
        return Task.CompletedTask;
    }

    public Task ApplyChangesAsync(ConfigChangeSet changes, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var candidate = _document.DeepClone().AsObject();
            candidate.Apply(changes);
            _document = candidate;
            if (!changes.IsEmpty)
                _revision++;
        }
        return Task.CompletedTask;
    }
}
