using System.IO.Abstractions;
using System.Text.Json;
using System.Text.Json.Nodes;

using BotNexus.Gateway.Configuration.Store;

namespace BotNexus.Gateway.Configuration.Writers;

/// <summary>
/// Writes the configuration document to <c>config.json</c> (#3527).
/// </summary>
/// <remarks>
/// <para>
/// This is <c>PlatformConfigWriter.WriteRootAsync</c> moved, not rewritten. Every property it
/// carried is load-bearing and each was added in response to a specific defect, so the code is
/// relocated verbatim rather than reimplemented against the new interface:
/// </para>
/// <list type="number">
///   <item><b>No-op detection (#2114)</b> - a canonically identical document does not touch the file
///     at all. An atomic replace rewrites the inode and re-triggers the reload pipeline, so writing
///     an unchanged document causes a reload storm.</item>
///   <item><b>Backup before replace</b> - the previous document is retained.</item>
///   <item><b>Atomic temp-then-replace with retry (#2357)</b> - Windows fails an overwrite while any
///     other handle is open on the destination, and the gateway is routinely its own competing
///     reader via <c>reloadOnChange</c>. A measured probe lost 29 of 40 saves without this.</item>
///   <item><b>Owner-only permissions applied TWICE (#2392)</b> - before the move so the file is never
///     briefly world-readable at its final path, and after because replacing an existing destination
///     has different per-platform semantics than creating one.</item>
///   <item><b>Directory creation</b> - the config directory may not exist on a fresh install.</item>
/// </list>
/// <para>
/// None of these apply to a store backend, which is why the fan-out is over an interface rather than
/// a shared implementation with conditionals.
/// </para>
/// </remarks>
public sealed class JsonConfigurationWriter : IConfigurationWriter
{
    /// <summary>Bounded retry count for the replace, matching the original writer.</summary>
    private const int ReplaceAttempts = 12;

    private static readonly JsonSerializerOptions PersistOptions = new() { WriteIndented = true };

    private readonly string _configPath;
    private readonly IFileSystem _fileSystem;
    private readonly ConfigBackupService? _backup;

    /// <summary>Creates a writer for <paramref name="configPath"/>.</summary>
    public JsonConfigurationWriter(string configPath, IFileSystem fileSystem, ConfigBackupService? backup = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configPath);
        ArgumentNullException.ThrowIfNull(fileSystem);

        _configPath = configPath;
        _fileSystem = fileSystem;
        _backup = backup;
    }

    /// <inheritdoc />
    public string Name => "json";

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// <b>The change set is applied to the document on disk, not to a re-serialised DTO.</b> That is what
    /// preserves the 33-of-34 configuration classes carrying no <c>[JsonExtensionData]</c>: a key the
    /// CLR type does not model is never visited, so it survives untouched instead of vanishing through a
    /// typed round-trip. Writing the DTO directly would be the whole-document write again with extra
    /// steps.
    /// </para>
    /// <para>
    /// An empty change set returns without touching the file at all. The no-op matters beyond
    /// efficiency: rewriting identical bytes still churns the backup history and the file mtime, so a
    /// save that changed nothing would be indistinguishable from one that did.
    /// </para>
    /// </remarks>
    public async Task<ConfigChangeSet> ApplyAsync(
        object dto,
        string pathPrefix,
        string reason,
        ConfigDiffOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dto);
        ArgumentNullException.ThrowIfNull(pathPrefix);

        var current = await ReadDocumentAsync(cancellationToken).ConfigureAwait(false);
        var changes = ConfigDtoDiffer.Diff(current, dto, pathPrefix, options);

        if (changes.IsEmpty)
        {
            return changes;
        }

        var next = current ?? [];
        ConfigDocumentPatcher.Apply(next, changes);
        await WriteAsync(next, reason, cancellationToken).ConfigureAwait(false);
        return changes;
    }

    /// <summary>
    /// Reads the configuration document currently on disk, or <see langword="null"/> when absent.
    /// </summary>
    /// <remarks>
    /// A missing file is not an error here: the first write on a fresh install legitimately has nothing
    /// to diff against, and a null document makes every key an insert.
    /// </remarks>
    private async Task<JsonObject?> ReadDocumentAsync(CancellationToken cancellationToken)
    {
        if (!_fileSystem.File.Exists(_configPath))
        {
            return null;
        }

        var text = await _fileSystem.File.ReadAllTextAsync(_configPath, cancellationToken).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text)?.AsObject();
    }

    /// <inheritdoc />
    public async Task WriteAsync(JsonObject document, string reason, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);

        var json = document.ToJsonString(PersistOptions);

        // (1) No-op detection (#2114).
        if (_fileSystem.File.Exists(_configPath))
        {
            var existing = await _fileSystem.File.ReadAllTextAsync(_configPath, cancellationToken);
            if (JsonCanonicalEquals(existing, json))
                return;
        }

        // (2) Backup.
        _backup?.Backup(_configPath, reason);

        // (5) Directory creation.
        var directory = _fileSystem.Path.GetDirectoryName(_configPath);
        if (!string.IsNullOrWhiteSpace(directory))
            _fileSystem.Directory.CreateDirectory(directory);

        // (3) Atomic temp-then-replace, with (4) permissions applied either side of the move.
        var tempPath = _configPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await _fileSystem.File.WriteAllTextAsync(tempPath, json, cancellationToken);

            SecureFilePermissions.RestrictToOwner(_fileSystem, tempPath);
            await ReplaceWithRetryAsync(tempPath, cancellationToken);
            SecureFilePermissions.RestrictToOwner(_fileSystem, _configPath);
        }
        finally
        {
            if (_fileSystem.File.Exists(tempPath))
                _fileSystem.File.Delete(tempPath);
        }
    }

    /// <summary>
    /// Replaces the destination, retrying while a competing reader holds a handle (#2357).
    /// </summary>
    private async Task ReplaceWithRetryAsync(string tempPath, CancellationToken ct)
    {
        var delayMs = 5;
        for (var attempt = 1; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (_fileSystem.File.Exists(_configPath))
                {
                    _fileSystem.File.Replace(
                        tempPath, _configPath, destinationBackupFileName: null, ignoreMetadataErrors: true);
                }
                else
                {
                    _fileSystem.File.Move(tempPath, _configPath, overwrite: true);
                }

                return;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                if (attempt >= ReplaceAttempts)
                    throw;
            }

            await Task.Delay(delayMs, ct);
            delayMs = Math.Min(delayMs * 2, 100);
        }
    }

    /// <summary>
    /// Structural equality, tolerating formatting differences so a re-serialised identical document
    /// is still a no-op.
    /// </summary>
    /// <remarks>
    /// Uses <see cref="JsonNode.DeepEquals"/>, exactly as the original writer did. An earlier version
    /// of this file hand-rolled the comparison and diverged: it treated documents as equal that
    /// <c>DeepEquals</c> considers different, so a genuine rewrite was skipped as a no-op and the
    /// owner-only permission re-application never ran - reintroducing #2392 for every save after the
    /// first. Reimplementing a comparison the framework already provides was the whole mistake.
    /// </remarks>
    private static bool JsonCanonicalEquals(string existing, string candidate)
    {
        if (string.Equals(existing, candidate, StringComparison.Ordinal))
            return true;

        try
        {
            var existingNode = JsonNode.Parse(existing);
            var candidateNode = JsonNode.Parse(candidate);
            return JsonNode.DeepEquals(existingNode, candidateNode);
        }
        catch (JsonException)
        {
            // A malformed existing file is not equal to anything: fall through and rewrite it.
            return false;
        }
    }
}
