using System.IO.Abstractions;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using BotNexus.Gateway.Configuration.Writers;

namespace BotNexus.Gateway.Configuration;

/// <summary>
/// Owns configuration-only registration of extension source repositories. It deliberately does
/// not clone, build, deploy, or execute repository content.
/// </summary>
public sealed partial class ExtensionRepositoryRegistryService
{
    private const string SectionName = "extensionRepositories";
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly PlatformConfigWriter _writer;

    /// <summary>Creates the registry over the configuration backends selected for a config path.</summary>
    public ExtensionRepositoryRegistryService(string configPath, IFileSystem fileSystem)
    {
        _writer = ConfigWriterFactory.Create(configPath, fileSystem);
    }

    /// <summary>Adds a repository registration while rejecting a concurrently-added duplicate.</summary>
    public async Task AddAsync(
        string id,
        string repositoryUrl,
        string requestedRef,
        bool enabled = true,
        bool updatesEnabled = true,
        CancellationToken ct = default)
    {
        ValidateId(id);
        ValidateRepositoryUrl(repositoryUrl);
        ValidateRequestedRef(requestedRef);

        await _writer.MutateAsync(root =>
        {
            var repositories = GetOrCreateSection(root);
            if (repositories.ContainsKey(id))
                throw new InvalidOperationException($"Extension repository '{id}' already exists.");

            repositories[id] = JsonSerializer.SerializeToNode(
                new ExtensionRepositoryRegistration
                {
                    RepositoryUrl = repositoryUrl,
                    RequestedRef = requestedRef,
                    Enabled = enabled,
                    UpdatesEnabled = updatesEnabled
                },
                SerializerOptions);
        }, "extension-repository-add", ct, [SectionName]);
    }

    /// <summary>Lists registrations in stable ordinal ID order for deterministic callers.</summary>
    public async Task<IReadOnlyList<ExtensionRepositoryRegistrationInfo>> ListAsync(
        CancellationToken ct = default)
    {
        var root = await _writer.ReadAsync(ct);
        if (root[SectionName] is not JsonObject repositories)
            return [];

        return repositories
            .OrderBy(item => item.Key, StringComparer.Ordinal)
            .Select(item =>
            {
                var registration = item.Value?.Deserialize<ExtensionRepositoryRegistration>(SerializerOptions)
                    ?? throw new InvalidOperationException(
                        $"Extension repository '{item.Key}' has an invalid registration.");
                return new ExtensionRepositoryRegistrationInfo(
                    item.Key,
                    registration.RepositoryUrl,
                    registration.RequestedRef,
                    registration.Enabled,
                    registration.UpdatesEnabled,
                    registration.ReconciliationStatus,
                    registration.ResolvedCommit,
                    registration.ClonePath,
                    registration.LastAttemptUtc,
                    registration.LastSuccessUtc,
                    registration.LatestFailure);
            })
            .ToArray();
    }

    /// <summary>Updates only supplied fields while rejecting a concurrently-removed registration.</summary>
    public async Task UpdateAsync(
        string id,
        string? repositoryUrl,
        string? requestedRef,
        bool? updatesEnabled,
        CancellationToken ct = default)
    {
        ValidateId(id);
        if (repositoryUrl is not null)
            ValidateRepositoryUrl(repositoryUrl);
        if (requestedRef is not null)
            ValidateRequestedRef(requestedRef);

        await _writer.MutateAsync(root =>
        {
            var registration = GetRequiredRegistration(root, id);
            if (repositoryUrl is not null)
                registration["repositoryUrl"] = repositoryUrl;
            if (requestedRef is not null)
                registration["requestedRef"] = requestedRef;
            if (updatesEnabled is not null)
                registration["updatesEnabled"] = updatesEnabled.Value;
        }, "extension-repository-update", ct, [SectionName]);
    }

    /// <summary>Enables or disables a registration without changing its update preference.</summary>
    public async Task SetEnabledAsync(string id, bool enabled, CancellationToken ct = default)
    {
        ValidateId(id);
        await _writer.MutateAsync(root =>
        {
            var registration = GetRequiredRegistration(root, id);
            registration["enabled"] = enabled;
        }, "extension-repository-set-enabled", ct, [SectionName]);
    }

    /// <summary>Removes registration metadata only; repository contents are never touched.</summary>
    public async Task RemoveAsync(string id, CancellationToken ct = default)
    {
        ValidateId(id);
        await _writer.MutateAsync(root =>
        {
            var repositories = GetRequiredSection(root, id);
            if (!repositories.Remove(id))
                throw Missing(id);
        }, "extension-repository-remove", ct, [SectionName]);
    }

    /// <summary>Records the start of an attempt before any clone is mutated.</summary>
    public Task RecordReconciliationAttemptAsync(string id, string clonePath, DateTimeOffset attemptedUtc, CancellationToken ct = default)
    {
        ValidateId(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(clonePath);
        return _writer.MutateAsync(root =>
        {
            var registration = GetRequiredRegistration(root, id);
            registration["reconciliationStatus"] = "reconciling";
            registration["clonePath"] = clonePath;
            registration["lastAttemptUtc"] = attemptedUtc;
            registration["latestFailure"] = null;
        }, "extension-repository-reconciliation-attempt", ct, [SectionName]);
    }

    /// <summary>Persists the immutable commit identity selected by a successful attempt.</summary>
    public Task RecordReconciliationSuccessAsync(string id, string resolvedCommit, DateTimeOffset succeededUtc, CancellationToken ct = default)
    {
        ValidateId(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(resolvedCommit);
        return _writer.MutateAsync(root =>
        {
            var registration = GetRequiredRegistration(root, id);
            registration["reconciliationStatus"] = "succeeded";
            registration["resolvedCommit"] = resolvedCommit;
            registration["lastSuccessUtc"] = succeededUtc;
            registration["latestFailure"] = null;
        }, "extension-repository-reconciliation-success", ct, [SectionName]);
    }

    /// <summary>Persists a stable failure name plus diagnostic without discarding prior success state.</summary>
    public Task RecordReconciliationFailureAsync(string id, string failureName, string diagnostic, CancellationToken ct = default)
    {
        ValidateId(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(failureName);
        ArgumentException.ThrowIfNullOrWhiteSpace(diagnostic);
        return _writer.MutateAsync(root =>
        {
            var registration = GetRequiredRegistration(root, id);
            registration["reconciliationStatus"] = "failed";
            registration["latestFailure"] = $"{failureName}: {diagnostic}";
        }, "extension-repository-reconciliation-failure", ct, [SectionName]);
    }

    private static JsonObject GetOrCreateSection(JsonObject root)
    {
        if (root[SectionName] is JsonObject repositories)
            return repositories;

        repositories = new JsonObject();
        root[SectionName] = repositories;
        return repositories;
    }

    private static JsonObject GetRequiredSection(JsonObject root, string id)
        => root[SectionName] as JsonObject ?? throw Missing(id);

    private static JsonObject GetRequiredRegistration(JsonObject root, string id)
    {
        var repositories = GetRequiredSection(root, id);
        return repositories[id] as JsonObject ?? throw Missing(id);
    }

    private static KeyNotFoundException Missing(string id)
        => new($"Extension repository '{id}' was not found.");

    private static void ValidateId(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || !IdPattern().IsMatch(id))
        {
            throw new ArgumentException(
                "Extension repository id must use lowercase alphanumeric segments separated by single hyphens.",
                nameof(id));
        }
    }

    private static void ValidateRepositoryUrl(string repositoryUrl)
    {
        if (!Uri.TryCreate(repositoryUrl, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https" or "ssh"))
        {
            throw new ArgumentException(
                "Repository URL must be an absolute http, https, or ssh URL.",
                nameof(repositoryUrl));
        }
    }

    private static void ValidateRequestedRef(string requestedRef)
    {
        if (string.IsNullOrWhiteSpace(requestedRef)
            || requestedRef[0] == '-'
            || requestedRef.Any(char.IsWhiteSpace)
            || requestedRef.Any(char.IsControl))
        {
            throw new ArgumentException(
                "Requested ref must be nonblank, contain no whitespace or control characters, and must not start with '-'.",
                nameof(requestedRef));
        }
    }

    [GeneratedRegex("^[a-z0-9]+(?:-[a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex IdPattern();
}
