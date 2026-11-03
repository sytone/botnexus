using System.IO.Abstractions;
using System.Text.Json.Nodes;
using BotNexus.Gateway.Configuration.Store;

namespace BotNexus.Gateway.Configuration.Writers;

/// <summary>A redaction-safe warning about an output projection after authority committed.</summary>
/// <param name="Code">Stable diagnostic code, never configuration contents or exception text.</param>
/// <param name="Message">Operator-facing description of the committed outcome.</param>
public sealed record ConfigurationProjectionWarning(string Code, string Message);

/// <summary>Commits SQLite before refreshing an optional, existing output-only JSON projection.</summary>
public sealed class AuthorityConfigurationWriter : IConfigurationWriter
{
    private readonly IConfigStore _store;
    private readonly SqliteConfigurationWriter _authority;
    private readonly JsonConfigurationWriter _projection;
    private readonly IFileSystem _fileSystem;
    private readonly string _configPath;

    /// <summary>Creates the sole authority writer and optional projection sink.</summary>
    public AuthorityConfigurationWriter(IConfigStore store, string storePath, string configPath,
        IFileSystem fileSystem, ConfigBackupService? backup)
    {
        _store = store;
        _authority = new SqliteConfigurationWriter(store, storePath);
        _projection = new JsonConfigurationWriter(configPath, fileSystem, backup);
        _fileSystem = fileSystem;
        _configPath = configPath;
    }

    /// <inheritdoc />
    public string Name => "sqlite";

    /// <inheritdoc />
    public IReadOnlyList<ConfigurationBackendDescriptor> Backends => _authority.Backends;

    /// <inheritdoc />
    public async Task WriteAsync(JsonObject document, string reason, CancellationToken cancellationToken = default)
        => _ = await WriteWithResultAsync(document, reason, cancellationToken).ConfigureAwait(false);

    /// <summary>Commits authority first, then returns this save's immutable output diagnostics.</summary>
    public async Task<ConfigurationWriteResult> WriteWithResultAsync(JsonObject document, string reason,
        CancellationToken cancellationToken = default)
    {
        await _authority.WriteAsync(document, reason, cancellationToken).ConfigureAwait(false);
        return await RefreshProjectionAsync(reason).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task ApplyChangeSetAsync(ConfigChangeSet changes, string reason, CancellationToken cancellationToken = default)
        => _ = await ApplyChangeSetWithResultAsync(changes, reason, cancellationToken).ConfigureAwait(false);

    /// <summary>Returns diagnostics for this change set only; empty sets perform no persistence.</summary>
    public async Task<ConfigurationWriteResult> ApplyChangeSetWithResultAsync(ConfigChangeSet changes, string reason,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(changes);
        cancellationToken.ThrowIfCancellationRequested();
        if (changes.IsEmpty)
            return ConfigurationWriteResult.Empty;
        await _authority.ApplyChangeSetAsync(changes, reason, cancellationToken).ConfigureAwait(false);
        return await RefreshProjectionAsync(reason).ConfigureAwait(false);
    }

    private async Task<ConfigurationWriteResult> RefreshProjectionAsync(string reason)
    {
        try
        {
            // Commit has happened. Caller cancellation must not misreport a committed edit as failed.
            // JSON is output only: never parse it as source or create an intentionally absent file.
            if (!_fileSystem.File.Exists(_configPath))
                return ConfigurationWriteResult.Empty;
            var snapshot = ConfigDocumentRehydrator.Rehydrate(await _store.ReadEntriesAsync(CancellationToken.None).ConfigureAwait(false));
            return await _projection.RefreshExistingWithResultAsync(snapshot, reason, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Authority failures never enter this boundary. Do not expose paths or exception text.
            return new([JsonConfigurationWriter.RefreshWarning]);
        }
    }
}
