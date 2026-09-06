using System.IO.Abstractions;
using System.Text.Json;
using System.Text.Json.Serialization;
using BotNexus.Extensions.Plugins.Lifecycle;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Security;
using BotNexus.Domain.Primitives;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotNexus.Extensions.Plugins.Agents;

/// <summary>
/// Surfaces agent descriptors shipped by installed plugins as a second
/// <see cref="IAgentConfigurationSource"/> (#2685).
/// </summary>
/// <remarks>
/// <para>
/// <b>A source, not new machinery.</b> <c>AgentConfigurationHostedService</c> already reconciles an
/// arbitrary number of sources - loading each at startup, watching each for change, and merging the
/// results. Plugin-shipped agents therefore need no reconciliation logic of their own, and this
/// type deliberately adds none: it implements the existing interface and nothing else. A second
/// reconciliation path is how two sets of agents drift into disagreeing about which descriptor won.
/// </para>
/// <para>
/// <b>The installed record is the authority, not the directory listing</b> - the same posture
/// <see cref="PluginSkillRootResolver"/> takes for skills (#2684). Descriptors are read only from
/// plugins that <see cref="PluginStateStore"/> records as installed, because an unrecorded
/// directory has no known provenance; surfacing an agent out of one would let anybody register an
/// agent by dropping a folder next to real plugins.
/// </para>
/// <para>
/// <b>Every descriptor passes the privilege fence.</b> A plugin comes from a marketplace, so its
/// descriptor is untrusted input. <see cref="PluginAgentDescriptorFence"/> rejects a descriptor
/// that declares isolation escalation, hooks, MCP servers, sub-agent grants, session/conversation
/// access or a shell command, and narrows a declared file-access policy to the installing user's
/// own ceiling. A rejected descriptor is skipped with an error naming the field - never loaded at
/// reduced privilege, because "load it anyway but ignore the dangerous bit" is indistinguishable at
/// the call site from "the plugin author's intent was honoured".
/// </para>
/// <para>
/// <b>No watcher.</b> <see cref="Watch"/> returns <c>null</c>, which the hosted service already
/// handles as "this source does not support change notification". Plugin content changes only
/// through install, update or remove - all of which are explicit operations - so a filesystem
/// watcher over the plugin root would add a second, racier notification path for events the
/// lifecycle manager already knows about. Wiring those operations to a reload is a later slice.
/// </para>
/// </remarks>
public sealed class PluginAgentConfigurationSource : IAgentConfigurationSource
{
    /// <summary>
    /// Directory name, relative to a plugin's own directory, holding that plugin's agent
    /// definitions. Matches the by-convention layout of the plugin manifest contract.
    /// </summary>
    public const string AgentsDirectoryName = "agents";

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string? _pluginRoot;
    private readonly IFileSystem _fileSystem;
    private readonly Func<FileAccessPolicy?> _ceilingAccessor;
    private readonly ILogger<PluginAgentConfigurationSource> _logger;

    /// <summary>
    /// Creates a source over the plugins installed under <paramref name="pluginRoot"/>.
    /// </summary>
    /// <param name="pluginRoot">
    /// Directory holding installed plugins. Null, blank or absent yields no descriptors, which is
    /// the correct reading of "this machine has no plugins" and keeps agent registration
    /// byte-identical to its pre-plugin behaviour.
    /// </param>
    /// <param name="ceilingAccessor">
    /// Supplies the installing user's file-access ceiling at load time. A delegate rather than a
    /// value so the ceiling is re-read on every load and a plugin agent cannot outlive a
    /// tightening of the user's own policy.
    /// </param>
    /// <param name="logger">Diagnostics sink; rejections are logged at error level.</param>
    /// <param name="fileSystem">Filesystem abstraction; defaults to the real filesystem.</param>
    public PluginAgentConfigurationSource(
        string? pluginRoot,
        Func<FileAccessPolicy?>? ceilingAccessor = null,
        ILogger<PluginAgentConfigurationSource>? logger = null,
        IFileSystem? fileSystem = null)
    {
        _pluginRoot = pluginRoot;
        _fileSystem = fileSystem ?? new FileSystem();
        _ceilingAccessor = ceilingAccessor ?? (static () => null);
        _logger = logger ?? NullLogger<PluginAgentConfigurationSource>.Instance;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<AgentDescriptor>> LoadAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(Load(cancellationToken));

    /// <inheritdoc />
    /// <remarks>
    /// Always <c>null</c>: plugin content changes only through explicit install/update/remove, so
    /// this source has nothing to watch. The hosted service treats a null watcher as "no change
    /// notification from this source" and reconciles it normally at startup.
    /// </remarks>
    public IDisposable? Watch(Action<IReadOnlyList<AgentDescriptor>> onChanged) => null;

    private IReadOnlyList<AgentDescriptor> Load(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_pluginRoot) || !_fileSystem.Directory.Exists(_pluginRoot))
            return [];

        var store = new PluginStateStore(_pluginRoot, _fileSystem);
        var ceiling = _ceilingAccessor();
        var descriptors = new List<AgentDescriptor>();

        foreach (var plugin in store.Read().OrderBy(static p => p.Name, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var agentsDir = _fileSystem.Path.Combine(_pluginRoot, plugin.Name, AgentsDirectoryName);
            if (!_fileSystem.Directory.Exists(agentsDir))
                continue;

            var files = _fileSystem.Directory
                .GetFiles(agentsDir, "*.json")
                .OrderBy(static f => f, StringComparer.Ordinal);

            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var descriptor = LoadOne(plugin.Name, file, ceiling);
                if (descriptor is not null)
                    descriptors.Add(descriptor);
            }
        }

        return descriptors;
    }

    private AgentDescriptor? LoadOne(string pluginName, string file, FileAccessPolicy? ceiling)
    {
        PluginAgentDefinition? definition;
        try
        {
            definition = JsonSerializer.Deserialize<PluginAgentDefinition>(
                _fileSystem.File.ReadAllText(file),
                SerializerOptions);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            _logger.LogError(
                ex,
                "Skipping unreadable agent definition '{File}' from plugin '{Plugin}'.",
                file,
                pluginName);
            return null;
        }

        if (definition is null || string.IsNullOrWhiteSpace(definition.Id))
        {
            _logger.LogError(
                "Skipping agent definition '{File}' from plugin '{Plugin}': it declares no 'id'.",
                file,
                pluginName);
            return null;
        }

        AgentDescriptor candidate;
        try
        {
            candidate = definition.ToDescriptor(pluginName);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // AgentId.From throws a Vogen-generated validation exception that this assembly cannot
            // name; catching broadly here keeps one bad definition from taking the whole load down.
            _logger.LogError(
                ex,
                "Skipping agent definition '{File}' from plugin '{Plugin}': invalid agent id '{Id}'.",
                file,
                pluginName,
                definition.Id);
            return null;
        }

        var result = PluginAgentDescriptorFence.Apply(candidate, ceiling);
        if (!result.IsAccepted)
        {
            _logger.LogError(
                "Rejecting agent '{AgentId}' from plugin '{Plugin}' ({File}): {Rejections}",
                definition.Id,
                pluginName,
                file,
                string.Join(" | ", result.Rejections));
            return null;
        }

        foreach (var narrowing in result.Narrowings)
        {
            _logger.LogWarning(
                "Narrowed agent '{AgentId}' from plugin '{Plugin}': {Narrowing}",
                definition.Id,
                pluginName,
                narrowing);
        }

        return result.Descriptor;
    }
}
