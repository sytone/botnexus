using BotNexus.Extensions.Plugins.Cron;
using BotNexus.Extensions.Plugins.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotNexus.Extensions.Plugins.Lifecycle;

/// <summary>
/// Owns the install / update / remove lifecycle for plugins fetched from a marketplace source.
/// </summary>
/// <remarks>
/// Two invariants drive the shape of this class.
/// <para>
/// <b>Install is all-or-nothing.</b> Content is fetched and validated in a staging directory
/// OUTSIDE the plugin root, and only promoted into place once it is known good. A fetch that
/// faults half way through therefore cannot leave a partial plugin directory behind - there is
/// nothing to clean up at the destination because nothing was ever written there. A partially
/// materialised plugin is worse than a failed one: it looks installed to every later consumer.
/// </para>
/// <para>
/// <b>Removal is exact-set, never pattern-matched.</b> Install records every file it wrote and
/// removal deletes exactly that set. Deleting the plugin directory wholesale would be simpler
/// and wrong: a user who dropped a local override or a log file next to plugin content would
/// silently lose it. Directories are pruned only when they are empty after the recorded files go.
/// </para>
/// </remarks>
public sealed class PluginLifecycleManager : IPluginUpdateService
{
    private readonly PluginStateStore _store;
    private readonly IPluginSourceFetcher _fetcher;
    private readonly PluginImportAdapter _importer;
    private readonly TimeProvider _timeProvider;
    private readonly IPluginInstallObserver? _installObserver;
    private readonly IPluginLifecycleParticipant? _participant;
    private readonly ILogger<PluginLifecycleManager> _logger;
    // Internal isolation seam, not operator configuration or a security switch.
    internal string StagingRoot { get; set; } = Path.Combine(Path.GetTempPath(), "botnexus-plugin-staging");
    private readonly SemaphoreSlim _mutationGate = new(1, 1);

    /// <summary>Creates a manager over a plugin root.</summary>
    /// <param name="store">Installed-plugin record store, which also defines the plugin root.</param>
    /// <param name="fetcher">Transport used to materialise a marketplace source.</param>
    /// <param name="parser">Manifest parser used to validate fetched content.</param>
    /// <param name="timeProvider">Clock, injectable so install timestamps are deterministic in tests.</param>
    /// <param name="logger">Logger, optional.</param>
    /// <param name="installObserver">
    /// Notified after a SUCCESSFUL install (#2683). Optional so this type stays constructible with
    /// no cron infrastructure at all - a consumer that only parses or removes plugins must not be
    /// forced to compose a scheduler.
    /// </param>
    /// <param name="participant">Awaited runtime activation and teardown, composed outside core Plugins.</param>
    public PluginLifecycleManager(
        PluginStateStore store,
        IPluginSourceFetcher fetcher,
        PluginManifestParser? parser = null,
        TimeProvider? timeProvider = null,
        ILogger<PluginLifecycleManager>? logger = null,
        IPluginInstallObserver? installObserver = null,
        IPluginLifecycleParticipant? participant = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _fetcher = fetcher ?? throw new ArgumentNullException(nameof(fetcher));
        _importer = new PluginImportAdapter(parser ?? new PluginManifestParser());
        _timeProvider = timeProvider ?? TimeProvider.System;
        _installObserver = installObserver;
        _participant = participant;
        _logger = logger ?? NullLogger<PluginLifecycleManager>.Instance;
    }

    /// <summary>Every plugin currently recorded as installed.</summary>
    public IReadOnlyList<InstalledPlugin> List() => _store.Read();

    /// <summary>Absolute directory a plugin's content lives in.</summary>
    /// <param name="name">Plugin identifier.</param>
    public string GetPluginDirectory(string name) => Path.Combine(_store.PluginRoot, name);

    /// <summary>
    /// Fetches <paramref name="request"/>'s source, validates its manifest, and promotes it into
    /// the plugin root. Nothing is written to the plugin's directory unless the whole fetch and
    /// validation succeeded.
    /// </summary>
    /// <param name="request">What to install.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<PluginOperationResult> InstallAsync(
        PluginInstallRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await InstallCoreAsync(request, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    private async Task<PluginOperationResult> InstallCoreAsync(
        PluginInstallRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Source))
        {
            return PluginOperationResult.Failure(request.Name ?? string.Empty, "source", "A plugin source is required.");
        }

        var staged = await StageAsync(request.Source, request.Reference, request.Name, cancellationToken)
            .ConfigureAwait(false);
        if (staged.Failure is not null)
        {
            return staged.Failure with { Name = request.Name ?? staged.Failure.Name };
        }

        var manifest = staged.Manifest!;
        try
        {
            var existing = _store.Find(manifest.Name);
            if (existing is not null)
            {
                return PluginOperationResult.Failure(
                    manifest.Name,
                    "name",
                    $"Plugin '{manifest.Name}' is already installed at version '{existing.ResolvedVersion}'. Remove it first, or update it.");
            }

            var destination = GetPluginDirectory(manifest.Name);
            if (Directory.Exists(destination))
            {
                return PluginOperationResult.Failure(
                    manifest.Name,
                    "directory",
                    $"Plugin directory '{destination}' already exists but is not recorded as installed. It was not overwritten.");
            }

            var files = Promote(staged.Directory!, destination);
            files = WriteTrustCatalog(destination, manifest.Name, files);

            var record = new InstalledPlugin
            {
                Name = manifest.Name,
                Source = request.Source,
                Reference = request.Reference,
                ResolvedVersion = staged.ResolvedVersion!,
                ManifestVersion = manifest.Version,
                ManifestFormat = staged.Format,
                UpdatesEnabled = request.UpdatesEnabled,
                InstalledAtUtc = _timeProvider.GetUtcNow(),
                Files = files,
            };
            _store.Upsert(record);
            if (_participant is not null)
            {
                var context = new PluginLifecycleContext(record, manifest, destination);
                try
                {
                    await _participant.ActivateAsync(context, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception activationError)
                {
                    // Keep a recovery receipt if cleanup fails: a potentially live process must
                    // never become an unowned orphan merely because install could not complete.
                    using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    try
                    {
                        await _participant.DeactivateAsync(context, cleanup.Token).ConfigureAwait(false);
                        DeleteRecordedFiles(record);
                        _store.Delete(record.Name);
                    }
                    catch (Exception compensationError)
                    {
                        _logger.LogError(activationError, "Plugin {Plugin} activation failed.", record.Name);
                        _logger.LogError(compensationError, "Plugin {Plugin} install compensation failed.", record.Name);
                        return PluginOperationResult.Failure(record.Name, "lifecycle",
                            "Activation failed. Install compensation failed; content and recovery receipt retained.");
                    }

                    _logger.LogWarning(activationError, "Plugin {Plugin} activation failed and was rolled back.", record.Name);
                    if (activationError is OperationCanceledException) throw;
                    return PluginOperationResult.Failure(record.Name, "lifecycle", "Activation failed and was rolled back.");
                }
            }

            _logger.LogInformation(
                "Installed plugin {Plugin} at {Version} ({FileCount} files).",
                record.Name,
                record.ResolvedVersion,
                files.Count);

            // #2683: the platform-wide plugin-update job is provisioned by the act of installing a
            // plugin. It fires only on SUCCESS - a failed install materialised nothing, so a job
            // scheduled for it would run forever over content that does not exist. Failure to
            // provision must not fail an install that has already completed: the plugin is on disk
            // and its record is written, and reporting that as a failed install would be a lie the
            // caller could act on destructively.
            if (_installObserver is not null)
            {
                try
                {
                    await _installObserver.OnPluginInstalledAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(
                        ex,
                        "Plugin {Plugin} installed, but provisioning the plugin-update cron job failed.",
                        record.Name);
                }
            }

            return new PluginOperationResult
            {
                Outcome = PluginOperationOutcome.Installed,
                Name = record.Name,
                Plugin = record,
            };
        }
        finally
        {
            TryDeleteDirectory(staged.Directory!);
        }
    }

    /// <summary>
    /// Re-resolves an installed plugin's source and replaces its content when the source has
    /// moved. A plugin whose update preference is disabled is left completely untouched - the
    /// source is not even fetched, because fetching a pinned plugin costs a clone to reach a
    /// foregone conclusion.
    /// </summary>
    /// <param name="name">Plugin identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<PluginOperationResult> UpdateAsync(string name, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await UpdateCoreAsync(name, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    private async Task<PluginOperationResult> UpdateCoreAsync(string name, CancellationToken cancellationToken)
    {
        var existing = _store.Find(name);
        if (existing is null)
        {
            return PluginOperationResult.Failure(name, "name", $"Plugin '{name}' is not installed.");
        }

        if (!existing.UpdatesEnabled)
        {
            _logger.LogInformation("Skipping update for pinned plugin {Plugin}.", name);
            return new PluginOperationResult
            {
                Outcome = PluginOperationOutcome.SkippedPinned,
                Name = name,
                Plugin = existing,
                PreviousVersion = existing.ResolvedVersion,
            };
        }

        var staged = await StageAsync(existing.Source, existing.Reference, existing.Name, cancellationToken)
            .ConfigureAwait(false);
        if (staged.Failure is not null)
        {
            return staged.Failure with { Name = name, PreviousVersion = existing.ResolvedVersion };
        }

        try
        {
            if (string.Equals(staged.ResolvedVersion, existing.ResolvedVersion, StringComparison.Ordinal))
            {
                return new PluginOperationResult
                {
                    Outcome = PluginOperationOutcome.AlreadyCurrent,
                    Name = name,
                    Plugin = existing,
                    PreviousVersion = existing.ResolvedVersion,
                };
            }

            var destination = GetPluginDirectory(name);
            if (HasCandidateCollision(existing, staged.Directory!, destination))
                return PluginOperationResult.Failure(name, "directory", "Update conflicts with local content; installed content and runtime were not changed.")
                    with { PreviousVersion = existing.ResolvedVersion };
            var previousContext = ReadContext(existing);
            // Snapshot only the recorded set. User-owned files are neither deleted nor restored.
            var backup = Path.Combine(StagingRoot, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(backup);
            InstalledPlugin? candidate = null;
            var retired = false;
            var written = new List<string>();
            try
            {
                CopyRecordedFiles(existing, backup);
                if (_participant is not null)
                    await _participant.DeactivateAsync(previousContext, cancellationToken).ConfigureAwait(false);
                retired = true;
                DeleteRecordedFiles(existing);
                var files = Promote(staged.Directory!, destination, written);
                files = WriteTrustCatalog(destination, name, files);
                candidate = existing with
                {
                    ResolvedVersion = staged.ResolvedVersion!,
                    ManifestVersion = staged.Manifest!.Version,
                    ManifestFormat = staged.Format,
                    InstalledAtUtc = _timeProvider.GetUtcNow(),
                    Files = files,
                };
                _store.Upsert(candidate);
                if (_participant is not null)
                    await _participant.ActivateAsync(new(candidate, staged.Manifest!, destination), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception updateError)
            {
                if (retired)
                {
                    using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    try
                    {
                        if (_participant is not null && candidate is not null)
                            await _participant.DeactivateAsync(new(candidate, staged.Manifest!, destination), cleanup.Token).ConfigureAwait(false);
                        // Promotion may itself have failed before returning a candidate receipt.
                        var candidateFiles = candidate?.Files ?? written;
                        DeleteRecordedFiles(existing with { Files = candidateFiles });
                        Promote(backup, destination);
                        _store.Upsert(existing);
                        if (_participant is not null)
                            await _participant.ActivateAsync(previousContext, cleanup.Token).ConfigureAwait(false);
                    }
                    catch (Exception compensationError)
                    {
                        // Preserve the snapshot for recovery if even the filesystem restore failed.
                        _logger.LogError(updateError, "Plugin {Plugin} update failed.", name);
                        _logger.LogError(compensationError, "Plugin {Plugin} update compensation failed; recovery snapshot retained at {RecoverySnapshot}.", name, backup);
                        return PluginOperationResult.Failure(name, "lifecycle",
                            "Update failed. Update compensation failed; recovery snapshot retained. Consult local logs for recovery.")
                            with { PreviousVersion = existing.ResolvedVersion };
                    }
                }
                _logger.LogWarning(updateError, "Plugin {Plugin} update failed; content and receipt retained or restored.", name);
                TryDeleteDirectory(backup);
                if (updateError is OperationCanceledException) throw;
                return PluginOperationResult.Failure(name, "lifecycle", "Update failed; installed content and receipt retained or restored.")
                    with { PreviousVersion = existing.ResolvedVersion };
            }
            TryDeleteDirectory(backup);
            var record = candidate ?? throw new InvalidOperationException("Update produced no installed receipt.");

            _logger.LogInformation(
                "Updated plugin {Plugin} from {From} to {To}.",
                name,
                existing.ResolvedVersion,
                record.ResolvedVersion);

            return new PluginOperationResult
            {
                Outcome = PluginOperationOutcome.Updated,
                Name = name,
                Plugin = record,
                PreviousVersion = existing.ResolvedVersion,
            };
        }
        finally
        {
            TryDeleteDirectory(staged.Directory!);
        }
    }

    /// <summary>
    /// Deletes exactly the files install recorded, prunes directories left empty by that
    /// deletion, and drops the installed record.
    /// </summary>
    /// <param name="name">Plugin identifier.</param>
    public PluginOperationResult Remove(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        // Compatibility for existing synchronous callers: never fire-and-forget teardown.
        return RemoveAsync(name).ConfigureAwait(false).GetAwaiter().GetResult();
    }

    /// <summary>Awaits runtime teardown before deleting recorded files or the installed receipt.</summary>
    public async Task<PluginOperationResult> RemoveAsync(string name, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await RemoveCoreAsync(name, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    private async Task<PluginOperationResult> RemoveCoreAsync(string name, CancellationToken cancellationToken)
    {
        var existing = _store.Find(name);
        if (existing is null)
        {
            return PluginOperationResult.Failure(name, "name", $"Plugin '{name}' is not installed.");
        }

        if (_participant is not null)
        {
            try
            {
                await _participant.DeactivateAsync(ReadContext(existing), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Plugin {Plugin} teardown failed; content and receipt retained.", name);
                return PluginOperationResult.Failure(name, "lifecycle", "Teardown failed; content and receipt retained.");
            }
        }
        DeleteRecordedFiles(existing);
        _store.Delete(name);

        _logger.LogInformation("Removed plugin {Plugin} ({FileCount} files).", name, existing.Files.Count);

        return new PluginOperationResult
        {
            Outcome = PluginOperationOutcome.Removed,
            Name = name,
            PreviousVersion = existing.ResolvedVersion,
        };
    }

    /// <summary>
    /// Sets whether update may replace a plugin's content. Exposed separately from install so a
    /// user can pin a plugin after the fact without reinstalling it.
    /// </summary>
    /// <param name="name">Plugin identifier.</param>
    /// <param name="updatesEnabled">New preference.</param>
    public PluginOperationResult SetUpdatePreference(string name, bool updatesEnabled)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        _mutationGate.Wait();
        try
        {
            return SetUpdatePreferenceCore(name, updatesEnabled);
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    private PluginOperationResult SetUpdatePreferenceCore(string name, bool updatesEnabled)
    {
        var existing = _store.Find(name);
        if (existing is null)
        {
            return PluginOperationResult.Failure(name, "name", $"Plugin '{name}' is not installed.");
        }

        var record = existing with { UpdatesEnabled = updatesEnabled };
        _store.Upsert(record);

        return new PluginOperationResult
        {
            Outcome = updatesEnabled ? PluginOperationOutcome.Installed : PluginOperationOutcome.SkippedPinned,
            Name = name,
            Plugin = record,
        };
    }

    private PluginLifecycleContext ReadContext(InstalledPlugin plugin)
    {
        var directory = GetPluginDirectory(plugin.Name);
        var parsed = _importer.ParseDirectory(directory, plugin.Name);
        // Teardown must remain possible after content is missing or tampered. Identity and source
        // come from the receipt; component discovery is only needed again on activation.
        var manifest = parsed.IsValid && parsed.Value is not null
            ? parsed.Value.Manifest : new PluginManifest { Name = plugin.Name };
        return new(plugin, manifest, directory);
    }

    private static bool HasCandidateCollision(InstalledPlugin existing, string staging, string destination)
    {
        if (!Directory.Exists(destination)) return false;
        var candidates = Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories)
            .Select(p => Path.GetRelativePath(staging, p).Replace('\\', '/'))
            .Where(p => !p.StartsWith(".git/", StringComparison.Ordinal))
            .Append(ContentTrustCatalog.CatalogFileName).Distinct(StringComparer.Ordinal).ToArray();
        var owned = existing.Files.ToHashSet(StringComparer.Ordinal);
        foreach (var entry in Directory.EnumerateFileSystemEntries(destination, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(destination, entry).Replace('\\', '/');
            var directory = Directory.Exists(entry);
            foreach (var candidate in candidates)
            {
                if (candidate.Equals(relative, StringComparison.OrdinalIgnoreCase))
                {
                    if (directory || !owned.Contains(relative) || candidate != relative) return true;
                }
                else if (candidate.StartsWith(relative + "/", StringComparison.OrdinalIgnoreCase))
                {
                    // Shared directories are safe only with identical portable spelling.
                    if (!directory || !candidate.StartsWith(relative + "/", StringComparison.Ordinal)) return true;
                }
                else if (relative.StartsWith(candidate + "/", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        return false;
    }

    private void CopyRecordedFiles(InstalledPlugin plugin, string backup)
    {
        var directory = GetPluginDirectory(plugin.Name);
        foreach (var relative in plugin.Files)
        {
            var source = Path.Combine(directory, relative.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(source)) throw new IOException("Recorded plugin content is missing; a complete recovery snapshot cannot be created.");
            var target = Path.Combine(backup, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(source, target);
        }
    }

    private sealed record StagedPlugin(
        string? Directory,
        string? ResolvedVersion,
        PluginManifest? Manifest,
        PluginOperationResult? Failure,
        PluginManifestFormat Format = PluginManifestFormat.Native);

    // Fetch and validate in a scratch directory under the OS temp root. Nothing here is inside
    // the plugin root, so a fault at any point in this method leaves the plugin root untouched -
    // which is the whole all-or-nothing guarantee, expressed structurally rather than by cleanup.
    private async Task<StagedPlugin> StageAsync(
        string source,
        string? reference,
        string? expectedName,
        CancellationToken cancellationToken)
    {
        var staging = Path.Combine(
            StagingRoot,
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);

        var reportName = expectedName ?? string.Empty;
        try
        {
            PluginFetchResult fetch;
            try
            {
                fetch = await _fetcher.FetchAsync(source, reference, staging, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Plugin source fetch failed.");
                TryDeleteDirectory(staging);
                return new StagedPlugin(null, null, null, PluginOperationResult.Failure(
                    reportName, "source", "Failed to fetch plugin source."));
            }

            if (string.IsNullOrWhiteSpace(fetch.ResolvedVersion))
            {
                TryDeleteDirectory(staging);
                return new StagedPlugin(null, null, null, PluginOperationResult.Failure(
                    reportName,
                    "resolvedVersion",
                    "Plugin source did not report a resolved version."));
            }

            var parse = _importer.ParseDirectory(staging, expectedName);
            if (!parse.IsValid)
            {
                TryDeleteDirectory(staging);
                return new StagedPlugin(null, null, null, PluginOperationResult.Failure(reportName, parse.Errors));
            }

            var imported = parse.Value!;
            var manifest = imported.Manifest;
            if (expectedName is not null && !string.Equals(expectedName, manifest.Name, StringComparison.Ordinal))
            {
                TryDeleteDirectory(staging);
                return new StagedPlugin(null, null, null, PluginOperationResult.Failure(
                    expectedName,
                    "name",
                    $"Plugin declares name '{manifest.Name}', which does not match requested name '{expectedName}'."));
            }

            return new StagedPlugin(staging, fetch.ResolvedVersion, manifest, null, imported.Format);
        }
        catch
        {
            TryDeleteDirectory(staging);
            throw;
        }
    }

    // Copies validated staging content into the plugin directory and returns the relative path of
    // every file written - which becomes the removal manifest. Paths are normalised to forward
    // slashes so a record written on Windows still removes correctly on Linux.
    private static List<string> Promote(string staging, string destination, List<string>? written = null)
    {
        Directory.CreateDirectory(destination);
        written ??= new List<string>();

        foreach (var sourceFile in Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(staging, sourceFile).Replace('\\', '/');

            // Git working metadata is an artefact of the transport, not plugin content, and
            // copying it would make every plugin directory a nested repository.
            if (relative.StartsWith(".git/", StringComparison.Ordinal))
            {
                continue;
            }

            var targetPath = Path.Combine(destination, relative.Replace('/', Path.DirectorySeparatorChar));
            var targetDir = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrEmpty(targetDir))
            {
                Directory.CreateDirectory(targetDir);
            }

            // Include even a partial copy that throws; the target was preflighted as owned/free.
            written.Add(relative);
            File.Copy(sourceFile, targetPath, overwrite: true);
        }

        written.Sort(StringComparer.Ordinal);
        return written;
    }

    /// <summary>
    /// Records a SHA-256 catalog over what was just materialised (#2682 AC1).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Generated from the CONTENT ON DISK rather than from <c>Files</c> or from anything the
    /// manifest declared. Those are claims about what should have been written; the hash must
    /// describe what actually was, or the catalog attests to the wrong thing.
    /// </para>
    /// <para>
    /// Update regenerates it for the same reason install writes it: after new content is promoted,
    /// the previous catalog describes files that no longer exist, and every later verification
    /// would refuse a legitimately updated plugin.
    /// </para>
    /// <para>
    /// A catalog that cannot be written must NOT fail an install that has already materialised its
    /// content - the plugin is on disk either way, and reporting a failed install the caller could
    /// act on destructively is worse than an unverifiable plugin. Under Enforce the missing catalog
    /// is itself a refusal, so failing open here does not hand out trust.
    /// </para>
    /// <para>
    /// The catalog is added to the returned removal manifest because install genuinely materialised
    /// it. Leaving it out would make removal leave a <c>trust.json</c> husk behind - and worse, a
    /// later reinstall would find a directory that exists but is not recorded, which install
    /// correctly refuses.
    /// </para>
    /// </remarks>
    /// <param name="destination">Plugin directory just materialised.</param>
    /// <param name="pluginName">Plugin identifier, for the log record.</param>
    /// <param name="files">Files <c>Promote</c> wrote.</param>
    /// <returns>The removal manifest, including the catalog when one was written.</returns>
    private List<string> WriteTrustCatalog(string destination, string pluginName, List<string> files)
    {
        try
        {
            var catalog = ContentTrustCatalog.GenerateCatalog(
                destination,
                includeFile: ContentTrustCatalog.IncludeEveryFile,
                generatedAt: _timeProvider.GetUtcNow());

            ContentTrustCatalog.WriteCatalog(destination, catalog);

            _logger.LogInformation(
                "Recorded trust catalog for plugin {Plugin} covering {EntryCount} files.",
                pluginName,
                catalog.Entries.Count);

            if (!files.Contains(ContentTrustCatalog.CatalogFileName, StringComparer.Ordinal))
            {
                files.Add(ContentTrustCatalog.CatalogFileName);
                files.Sort(StringComparer.Ordinal);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(
                ex,
                "Plugin {Plugin} was materialised, but its trust catalog could not be written. The plugin will not verify under Warn or Enforce.",
                pluginName);
        }

        return files;
    }

    // Deletes the recorded set only. Directories are pruned bottom-up and ONLY when empty, so a
    // file the user added inside a plugin subdirectory both survives and keeps its directory.
    private void DeleteRecordedFiles(InstalledPlugin plugin)
    {
        var directory = GetPluginDirectory(plugin.Name);
        if (!Directory.Exists(directory))
        {
            return;
        }

        foreach (var relative in plugin.Files)
        {
            var path = Path.Combine(directory, relative.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }

        PruneEmptyDirectories(directory, isRoot: true);
    }

    private static void PruneEmptyDirectories(string directory, bool isRoot)
    {
        foreach (var child in Directory.EnumerateDirectories(directory))
        {
            PruneEmptyDirectories(child, isRoot: false);
        }

        if (!isRoot && !Directory.EnumerateFileSystemEntries(directory).Any())
        {
            Directory.Delete(directory);
            return;
        }

        if (isRoot && !Directory.EnumerateFileSystemEntries(directory).Any())
        {
            Directory.Delete(directory);
        }
    }

    private void TryDeleteDirectory(string? directory)
    {
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
        {
            return;
        }

        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Could not delete plugin staging directory {Directory}.", directory);
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning(ex, "Could not delete plugin staging directory {Directory}.", directory);
        }
    }
}
