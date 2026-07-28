using System.IO.Abstractions;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace BotNexus.Gateway.Configuration;

/// <summary>
/// Thread-safe writer for platform config JSON files.
/// Performs atomic read-modify-write with file locking.
/// </summary>
public sealed class PlatformConfigWriter
{
    private static readonly SemaphoreSlim WriteLock = new(1, 1);
    private static readonly JsonSerializerOptions PlatformReadOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };
    private static readonly JsonSerializerOptions PlatformWriteOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
    private static readonly JsonSerializerOptions PlatformPersistOptions = new() { WriteIndented = true };
    private readonly string _configPath;
    private readonly IFileSystem _fileSystem;
    private readonly ConfigBackupService? _backup;

    public PlatformConfigWriter(string configPath, IFileSystem fileSystem, ConfigBackupService? backup = null)
    {
        _configPath = configPath;
        _fileSystem = fileSystem;
        _backup = backup;
    }

    /// <summary>
    /// Reads the full config as a JSON object.
    /// </summary>
    public async Task<JsonObject> ReadAsync(CancellationToken ct = default)
    {
        return await ReadRootAsync(ct);
    }

    /// <summary>
    /// Reads the current platform configuration as a strongly-typed object.
    /// </summary>
    public async Task<PlatformConfig> ReadPlatformConfigAsync(CancellationToken ct = default)
    {
        var root = await ReadRootAsync(ct);
        var json = root.ToJsonString();
        return JsonSerializer.Deserialize<PlatformConfig>(json, PlatformReadOptions) ?? new PlatformConfig();
    }

    /// <summary>
    /// Atomically updates a section of the config.
    ///
    /// The incoming payload comes from the config UI, which was served redacted
    /// secrets ("***") and channel subtrees it may not fully model. A raw
    /// <c>root[sectionName] = value</c> replace would (a) clobber real on-disk
    /// secrets with the "***" placeholder the UI round-tripped (#1955) and
    /// (b) drop existing keys the payload omits, e.g. telegram bots or
    /// serviceBus queues (#1954). Instead we restore any placeholder secrets
    /// from the existing section and deep-merge the incoming payload over the
    /// existing section so omitted keys survive.
    /// </summary>
    /// <param name="merge">
    /// When <see langword="true"/> (default, used by the config-UI PUT path), the
    /// incoming payload is treated as potentially partial/redacted: secrets are
    /// restored and the payload is deep-merged over the existing section so omitted
    /// keys survive. When <see langword="false"/>, callers that already assemble the
    /// full authoritative section from disk (e.g. LocationsController, which must be
    /// able to delete entries by omission) get a straight replace.
    /// </param>
    public async Task UpdateSectionAsync(string sectionName, JsonNode value, CancellationToken ct = default, bool merge = true)
        => await MutateAsync(
            root =>
            {
                if (!merge || value is not JsonObject incoming || root[sectionName] is not JsonObject existing)
                {
                    // No existing object section (or non-object payload): nothing to
                    // merge/preserve, so fall back to a straight assignment.
                    root[sectionName] = value;
                    return;
                }

                // Work on a clone so we never mutate the shared root mid-flight.
                var merged = existing.DeepClone().AsObject();

                // 1) Restore secrets: wrap both under the real section name so the
                //    symmetric restore walks the same paths RedactSecrets uses.
                var existingWrapper = new JsonObject { [sectionName] = existing.DeepClone() };
                var incomingWrapper = new JsonObject { [sectionName] = incoming.DeepClone() };
                ConfigSecretMerge.RestoreSecrets(existingWrapper, incomingWrapper);
                var restoredIncoming = incomingWrapper[sectionName] as JsonObject ?? incoming;

                // 2) Deep-merge restored payload over existing so omitted subtrees survive.
                ConfigSecretMerge.DeepMerge(merged, restoredIncoming);

                root[sectionName] = merged;
            },
            $"before-{sectionName}-update",
            ct);

    /// <summary>
    /// Replaces the entire platform configuration document.
    /// </summary>
    public async Task UpdatePlatformConfigAsync(PlatformConfig config, string reason, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        await MutateAsync(root =>
        {
            var serialized = JsonSerializer.Serialize(config, PlatformWriteOptions);
            var next = JsonNode.Parse(serialized)?.AsObject() ?? new JsonObject();
            root.Clear();
            foreach (var kvp in next)
                root[kvp.Key] = kvp.Value?.DeepClone();
        }, reason, ct);
    }

    /// <summary>
    /// Updates a keyed entry within a section (e.g., providers.github-copilot).
    /// </summary>
    public async Task UpdateSectionEntryAsync(string sectionName, string key, JsonNode value, CancellationToken ct = default)
        => await MutateAsync(root =>
        {
            if (root[sectionName] is not JsonObject section)
            {
                section = new JsonObject();
                root[sectionName] = section;
            }

            // Same secret-restore + deep-merge as UpdateSectionAsync, but scoped to a single keyed
            // entry. The UI PUTs a redacted entry (e.g. providers.github-copilot) back verbatim, so
            // a raw replace would clobber the real secret with "***" (#1955) and drop any on-disk
            // keys the payload omits (#1954). Wrap the entry under its real section name so the
            // secret restore walks the same paths ConfigSecretMerge.Redact uses.
            if (value is JsonObject incoming && section[key] is JsonObject existing)
            {
                var existingWrapper = new JsonObject { [sectionName] = new JsonObject { [key] = existing.DeepClone() } };
                var incomingWrapper = new JsonObject { [sectionName] = new JsonObject { [key] = incoming.DeepClone() } };
                ConfigSecretMerge.RestoreSecrets(existingWrapper, incomingWrapper);
                var restoredIncoming = incomingWrapper[sectionName]![key] as JsonObject ?? incoming;

                var merged = existing.DeepClone().AsObject();
                ConfigSecretMerge.DeepMerge(merged, restoredIncoming);
                section[key] = merged;
            }
            else
            {
                section[key] = value;
            }
        }, $"before-{sectionName}-update", ct);

    /// <summary>
    /// Atomically mutates the config document and persists the result.
    /// </summary>
    public async Task MutateAsync(Action<JsonObject> mutation, string reason, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        await MutateAsync(root =>
        {
            mutation(root);
            return Task.CompletedTask;
        }, reason, ct);
    }

    /// <summary>
    /// Atomically mutates the config document and persists the result.
    /// </summary>
    public async Task MutateAsync(Func<JsonObject, Task> mutation, string reason, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        await WriteLock.WaitAsync(ct);
        try
        {
            var root = await ReadRootAsync(ct);
            await mutation(root);
            await WriteRootAsync(root, reason, ct);
        }
        finally
        {
            WriteLock.Release();
        }
    }

    /// <summary>
    /// Applies a targeted raw-JSON mutation and persists it only when the resulting
    /// <em>complete</em> candidate document validates.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the safe replacement for the typed whole-root rewrite
    /// (<see cref="UpdatePlatformConfigAsync"/>) that targeted CLI operations used to perform
    /// (#2057). Two properties matter:
    /// </para>
    /// <list type="number">
    ///   <item>The document is read, mutated, validated, and written inside the same writer lock,
    ///   so a concurrent writer cannot interleave between the read and the replace.</item>
    ///   <item>Validation runs against the candidate <em>before</em> the live file is touched. When
    ///   the candidate is rejected nothing is written, no backup is taken, and the original bytes
    ///   on disk are left byte-for-byte unchanged.</item>
    /// </list>
    /// <para>
    /// Because only the addressed node is rewritten, unknown root/child keys, extension-owned
    /// JSON, secrets, and the reserved <c>agents.defaults</c> entry all survive untouched.
    /// </para>
    /// </remarks>
    /// <param name="mutation">
    /// Mutates the raw root in place and returns <see langword="null"/> on success, or a
    /// caller-presentable message to abort the write (for example an unresolvable key path).
    /// </param>
    /// <param name="reason">Backup reason label recorded when the write proceeds.</param>
    /// <returns>The rejection messages; empty when the mutation was validated and persisted.</returns>
    public async Task<IReadOnlyList<string>> MutateValidatedAsync(
        Func<JsonObject, string?> mutation,
        string reason,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(mutation);

        await WriteLock.WaitAsync(ct);
        try
        {
            var root = await ReadRootAsync(ct);

            var mutationError = mutation(root);
            if (!string.IsNullOrWhiteSpace(mutationError))
                return [mutationError];

            // Validate the complete candidate document, not just the mutated fragment: a locally
            // plausible edit can still violate a cross-field rule elsewhere in the graph.
            var candidateJson = root.ToJsonString(PlatformPersistOptions);
            var errors = PlatformConfigLoader.ValidateRawJson(candidateJson);
            if (errors.Count > 0)
                return errors;

            await WriteRootAsync(root, reason, ct);
            return [];
        }
        finally
        {
            WriteLock.Release();
        }
    }

    /// <summary>
    /// Removes a keyed entry from a section.
    /// </summary>
    public async Task RemoveSectionEntryAsync(string sectionName, string key, CancellationToken ct = default)
        => await MutateAsync(root =>
        {
            if (root[sectionName] is JsonObject section)
                section.Remove(key);
        }, $"before-{sectionName}-remove", ct);

    private async Task<JsonObject> ReadRootAsync(CancellationToken ct)
    {
        if (!_fileSystem.File.Exists(_configPath))
            return new JsonObject();

        var json = await _fileSystem.File.ReadAllTextAsync(_configPath, ct);
        return JsonNode.Parse(json)?.AsObject() ?? new JsonObject();
    }

    private async Task WriteRootAsync(JsonObject root, string reason, CancellationToken ct)
    {
        var json = root.ToJsonString(PlatformPersistOptions);

        // Issue #2114: no-op detection. If the resulting canonical JSON is byte-for-byte
        // identical to what is already on disk, do not back up, replace, or otherwise touch
        // the file. This prevents startup and redundant-mutation reload storms (an atomic
        // File.Move rewrites the inode/timestamp and re-triggers the IConfiguration reload
        // pipeline even when nothing effectively changed).
        if (_fileSystem.File.Exists(_configPath))
        {
            var existing = await _fileSystem.File.ReadAllTextAsync(_configPath, ct);
            if (JsonCanonicalEquals(existing, json))
                return;
        }

        _backup?.Backup(_configPath, reason);

        var directory = _fileSystem.Path.GetDirectoryName(_configPath);
        if (!string.IsNullOrWhiteSpace(directory))
            _fileSystem.Directory.CreateDirectory(directory);

        var tempPath = _configPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await _fileSystem.File.WriteAllTextAsync(tempPath, json, ct);

            // #2392: config.json carries provider API keys and channel bot tokens, so it must not
            // inherit a default umask/parent-ACL that leaves it group- or world-readable.
            //
            // Restrict TWICE, deliberately:
            //  - before the move, so the file is never owner-readable-only *after* it is already
            //    visible at its final path (no window where a broad-permission config.json exists);
            //  - after the move, because the semantics of replacing an existing destination differ
            //    per platform, and this is a REWRITE path, not a first-create path - a fix applied
            //    only when the file is first created would leave every subsequent save wrong.
            SecureFilePermissions.RestrictToOwner(_fileSystem, tempPath);
            await ReplaceWithRetryAsync(tempPath, ct);
            SecureFilePermissions.RestrictToOwner(_fileSystem, _configPath);
        }
        finally
        {
            if (_fileSystem.File.Exists(tempPath))
                _fileSystem.File.Delete(tempPath);
        }
    }

    // #2357: Windows fails File.Move(..., overwrite: true) with UnauthorizedAccessException when
    // ANY other handle is open on the destination - even one opened with the maximal
    // FileShare.ReadWrite | FileShare.Delete that the configuration provider's reload watcher
    // uses. The gateway registers config.json with AddJsonFile(reloadOnChange: true), so it is
    // routinely its own competing reader; a measured probe lost 29 of 40 saves.
    //
    // Two mitigations, in order:
    //  1) Prefer File.Replace, which maps to Win32 ReplaceFile semantics and tolerates readers
    //     that opened the destination with delete sharing. Replace requires the destination to
    //     exist, so a first-create still uses Move.
    //  2) Wrap both in a bounded retry with backoff, because a reader that opened WITHOUT delete
    //     sharing still blocks the swap momentarily and that window is short.
    //
    // Atomicity is unchanged: the staged temp file is still swapped in as a single operation, and
    // the final failure is rethrown rather than swallowed so a lost edit can never be mistaken
    // for a successful save.
    private const int ReplaceAttempts = 10;

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
    /// Compares two JSON documents for structural (effective) equality, tolerating whitespace
    /// and formatting differences so that a re-serialized identical document is treated as a
    /// no-op even when the on-disk copy used a different indentation or key formatting.
    /// </summary>
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
            // Existing file is not valid JSON: treat as different so we rewrite it.
            return false;
        }
    }
}
