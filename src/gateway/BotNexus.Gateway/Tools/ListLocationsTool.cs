using System.Text.Json;
using System.Text.Json.Serialization;
using BotNexus.Agent.Core.Tools;
using BotNexus.Domain.Primitives;
using BotNexus.Agent.Core.Types;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Gateway.Configuration;
using Microsoft.Extensions.Options;

namespace BotNexus.Gateway.Tools;

/// <summary>
/// Lets an agent discover the servers and resources this installation knows about, without giving
/// it any means of authenticating to them.
/// </summary>
/// <remarks>
/// <para>
/// This is the discovery half of "capability without custody". An agent that can read a credential
/// can be talked into disclosing it - injected text and legitimate instructions arrive through the
/// same channel - so the agent learns that <c>proxmox-main</c> exists and where it is, while the
/// credential is resolved inside whichever tool actually performs the work and never enters the
/// conversation.
/// </para>
/// <para>
/// <b>The projection is deliberate and deliberately narrow.</b> <see cref="LocationEntry"/> is a
/// separate type from <see cref="LocationConfig"/> rather than a serialisation of it, so a field
/// added to configuration cannot start flowing to agents merely because someone added it. Two
/// fields are excluded on purpose:
/// </para>
/// <list type="bullet">
/// <item><description><c>credentialRef</c> - names where a credential lives. Harmless in a config
/// file an operator reads, but there is no reason an agent needs it, and "the token is in
/// PROXMOX_TOKEN" is a useful hint to anyone who has talked their way into the context.</description></item>
/// <item><description><c>connectionString</c> - <b>is</b> a credential. Note that the locations
/// REST API derives its display value as <c>Path ?? Endpoint ?? ConnectionString</c> and redacts
/// afterwards; reusing that helper here would have handed an agent the connection string of every
/// database location. This projection reads <c>Path</c> and <c>Endpoint</c> by name and never
/// consults <c>ConnectionString</c> at all.</description></item>
/// </list>
/// <para>
/// Only configured locations are listed - the ones an operator put in <c>gateway.locations</c>.
/// The world descriptor also carries derived entries for agent workspaces and internal directories,
/// which are an implementation detail rather than something to go looking at.
/// </para>
/// <para>
/// <b>Scoped per agent</b> since #3232, via <see cref="LocationAccessPolicy"/>. A location whose
/// <c>agents</c> list does not admit the caller is filtered out before projection, so it does not
/// appear and nothing in the result reveals that it was withheld. An absent list means every
/// agent, which is the default and why this changed nothing on upgrade.
/// </para>
/// </remarks>
public sealed class ListLocationsTool(
    IOptionsMonitor<PlatformConfig> platformConfig,
    AgentId? agentId = null) : IAgentTool
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    /// <inheritdoc />
    public string Name => "list_locations";

    /// <inheritdoc />
    public string Label => "List Locations";

    /// <summary>
    /// Operator-authored configuration read from this gateway's own config file, so it carries no
    /// more taint than the agent's own definition does.
    /// </summary>
    public string ContentSource => ToolContentSource.Local;

    /// <inheritdoc />
    public Tool Definition => new(
        Name,
        "List the servers and resources this BotNexus installation knows about: name, kind, address and description. "
        + "Use it to find the name of a target before asking another tool to act on it. "
        + "Credentials are never returned - a tool that acts on a location resolves its own.",
        JsonDocument.Parse("""
            {
              "type": "object",
              "properties": {
                "filter": {
                  "type": "string",
                  "description": "Optional free-text filter applied to name, description and tags (case-insensitive)."
                },
                "type": {
                  "type": "string",
                  "description": "Optional exact kind filter: filesystem, api, mcp-server, database or remote-node."
                }
              },
              "required": []
            }
            """).RootElement.Clone());

    /// <inheritdoc />
    public Task<IReadOnlyDictionary<string, object?>> PrepareArgumentsAsync(
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(arguments);
    }

    /// <inheritdoc />
    public Task<AgentToolResult> ExecuteAsync(
        string toolCallId,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default,
        AgentToolUpdateCallback? onUpdate = null)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var filter = ReadString(arguments, "filter");
        var type = ReadString(arguments, "type");

        // CurrentValue rather than a captured snapshot: configuration reloads live, and an agent
        // asking what exists should be told what exists now.
        var configured = platformConfig.CurrentValue.Gateway?.Locations;

        var entries = (configured ?? [])
            .Where(pair => pair.Value is not null)
            // #3232: scope BEFORE projecting. A location this agent may not see must not become a
            // LocationEntry at all, so no later filter can accidentally let one through and no
            // count of the result reveals that something was withheld.
            .Where(pair => LocationAccessPolicy.IsVisibleTo(pair.Value, agentId))
            .Select(pair => Project(pair.Key, pair.Value))
            .Where(entry => MatchesType(entry, type))
            .Where(entry => MatchesFilter(entry, filter))
            .OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var json = JsonSerializer.Serialize(entries, JsonOptions);
        return Task.FromResult(new AgentToolResult(
            [new AgentToolContent(AgentToolContentType.Text, json)]));
    }

    /// <summary>
    /// Builds the agent-visible view of one location. Every field is named explicitly - there is no
    /// reflection or object spread here, because the safety property is that adding a field to
    /// <see cref="LocationConfig"/> does nothing until someone edits this method.
    /// </summary>
    private static LocationEntry Project(string name, LocationConfig config)
    {
        var type = string.IsNullOrWhiteSpace(config.Type) ? "filesystem" : config.Type.Trim();

        return new LocationEntry(
            Name: name,
            Type: type,
            // Path for filesystem locations, Endpoint for the rest. ConnectionString is never
            // consulted: for a database location it IS the credential.
            Address: type.Equals("filesystem", StringComparison.OrdinalIgnoreCase)
                ? config.Path
                : config.Endpoint,
            Username: config.Username,
            Description: config.Description,
            Tags: config.Tags is { Count: > 0 } tags ? tags : null,
            // Whether a credential is configured, never which one or where. Enough for an agent to
            // know a target is authenticated and to say so if a later call fails.
            HasCredential: !string.IsNullOrWhiteSpace(config.CredentialRef)
                           || !string.IsNullOrWhiteSpace(config.ConnectionString),
            VerifyTls: config.VerifyTls);
    }

    private static bool MatchesType(LocationEntry entry, string? type)
        => string.IsNullOrWhiteSpace(type)
           || entry.Type.Equals(type.Trim(), StringComparison.OrdinalIgnoreCase);

    private static bool MatchesFilter(LocationEntry entry, string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter))
            return true;

        return entry.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || (entry.Description?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false)
            || (entry.Tags?.Any(t => t.Contains(filter, StringComparison.OrdinalIgnoreCase)) ?? false);
    }

    private static string? ReadString(IReadOnlyDictionary<string, object?> arguments, string key)
        => arguments.TryGetValue(key, out var value) && value is not null
            ? value as string ?? value.ToString()
            : null;
}

/// <summary>
/// The agent-visible projection of a configured location.
/// </summary>
/// <remarks>
/// Kept separate from <see cref="LocationConfig"/> on purpose: this is the exposure boundary, and a
/// field only crosses it by being added here <i>and</i> populated in
/// <c>ListLocationsTool.Project</c>. <c>LocationsToolExposureFenceArchitectureTests</c> asserts that
/// no credential-bearing member appears on this type.
/// </remarks>
/// <param name="Name">The name the location is referred to by.</param>
/// <param name="Type">filesystem, api, mcp-server, database or remote-node.</param>
/// <param name="Address">Filesystem path or endpoint URL. Never a connection string.</param>
/// <param name="Username">Account used to authenticate, when one is configured. An identity, not a credential.</param>
/// <param name="Description">Operator's description.</param>
/// <param name="Tags">Operator's labels.</param>
/// <param name="HasCredential">Whether a credential is configured - never which, nor where.</param>
/// <param name="VerifyTls">Whether TLS verification is on for this target.</param>
public sealed record LocationEntry(
    string Name,
    string Type,
    string? Address,
    string? Username,
    string? Description,
    IReadOnlyList<string>? Tags,
    bool HasCredential,
    bool VerifyTls);
