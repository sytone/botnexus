using System.IO.Abstractions;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace BotNexus.Gateway.Configuration;

/// <summary>
/// Thread-safe writer for platform config JSON files.
/// Performs atomic read-modify-write with file locking.
/// </summary>
/// <remarks>
/// <para><b>Explicit-null semantics (#2705).</b> config.json has THREE distinct states per key:
/// key absent, key present with value <c>null</c>, and key present with a value.
/// <see cref="AgentConfigMerger" /> depends on that distinction in six places
/// (<c>memory</c>, <c>search</c>, <c>temporalDecay</c>, <c>heartbeat</c>, <c>quietHours</c>,
/// <c>fileAccess</c>): an explicit null means <em>suppress the inherited world default</em>,
/// whereas absence means <em>inherit it</em>.</para>
/// <para>The writer therefore guarantees that <b>an explicit null present in the document on disk
/// survives a whole-document write</b>. This is a deliberate contract, not an implementation
/// accident: the typed <see cref="PlatformConfig" /> graph cannot represent "present and null"
/// (a null CLR property and an absent one are the same state), and the serializer's
/// <see cref="JsonIgnoreCondition.WhenWritingNull" /> policy - chosen for output tidiness - would
/// otherwise silently downgrade every explicit null to an absent key and invert the operator's
/// intent on a write they did not initiate.</para>
/// <para>The asymmetry is intentional: an explicit null can only be removed by an explicit
/// raw-document edit (<see cref="MutateAsync(Action{JsonObject}, string, CancellationToken)" /> or
/// <see cref="MutateValidatedAsync" />), never as a side effect of a typed whole-document replace.
/// Any future configuration store (#2646) must reproduce this behaviour deliberately.</para>
/// <para><b>Destructive-section guard (#2816).</b> Every write - without exception - passes through
/// the single private pipeline <see cref="MutateCoreAsync" />, which applies
/// <see cref="ConfigSectionGuard" /> to the candidate document before the file is touched. A
/// candidate that would drop or empty a populated top-level section the caller did not
/// <em>name</em> is refused, and the bytes on disk are left exactly as they were. This exists
/// because a 2026-07-31 production write flattened the whole <c>channels</c> section to a single
/// defaulted property and destroyed live credentials while reporting success; see
/// <see cref="ConfigSectionGuard" /> for the full rationale and for why the guard deliberately has
/// no bypass flag. New write methods must route through <see cref="MutateCoreAsync" /> rather than
/// re-implementing the read/mutate/validate/write sequence, or they will silently opt out of it.</para>
/// </remarks>
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
                // #2816: sectionName is declared to the guard below, so this path may legitimately
                // clear the section it was asked to replace - and only that section.
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
            ct,
            namedSections: [sectionName]);

    /// <summary>
    /// Reads the current platform configuration together with the revision token it was read at.
    /// </summary>
    /// <remarks>
    /// Issue #2134. A caller that intends to replace the <em>whole</em> document later must be able
    /// to prove nothing else committed in between. The revision is a content digest of the exact
    /// bytes-equivalent document this snapshot was materialised from; pass it back to
    /// <see cref="UpdatePlatformConfigAsync"/> to get a compare-and-swap instead of a blind
    /// last-writer-wins overwrite.
    /// </remarks>
    public async Task<(PlatformConfig Config, string Revision)> ReadPlatformConfigWithRevisionAsync(CancellationToken ct = default)
    {
        var root = await ReadRootAsync(ct);
        var json = root.ToJsonString();
        var config = JsonSerializer.Deserialize<PlatformConfig>(json, PlatformReadOptions) ?? new PlatformConfig();
        return (config, ComputeRevision(root));
    }

    /// <summary>
    /// Replaces the entire platform configuration document.
    /// </summary>
    /// <param name="config">The complete replacement document.</param>
    /// <param name="reason">Backup reason label recorded when the write proceeds.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="expectedRevision">
    /// Optional compare-and-swap guard (#2134). When supplied, the write is rejected with a
    /// <see cref="PlatformConfigConcurrencyException"/> if the document on disk no longer matches
    /// the revision the caller's snapshot was read at - i.e. another writer committed in between
    /// and this whole-document replace would silently discard their changes. When
    /// <see langword="null"/> the historical last-writer-wins behaviour is preserved, so existing
    /// callers are unaffected.
    /// </param>
    /// <param name="namedSections">
    /// Top-level sections this replacement is explicitly entitled to empty or remove (#2816).
    /// Defaults to none: a whole-document replace is exactly the shape of write that destroyed a
    /// production <c>channels</c> section, so by default it is held to the same guard as every
    /// other path and is refused if it silently flattens something. Pass the section names only
    /// from a caller whose declared job is to regenerate those sections wholesale.
    /// </param>
    public async Task UpdatePlatformConfigAsync(
        PlatformConfig config,
        string reason,
        CancellationToken ct = default,
        string? expectedRevision = null,
        IReadOnlyCollection<string>? namedSections = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        await MutateAsync(root =>
        {
            // The check runs inside the writer lock, against the root that was read inside the
            // same lock, so no writer can interleave between the comparison and the replace.
            if (expectedRevision is not null)
            {
                var actual = ComputeRevision(root);
                if (!string.Equals(actual, expectedRevision, StringComparison.Ordinal))
                    throw new PlatformConfigConcurrencyException(_configPath, expectedRevision, actual);
            }

            var serialized = JsonSerializer.Serialize(config, PlatformWriteOptions);
            var next = JsonNode.Parse(serialized)?.AsObject() ?? new JsonObject();

            // #2705: re-apply the explicit nulls the operator wrote, BEFORE clearing the root.
            //
            // PlatformWriteOptions uses WhenWritingNull for output tidiness, and the typed
            // PlatformConfig graph cannot represent "present and null" at all - a null property
            // and an absent property are the same CLR state. So a whole-document write through
            // the typed model erases every explicit null in config.json.
            //
            // That is not cosmetic. AgentConfigMerger treats absent / explicit-null / value as
            // THREE distinct states in six places (memory, search, temporalDecay, heartbeat,
            // quietHours, fileAccess): explicit null means "suppress the inherited default",
            // absence means "inherit it". Erasing the null therefore flips the setting to the
            // opposite of what the operator wrote, silently, on a write they did not initiate.
            //
            // Two rejected alternatives, recorded so they are not "simplified" back in:
            //  - Dropping WhenWritingNull globally would spray nulls for every unset optional
            //    property across the whole document - a far larger behaviour change than the
            //    defect warrants, and it would not help the keys the merger reads anyway,
            //    because the typed model cannot tell "operator wrote null" from "never set".
            //  - Changing the merger's meaning of null is wrong: the merger is correct and six
            //    call sites depend on it.
            // Instead the preservation is scoped precisely to keys that were explicitly null in
            // the SOURCE document, and only where the regenerated document left them absent - a
            // real value in the new document always wins.
            RestoreExplicitNulls(root, next);

            root.Clear();
            foreach (var kvp in next)
                root[kvp.Key] = kvp.Value?.DeepClone();
        }, reason, ct, namedSections);
    }

    /// <summary>
    /// Mutates a single named section <em>inside</em> the writer lock and persists the result only
    /// when the resulting complete candidate document validates.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Issue #2134. The pre-existing shape of the config write path was:
    /// <c>read snapshot -&gt; modify it -&gt; hand the finished snapshot to UpdateSectionAsync</c>.
    /// Only the last step took the writer lock, so the read-modify-window sat entirely
    /// <em>outside</em> mutual exclusion and two concurrent callers each replaced the section with
    /// their own stale-plus-one view; whichever wrote second erased the other's entry.
    /// </para>
    /// <para>
    /// This API closes that window by inverting the control flow: the caller supplies the
    /// modification, not the finished snapshot, and the writer reads the section, applies the
    /// modification, validates and writes all under the one lock. Adding more locking around the
    /// old shape would not have helped - the defect was what the lock <em>spanned</em>.
    /// </para>
    /// </remarks>
    /// <param name="sectionName">The root section to mutate (created when absent).</param>
    /// <param name="mutation">
    /// Mutates the live section object in place and returns <see langword="null"/> on success, or a
    /// caller-presentable message to abort the write (for example a duplicate-name conflict).
    /// When the mutation aborts, nothing is written and the file is left untouched.
    /// </param>
    /// <param name="reason">Backup reason label recorded when the write proceeds.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The rejection messages; empty when the mutation was validated and persisted.</returns>
    public async Task<IReadOnlyList<string>> MutateSectionAsync(
        string sectionName,
        Func<JsonObject, string?> mutation,
        string reason,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sectionName);
        ArgumentNullException.ThrowIfNull(mutation);

        // #2816: this call names exactly one section, so the guard permits it to empty that one
        // and nothing else - a mutation aimed at gateway still cannot flatten channels.
        return await MutateValidatedAsync(
            root =>
            {
                if (root[sectionName] is not JsonObject section)
                {
                    section = new JsonObject();
                    root[sectionName] = section;
                }

                return mutation(section);
            },
            reason,
            ct,
            namedSections: [sectionName]);
    }

    /// <summary>
    /// Copies explicit JSON nulls from <paramref name="source" /> (the document as it exists on
    /// disk) into <paramref name="target" /> (the regenerated document), at the same paths, but
    /// only where <paramref name="target" /> has no key at all.
    /// </summary>
    /// <remarks>
    /// #2705. Deliberately conservative:
    /// <list type="bullet">
    ///   <item>Only keys whose SOURCE value is JSON null are considered, so this never invents a
    ///   null the operator did not write.</item>
    ///   <item>A key the regenerated document supplies with a value is left alone, so a caller
    ///   that genuinely sets a previously-null section still wins.</item>
    ///   <item>Recursion descends only where BOTH sides still have an object, so a subtree the
    ///   caller deleted wholesale (for example a removed agent) does not come back.</item>
    /// </list>
    /// </remarks>
    private static void RestoreExplicitNulls(JsonObject source, JsonObject target)
    {
        foreach (var kvp in source)
        {
            if (kvp.Value is null)
            {
                if (!target.ContainsKey(kvp.Key))
                    target[kvp.Key] = null;
                continue;
            }

            if (kvp.Value is JsonObject childSource && target[kvp.Key] is JsonObject childTarget)
                RestoreExplicitNulls(childSource, childTarget);
        }
    }

    /// <summary>
    /// Reads the raw configuration document together with the revision token it was read at
    /// (issue #2059).
    /// </summary>
    /// <remarks>
    /// The settings UI needs the revision of the <em>document</em>, not of a typed projection: it
    /// edits raw JSON paths (including sections and keys <see cref="PlatformConfig"/> does not
    /// model) and must be able to prove that nothing else committed between the load it rendered
    /// from and the save it submits. Pair with <see cref="ApplyPatchAsync"/>.
    /// </remarks>
    public async Task<(JsonObject Config, string Revision)> ReadWithRevisionAsync(CancellationToken ct = default)
    {
        var root = await ReadRootAsync(ct);
        return (root, ComputeRevision(root));
    }

    /// <summary>
    /// Applies a batch of addressed patch operations as one all-or-nothing write, optionally
    /// guarded by the revision the caller's snapshot was read at (issue #2059).
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the write shape the settings UI needs and previously did not have. Its predecessor
    /// looped over every materialised top-level section issuing an independent
    /// <c>PUT /api/config/{section}</c>. That loop had three defects, all of which this method
    /// closes structurally rather than by convention:
    /// </para>
    /// <list type="number">
    ///   <item><b>No dirty tracking.</b> Sections the operator never touched were rewritten from a
    ///   stale snapshot, so a concurrent edit elsewhere was silently reverted. A patch carries only
    ///   the paths that changed, so an untouched section is not part of the write at all.</item>
    ///   <item><b>No concurrency check.</b> Every PUT was last-writer-wins. Supplying
    ///   <paramref name="expectedRevision"/> turns the save into a compare-and-swap: a save built
    ///   on a stale read is rejected with <see cref="PlatformConfigConcurrencyException"/> so the
    ///   UI can reload and re-apply, instead of destroying the other writer's change.</item>
    ///   <item><b>No atomicity.</b> The loop committed section-by-section and aborted mid-way on
    ///   the first failure, leaving the document half-saved. Here the whole batch is applied to an
    ///   in-memory candidate inside the writer lock; if any operation, the guard, or validation
    ///   rejects it, nothing at all is written.</item>
    /// </list>
    /// <para>
    /// Redacted secrets are restored from the document on disk before the candidate is validated,
    /// so a field the UI rendered as <c>***</c> and submitted back verbatim cannot overwrite the
    /// real value (#1955).
    /// </para>
    /// <para>
    /// Scope note: this closes the <em>portal-side</em> hole. Coordination between independent
    /// writers beyond this process is #2060, and the validate-before-commit contract is #2058 -
    /// this method reuses the existing candidate validation rather than redefining it.
    /// </para>
    /// </remarks>
    /// <param name="operations">The addressed mutations, applied in order.</param>
    /// <param name="reason">Backup reason label recorded when the write proceeds.</param>
    /// <param name="expectedRevision">
    /// Optional compare-and-swap token from <see cref="ReadWithRevisionAsync"/>. When
    /// <see langword="null"/> the write proceeds without a concurrency check.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The outcome, carrying the newly committed revision on success.</returns>
    /// <exception cref="PlatformConfigConcurrencyException">
    /// The document on disk no longer matches <paramref name="expectedRevision"/>. Nothing was
    /// written.
    /// </exception>
    public async Task<ConfigPatchResult> ApplyPatchAsync(
        IReadOnlyList<ConfigPatchOperation> operations,
        string reason,
        string? expectedRevision = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(operations);

        if (operations.Count == 0)
            return new ConfigPatchResult(true, null, []);

        string? committedRevision = null;

        var errors = await MutateCoreAsync(
            root =>
            {
                // The comparison runs inside the writer lock against the root read inside the same
                // lock, so no writer can interleave between the check and the write.
                if (expectedRevision is not null)
                {
                    var actual = ComputeRevision(root);
                    if (!string.Equals(actual, expectedRevision, StringComparison.Ordinal))
                        throw new PlatformConfigConcurrencyException(_configPath, expectedRevision, actual);
                }

                // Apply to a candidate first: a failure part-way through must not leave the live
                // root half-mutated, because MutateCoreAsync's guard compares against it.
                var candidate = root.DeepClone().AsObject();
                var applyError = ConfigPatchApplier.Apply(candidate, operations);
                if (applyError is not null)
                    return Task.FromResult<string?>(applyError);

                ConfigSecretMerge.RestoreSecrets(root, candidate);

                root.Clear();
                foreach (var kvp in candidate)
                    root[kvp.Key] = kvp.Value?.DeepClone();

                committedRevision = ComputeRevision(root);
                return Task.FromResult<string?>(null);
            },
            reason,
            validateCandidate: true,
            ConfigPatchApplier.DeclaredSections(operations),
            ct);

        return errors.Count > 0
            ? new ConfigPatchResult(false, null, errors)
            : new ConfigPatchResult(true, committedRevision, []);
    }

    /// <summary>
    /// Computes the revision token for a configuration document: a stable content digest of its
    /// canonical serialization, used as the compare-and-swap token for whole-document replaces.
    /// </summary>
    private static string ComputeRevision(JsonObject root)
    {
        var canonical = root.ToJsonString(PlatformPersistOptions);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
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
        }, $"before-{sectionName}-update", ct, namedSections: [sectionName]);

    /// <summary>
    /// Atomically mutates the config document and persists the result.
    /// </summary>
    /// <param name="namedSections">
    /// Top-level sections the caller explicitly declares it is operating on, so the
    /// destructive-section guard (#2816) permits this write to empty or remove them. Leave
    /// <see langword="null" /> for a targeted edit that is not supposed to destroy anything.
    /// </param>
    public async Task MutateAsync(
        Action<JsonObject> mutation,
        string reason,
        CancellationToken ct = default,
        IReadOnlyCollection<string>? namedSections = null)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        await MutateAsync(root =>
        {
            mutation(root);
            return Task.CompletedTask;
        }, reason, ct, namedSections);
    }

    /// <summary>
    /// Atomically mutates the config document and persists the result.
    /// </summary>
    /// <param name="namedSections">
    /// Top-level sections the caller explicitly declares it is operating on (#2816). See the
    /// overload above.
    /// </param>
    /// <exception cref="PlatformConfigSectionGuardException">
    /// The candidate document would have destroyed a populated top-level section that
    /// <paramref name="namedSections" /> does not name. Nothing was written.
    /// </exception>
    public async Task MutateAsync(
        Func<JsonObject, Task> mutation,
        string reason,
        CancellationToken ct = default,
        IReadOnlyCollection<string>? namedSections = null)
    {
        ArgumentNullException.ThrowIfNull(mutation);

        var errors = await MutateCoreAsync(
            async root =>
            {
                await mutation(root);
                return null;
            },
            reason,
            validateCandidate: false,
            namedSections,
            ct);

        // This overload has no error channel in its signature, so a guard rejection must throw or
        // it would be indistinguishable from a successful write - which is precisely the silent
        // failure mode #2816 is about.
        if (errors.Count > 0)
            throw new PlatformConfigSectionGuardException(errors[0]);
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
    /// <param name="namedSections">
    /// Top-level sections the caller explicitly declares it is operating on, so the
    /// destructive-section guard (#2816) permits this write to empty or remove them. A guard
    /// rejection is returned through the same error list as a validation failure, so existing
    /// callers report it and exit non-zero without any change.
    /// </param>
    public async Task<IReadOnlyList<string>> MutateValidatedAsync(
        Func<JsonObject, string?> mutation,
        string reason,
        CancellationToken ct = default,
        IReadOnlyCollection<string>? namedSections = null)
    {
        ArgumentNullException.ThrowIfNull(mutation);

        return await MutateCoreAsync(
            root => Task.FromResult(mutation(root)),
            reason,
            validateCandidate: true,
            namedSections,
            ct);
    }

    /// <summary>
    /// The single read-modify-guard-validate-write pipeline every public write method funnels
    /// through.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Issue #2816. The destructive-section guard is applied here, once, rather than in each public
    /// method. That is the whole design decision: the incident it prevents was never attributed to
    /// a specific command, so a per-command check would have been re-derived four ways, forgotten
    /// by the fifth command, and possibly absent from the path that actually caused the damage. A
    /// method that bypasses this pipeline bypasses the guard, so do not add one.
    /// </para>
    /// <para>
    /// Ordering inside the lock is deliberate: the pristine document is captured BEFORE the
    /// mutation runs (the mutation edits the live root in place), the guard then compares pristine
    /// against candidate, and only a candidate that passes both the guard and validation reaches
    /// <see cref="WriteRootAsync" />. A rejected write therefore leaves the file byte-for-byte
    /// unchanged and takes no backup.
    /// </para>
    /// </remarks>
    /// <param name="validateCandidate">
    /// Whether to run full <c>PlatformConfigLoader.ValidateRawJson</c> validation on the candidate.
    /// Only the validated write paths do; the raw <c>MutateAsync</c> paths historically do not, and
    /// turning that on here would change behaviour well beyond #2816.
    /// </param>
    private async Task<IReadOnlyList<string>> MutateCoreAsync(
        Func<JsonObject, Task<string?>> mutation,
        string reason,
        bool validateCandidate,
        IReadOnlyCollection<string>? namedSections,
        CancellationToken ct)
    {
        // Lock order is always semaphore -> cross-process file lock (#2134). See
        // CrossProcessConfigLock for the ordering/deadlock argument: the semaphore keeps this
        // process's own threads from queueing on the OS lock, and the file lock extends the same
        // critical section across the CLI/gateway process boundary.
        await WriteLock.WaitAsync(ct);
        try
        {
            using var crossProcess = await CrossProcessConfigLock.AcquireAsync(_configPath, _fileSystem, ct);
            var root = await ReadRootAsync(ct);

            // Snapshot the pristine document: mutations edit root in place, so this is the only
            // moment the pre-mutation state still exists to compare against.
            var pristine = root.DeepClone().AsObject();

            var mutationError = await mutation(root);
            if (!string.IsNullOrWhiteSpace(mutationError))
                return [mutationError];

            var destroyed = ConfigSectionGuard.FindDestroyedSections(pristine, root, namedSections);
            if (destroyed.Count > 0)
                return [ConfigSectionGuard.FormatRejection(_configPath, destroyed)];

            if (validateCandidate)
            {
                // Validate the complete candidate document, not just the mutated fragment: a
                // locally plausible edit can still violate a cross-field rule elsewhere in the
                // graph.
                var candidateJson = root.ToJsonString(PlatformPersistOptions);
                var errors = PlatformConfigLoader.ValidateRawJson(candidateJson);
                if (errors.Count > 0)
                    return errors;
            }

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
        }, $"before-{sectionName}-remove", ct, namedSections: [sectionName]);

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
