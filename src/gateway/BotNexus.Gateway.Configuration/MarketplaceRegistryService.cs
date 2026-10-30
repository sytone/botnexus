using System.IO.Abstractions;
using System.Text.Json;
using System.Text.Json.Nodes;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Configuration.Writers;

namespace BotNexus.Gateway.Configuration;

/// <summary>A persisted Git source registration. This is metadata, not installed plugin content.</summary>
/// <param name="Id">Canonical registration identity.</param>
/// <param name="Source">Credential-free absolute Git repository URL.</param>
/// <param name="Reference">Optional branch, tag, or commit; null selects the repository default.</param>
/// <param name="Enabled">Whether future marketplace discovery may use this registration.</param>
public sealed record MarketplaceRegistrationInfo(string Id, string Source, string? Reference, bool Enabled);

/// <summary>
/// Owns machine-global marketplace metadata in world extension configuration. No operation clones,
/// installs, executes, or uninstalls plugins. All mutations use the canonical configuration writer.
/// </summary>
public sealed class MarketplaceRegistryService
{
    /// <summary>The extension configuration namespace shared by all gateway and CLI callers.</summary>
    public const string ExtensionId = "botnexus-plugins-api";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly PlatformConfigWriter _writer;

    /// <summary>Uses the same home-rooted configuration identity as other platform configuration services.</summary>
    public MarketplaceRegistryService(string configPath, IFileSystem fileSystem) =>
        _writer = ConfigWriterFactory.Create(configPath, fileSystem);

    /// <summary>Uses the gateway's canonical writer, including its resolved configuration identity and backends.</summary>
    public MarketplaceRegistryService(PlatformConfigWriter writer) =>
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));

    /// <summary>Registers a source atomically, rejecting duplicate identities without touching plugin content.</summary>
    public Task AddAsync(MarketplaceId id, string source, string? reference, CancellationToken ct = default)
    {
        ValidateSource(source);
        ValidateReference(reference);
        return _writer.MutateAsync(root =>
        {
            var marketplaces = Section(root, create: true);
            if (marketplaces.ContainsKey(id.Value))
                throw new InvalidOperationException($"Marketplace '{id}' already exists.");
            marketplaces[id.Value] = JsonSerializer.SerializeToNode(new Registration(source, reference, true), JsonOptions);
        }, "marketplace-add", ct);
    }

    /// <summary>Reads current canonical state rather than a startup snapshot or generated projection.</summary>
    public async Task<IReadOnlyList<MarketplaceRegistrationInfo>> ListAsync(CancellationToken ct = default)
    {
        // Read the canonical raw document so reads and writes use identical validation and
        // property lookup. A permissive typed binder must not hide malformed or ambiguous data.
        var marketplaces = Section(await _writer.ReadAsync(ct), create: false);
        return marketplaces.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x =>
        {
            var registration = x.Value as JsonObject
                ?? throw InvalidConfiguration($"marketplaces.{x.Key}", "must be an object");
            return new MarketplaceRegistrationInfo(x.Key,
                registration[PropertyName(registration, "source")]?.GetValue<string>()
                    ?? throw InvalidConfiguration($"marketplaces.{x.Key}.source", "must be a credential-free repository URL"),
                registration[PropertyName(registration, "reference")]?.GetValue<string>(),
                registration[PropertyName(registration, "enabled")]?.GetValue<bool>() ?? true);
        }).ToArray();
    }

    /// <summary>Returns one registration, or throws when it no longer exists.</summary>
    public async Task<MarketplaceRegistrationInfo> GetAsync(MarketplaceId id, CancellationToken ct = default) =>
        (await ListAsync(ct)).FirstOrDefault(x => x.Id == id.Value) ?? throw Missing(id);

    /// <summary>Replaces the source and optional reference while preserving enabled state and sibling metadata.</summary>
    public Task UpdateAsync(MarketplaceId id, string source, string? reference, CancellationToken ct = default)
    {
        ValidateSource(source);
        ValidateReference(reference);
        return _writer.MutateAsync(root =>
        {
            var registration = Required(root, id);
            registration[PropertyName(registration, "source")] = source;
            registration[PropertyName(registration, "reference")] = reference;
        }, "marketplace-update", ct);
    }

    /// <summary>Changes future source availability only; installed plugins and their preferences are unchanged.</summary>
    public Task SetEnabledAsync(MarketplaceId id, bool enabled, CancellationToken ct = default) =>
        _writer.MutateAsync(root =>
        {
            var registration = Required(root, id);
            registration[PropertyName(registration, "enabled")] = enabled;
        }, "marketplace-set-enabled", ct);

    /// <summary>Deletes only this registration; never uninstalls plugins or deletes repository contents.</summary>
    public Task RemoveAsync(MarketplaceId id, CancellationToken ct = default) =>
        _writer.MutateAsync(root =>
        {
            if (!Section(root, create: false).Remove(id.Value)) throw Missing(id);
        }, "marketplace-remove", ct);

    private static JsonObject Required(JsonObject root, MarketplaceId id) =>
        Section(root, create: false)[id.Value] as JsonObject ?? throw Missing(id);

    private static KeyNotFoundException Missing(MarketplaceId id) => new($"Marketplace '{id}' was not found.");

    private static JsonObject Section(JsonObject root, bool create)
    {
        var node = root;
        foreach (var segment in new[] { "world", "extensions", ExtensionId, "marketplaces" })
        {
            // Extension IDs are dictionary keys, not bindable property names.
            var key = segment == ExtensionId ? segment : PropertyName(node, segment);
            if (node[key] is JsonObject child) { node = child; continue; }
            if (node.ContainsKey(key))
                throw InvalidConfiguration(segment, "must be an object");
            if (!create) return new JsonObject();
            var added = new JsonObject();
            node[segment] = added;
            node = added;
        }
        ValidateRegistrations(JsonSerializer.SerializeToElement(node));
        return node;
    }

    private static string PropertyName(JsonObject node, string name)
    {
        var matches = node.Select(x => x.Key).Where(x => string.Equals(x, name, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length > 1)
            throw InvalidConfiguration(name, "contains ambiguous case-insensitive properties");
        return matches.Length == 0 ? name : matches[0];
    }

    private static bool TryProperty(JsonElement node, string name, string path, out JsonElement value)
    {
        value = default;
        var found = false;
        foreach (var property in node.EnumerateObject())
        {
            if (!string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
            if (found) throw InvalidConfiguration(path + "." + name, "contains ambiguous case-insensitive properties");
            found = true;
            value = property.Value;
        }
        return found;
    }

    private static InvalidDataException InvalidConfiguration(string path, string reason) =>
        new($"Invalid marketplace configuration at 'world.extensions.{ExtensionId}{(path.Length == 0 ? string.Empty : "." + path)}': {reason}.");

    private static void ValidateRegistrations(JsonElement marketplaces)
    {
        if (marketplaces.ValueKind != JsonValueKind.Object)
            throw InvalidConfiguration("marketplaces", "must be an object");
        foreach (var entry in marketplaces.EnumerateObject())
        {
            var path = $"marketplaces.{entry.Name}";
            if (!MarketplaceId.TryFrom(entry.Name, out _))
                throw InvalidConfiguration("marketplaces", "contains a noncanonical registration identity");
            if (entry.Value.ValueKind != JsonValueKind.Object)
                throw InvalidConfiguration(path, "must be an object");
            if (!TryProperty(entry.Value, "source", path, out var source) || source.ValueKind != JsonValueKind.String)
                throw InvalidConfiguration(path + ".source", "must be a credential-free repository URL");
            try { ValidateSource(source.GetString() ?? string.Empty); }
            catch (ArgumentException) { throw InvalidConfiguration(path + ".source", "must be a credential-free repository URL"); }
            if (TryProperty(entry.Value, "reference", path, out var reference) && reference.ValueKind != JsonValueKind.Null)
            {
                if (reference.ValueKind != JsonValueKind.String)
                    throw InvalidConfiguration(path + ".reference", "must be a Git reference or null");
                try { ValidateReference(reference.GetString()); }
                catch (ArgumentException) { throw InvalidConfiguration(path + ".reference", "must be a valid Git reference"); }
            }
            if (TryProperty(entry.Value, "enabled", path, out var enabled)
                && enabled.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw InvalidConfiguration(path + ".enabled", "must be a boolean");
        }
    }

    private static void ValidateSource(string source)
    {
        // Registration has no credential store. Authentication belongs to Git's external credential
        // helpers/SSH configuration, never to a persisted URL (including SSH user information).
        if (string.IsNullOrWhiteSpace(source) || source.Any(c => char.IsWhiteSpace(c) || char.IsControl(c))
            || source.Contains('\\') || !Uri.TryCreate(source, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("https" or "http" or "ssh") || string.IsNullOrEmpty(uri.Host)
            || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)
            || uri.AbsolutePath.Trim('/').Length == 0
            || Uri.UnescapeDataString(source).Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
            throw new ArgumentException("Source must be an absolute http, https, or ssh repository URL with a path and no credentials, query, fragment, whitespace, or control characters.", nameof(source));
    }

    private static void ValidateReference(string? reference)
    {
        if (reference is null) return;
        if (string.IsNullOrWhiteSpace(reference) || reference.StartsWith('-') || reference.StartsWith('/')
            || reference.EndsWith('/') || reference.EndsWith('.') || reference.EndsWith(".lock", StringComparison.Ordinal)
            || reference.Contains("..", StringComparison.Ordinal) || reference.Contains("@{", StringComparison.Ordinal)
            || reference.Contains("//", StringComparison.Ordinal) || reference == "@"
            || reference.Split('/').Any(component => component.StartsWith('.') || component.EndsWith(".lock", StringComparison.Ordinal))
            || reference.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || "~^:?*[\\".Contains(c)))
            throw new ArgumentException("Reference must be a Git branch, tag, or commit without option prefixes, whitespace, control characters, or Git ref metacharacters.", nameof(reference));
    }

    private sealed record Registration(string Source, string? Reference, bool Enabled = true);
}
