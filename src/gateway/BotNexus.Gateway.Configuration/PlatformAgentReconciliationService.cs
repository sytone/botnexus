using System.IO.Abstractions;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BotNexus.Gateway.Configuration;

/// <summary>
/// Hosted service that additively reconciles the bundled agent catalog
/// (<see cref="BundledPlatformAgents"/>) into <c>config.json</c> on startup.
/// </summary>
/// <remarks>
/// <para>
/// Issue #2635. BotNexus previously had no seam for shipping a user-visible, config-defined agent
/// to an <em>existing</em> installation: <c>install</c> clones and builds, <c>update</c> pulls and
/// restarts, and neither is a migration point. <see cref="ConfigHydrationService"/> deep-adds
/// schema defaults but is aimed at ordinary configuration sections, not keyed lifecycle-managed
/// agents, and <c>PlatformConfigAgentWriter.SaveAsync</c> always writes <c>enabled: true</c> — so
/// routing reconciliation through it would silently resurrect an agent the user turned off. This
/// service exists to close that gap and nothing else.
/// </para>
/// <para>
/// The contract is deliberately narrow:
/// </para>
/// <list type="bullet">
///   <item><b>Insert-only.</b> If <c>agents.&lt;id&gt;</c> exists in any form — enabled, disabled,
///   edited, or half-filled — the service performs no write at all. It does not merge, top up
///   missing fields, or "repair" the entry. The user owns it from the moment it exists.</item>
///   <item><b>Non-fatal.</b> Malformed JSON or a read-only config file produces exactly one
///   bounded warning and startup continues, mirroring <see cref="ConfigHydrationService"/>.</item>
///   <item><b>Ordered before agent registration.</b> Registered ahead of
///   <c>AgentConfigurationHostedService</c> so an inserted entry is visible to the normal config
///   agent source within the same startup rather than only after a restart.</item>
/// </list>
/// </remarks>
public sealed class PlatformAgentReconciliationService : IHostedService
{
    private readonly PlatformConfigWriter _writer;
    private readonly IReadOnlyList<BundledAgentDefinition> _catalog;
    private readonly ILogger _logger;

    public PlatformAgentReconciliationService(
        PlatformConfigWriter writer,
        ILogger<PlatformAgentReconciliationService> logger)
        : this(writer, BundledPlatformAgents.All, logger)
    {
    }

    public PlatformAgentReconciliationService(
        PlatformConfigWriter writer,
        IReadOnlyList<BundledAgentDefinition> catalog,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(logger);

        _writer = writer;
        _catalog = catalog;
        _logger = logger;
    }

    /// <summary>
    /// Builds a reconciliation service for a BotNexus home, honouring <c>BOTNEXUS_DATA_DIR</c>.
    /// </summary>
    /// <remarks>
    /// Config is read from (and written back to) <see cref="BotNexusHome.RootPath"/>, which may be
    /// a read-only mount, while backups are taken under <see cref="BotNexusHome.DataPath"/> — the
    /// directory <c>BOTNEXUS_DATA_DIR</c> designates as writable. Pointing backups at the config
    /// directory instead would make a containerised install fail its backup before the (equally
    /// doomed) write, producing a confusing second failure mode.
    /// </remarks>
    public static PlatformAgentReconciliationService Create(
        BotNexusHome home,
        IFileSystem fileSystem,
        ILogger logger,
        IReadOnlyList<BundledAgentDefinition>? catalog = null)
    {
        ArgumentNullException.ThrowIfNull(home);
        ArgumentNullException.ThrowIfNull(fileSystem);

        var configPath = fileSystem.Path.Combine(home.RootPath, "config.json");
        var backup = new ConfigBackupService(ResolveBackupDirectory(home), fileSystem);
        return new PlatformAgentReconciliationService(
            new PlatformConfigWriter(configPath, fileSystem, backup),
            catalog ?? BundledPlatformAgents.All,
            logger);
    }

    /// <summary>
    /// The directory config backups are written to: always under the writable data directory
    /// (<c>BOTNEXUS_DATA_DIR</c> when set), never the possibly read-only config root.
    /// </summary>
    public static string ResolveBackupDirectory(BotNexusHome home)
    {
        ArgumentNullException.ThrowIfNull(home);
        return Path.Combine(home.DataPath, "backups");
    }

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (_catalog.Count == 0)
            return;

        // Best-effort, exactly like ConfigHydrationService: the gateway must start even when
        // config.json is unparseable or the mount is read-only. One bounded warning, then on with
        // startup — a bundled onboarding agent is never worth failing a boot over.
        try
        {
            var snapshot = await _writer.ReadAsync(cancellationToken);
            var missing = _catalog.Where(d => !EntryExists(snapshot, d.AgentId)).ToList();
            if (missing.Count == 0)
            {
                _logger.LogDebug(
                    "Bundled agent reconciliation: nothing to do, all {Count} bundled agents already present in config.",
                    _catalog.Count);
                return;
            }

            List<string> inserted = [];

            // Re-run the existence check inside the writer's locked read-modify-write so the
            // decision is made against the authoritative on-disk document, not the detached
            // snapshot above. Without this a concurrent writer could add the key between the
            // read and the mutation and we would clobber it.
            await _writer.MutateAsync(root =>
            {
                foreach (var definition in missing)
                {
                    if (EntryExists(root, definition.AgentId))
                        continue;

                    if (root["agents"] is not JsonObject agents)
                    {
                        agents = new JsonObject();
                        root["agents"] = agents;
                    }

                    agents[definition.AgentId] = BuildEntry(root, definition);
                    inserted.Add(definition.AgentId);
                }
            }, "bundled-agent-reconciliation", cancellationToken);

            if (inserted.Count > 0)
            {
                _logger.LogInformation(
                    "Bundled agent reconciliation inserted {Count} agent(s) into config.json: {AgentIds}",
                    inserted.Count,
                    string.Join(", ", inserted));
            }
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex,
                "Bundled agent reconciliation skipped — config.json is not valid JSON. "
                + "The gateway will start without the bundled agents; fix the JSON and restart.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(
                "Bundled agent reconciliation skipped — config.json is not writable "
                + "(read-only mount or permission denied: {Reason}). The gateway will start without the bundled agents.",
                ex.Message);
        }
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Whether the config document already carries an entry for <paramref name="agentId"/>.
    /// </summary>
    /// <remarks>
    /// Presence of the <em>key</em> is the whole test. A disabled, empty, or malformed entry still
    /// counts as present: the user (or a previous run) put it there, and reconciliation is not
    /// entitled to an opinion about its contents.
    /// </remarks>
    internal static bool EntryExists(JsonObject root, string agentId)
        => root["agents"] is JsonObject agents && agents.ContainsKey(agentId);

    private static JsonObject BuildEntry(JsonObject root, BundledAgentDefinition definition)
        => BuildEntry(definition, ResolveProviderAndModel(root));

    /// <summary>
    /// Materialises the config entry for a bundled agent from its template and an already-resolved
    /// provider/model pair.
    /// </summary>
    /// <remarks>
    /// Issue #2636. This overload is the seam <see cref="FreshInstallAgentDefaults"/> uses so that
    /// <c>botnexus init</c> emits byte-identical entries to the ones this service would insert.
    /// Only the <em>resolution</em> of provider/model differs between the two callers - a fresh
    /// install has no existing agent to copy from, so it supplies the fresh-install pair directly -
    /// and the entry construction itself must never be duplicated.
    /// </remarks>
    /// <param name="definition">The bundled agent to materialise.</param>
    /// <param name="resolved">
    /// The provider/model to adopt, or <see langword="null"/> when none could be resolved, in which
    /// case the entry is produced disabled with an actionable description.
    /// </param>
    internal static JsonObject BuildEntry(
        BundledAgentDefinition definition,
        (string Provider, string Model)? resolved)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var entry = definition.CreateTemplate();

        entry[BundledPlatformAgents.DefinitionVersionMetadataKey] = definition.DefinitionVersion;

        if (resolved is null)
        {
            // No provider/model anywhere in this installation. Writing a half-formed enabled
            // agent would just produce a validation error on every config load, so insert it
            // disabled and say plainly in the description what the operator has to fill in.
            entry["enabled"] = false;
            entry["description"] = BundledPlatformAgents.UnresolvedProviderDescription;
            return entry;
        }

        entry["provider"] = resolved.Value.Provider;
        entry["model"] = resolved.Value.Model;
        entry["enabled"] = true;

        return entry;
    }

    /// <summary>
    /// Resolves the provider/model pair a newly inserted bundled agent should adopt.
    /// </summary>
    /// <remarks>
    /// <para>Order, per #2635:</para>
    /// <list type="number">
    ///   <item>An existing entry wins — handled by the caller, which never reaches this method
    ///   when the key is already present.</item>
    ///   <item>The <c>gateway.defaultAgentId</c> agent, when it declares a valid provider and
    ///   model. This is the installation's own answer to "which model do I use", so copying it
    ///   gives the bundled agent the behaviour the user already trusts.</item>
    ///   <item>Otherwise the first enabled config agent with a valid provider and model.</item>
    ///   <item>Otherwise <see langword="null"/> — the caller inserts a disabled entry with an
    ///   actionable description rather than guessing a provider that does not exist.</item>
    /// </list>
    /// </remarks>
    internal static (string Provider, string Model)? ResolveProviderAndModel(JsonObject root)
    {
        if (root["agents"] is not JsonObject agents)
            return null;

        var defaultAgentId = root["gateway"]?["defaultAgentId"]?.GetValue<string>();
        if (!string.IsNullOrWhiteSpace(defaultAgentId)
            && agents[defaultAgentId] is JsonObject defaultAgent
            && TryReadProviderAndModel(defaultAgent, out var fromDefault))
        {
            return fromDefault;
        }

        foreach (var (agentId, node) in agents)
        {
            if (string.Equals(agentId, "defaults", StringComparison.OrdinalIgnoreCase))
                continue;

            if (node is not JsonObject agent)
                continue;

            // Mirrors PlatformConfigAgentSource: a disabled agent is not a running agent, so its
            // provider/model is not evidence that this installation can actually reach a model.
            if (agent["enabled"] is JsonNode enabled && enabled.GetValueKind() == JsonValueKind.False)
                continue;

            if (TryReadProviderAndModel(agent, out var fromAgent))
                return fromAgent;
        }

        return null;
    }

    private static bool TryReadProviderAndModel(JsonObject agent, out (string Provider, string Model) result)
    {
        result = default;

        var provider = ReadString(agent, "provider");
        var model = ReadString(agent, "model");
        if (string.IsNullOrWhiteSpace(provider) || string.IsNullOrWhiteSpace(model))
            return false;

        result = (provider, model);
        return true;
    }

    private static string? ReadString(JsonObject obj, string key)
        => obj[key] is JsonValue value && value.GetValueKind() == JsonValueKind.String
            ? value.GetValue<string>()
            : null;
}
