using System.Text.Json.Serialization;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Security;

namespace BotNexus.Extensions.Plugins.Agents;

/// <summary>
/// Typed projection of one <c>agents/&lt;name&gt;.json</c> document shipped by a plugin (#2685).
/// </summary>
/// <remarks>
/// <para>
/// <b>This type is a second, independent fence - deliberately so.</b> It declares only the members
/// <see cref="PluginAgentDescriptorFence.DeclarableMembers"/> permits, so a plugin cannot even
/// express an isolation strategy or a shell command in JSON: the field has nowhere to bind and is
/// discarded at parse time. That makes the common case cheap and the error message early.
/// </para>
/// <para>
/// It is <b>not</b> a substitute for the runtime fence, and the runtime fence is not redundant
/// given it. This type is a hand-maintained list of field names, and a hand-maintained list is
/// exactly what #2685 clause 4 refuses to rely on - adding a property here is one line and no
/// alarm. <see cref="PluginAgentDescriptorFence"/> is the structural authority: it reflects over
/// the live descriptor member set, so widening THIS type without widening the fence's reviewed
/// declarable set fails the architecture test. Two layers, one of which cannot silently drift.
/// </para>
/// <para>
/// <see cref="FileAccess"/> is present because clause 3 narrows rather than rejects it - a plugin
/// must be able to ASK for a path grant so the fence has something to clamp.
/// </para>
/// </remarks>
public sealed record PluginAgentDefinition
{
    /// <summary>Agent identifier, unique within the world.</summary>
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    /// <summary>Human-readable display name; falls back to <see cref="Id"/> when omitted.</summary>
    [JsonPropertyName("displayName")]
    public string? DisplayName { get; init; }

    /// <summary>Emoji identifying the agent in user interfaces, or <c>null</c>.</summary>
    [JsonPropertyName("emoji")]
    public string? Emoji { get; init; }

    /// <summary>Description of the agent's purpose, or <c>null</c>.</summary>
    [JsonPropertyName("description")]
    public string? Description { get; init; }

    /// <summary>Model identifier this agent uses by default.</summary>
    [JsonPropertyName("model")]
    public string? Model { get; init; }

    /// <summary>Provider instance key the model is registered under.</summary>
    [JsonPropertyName("provider")]
    public string? Provider { get; init; }

    /// <summary>System prompt text, or <c>null</c>.</summary>
    [JsonPropertyName("systemPrompt")]
    public string? SystemPrompt { get; init; }

    /// <summary>Ordered system-prompt file paths, or <c>null</c>.</summary>
    [JsonPropertyName("systemPromptFiles")]
    public IReadOnlyList<string>? SystemPromptFiles { get; init; }

    /// <summary>
    /// Tool identifiers the agent requests. These name tools the HOST has registered; an id the
    /// host does not know resolves to nothing, so this cannot conjure an uninstalled capability.
    /// </summary>
    [JsonPropertyName("toolIds")]
    public IReadOnlyList<string>? ToolIds { get; init; }

    /// <summary>Model ids the agent may use, or <c>null</c> for the provider allowlist.</summary>
    [JsonPropertyName("allowedModels")]
    public IReadOnlyList<string>? AllowedModels { get; init; }

    /// <summary>Default reasoning effort, or <c>null</c> for the model default.</summary>
    [JsonPropertyName("thinking")]
    public string? Thinking { get; init; }

    /// <summary>Default context-window size in tokens, or <c>null</c> for the model default.</summary>
    [JsonPropertyName("contextWindow")]
    public int? ContextWindow { get; init; }

    /// <summary>Maximum concurrent sessions; zero means unlimited.</summary>
    [JsonPropertyName("maxConcurrentSessions")]
    public int? MaxConcurrentSessions { get; init; }

    /// <summary>
    /// Requested file-access policy. Narrowed to the installing user's own ceiling by
    /// <see cref="PluginAgentDescriptorFence"/> rather than honoured as written (clause 3).
    /// </summary>
    [JsonPropertyName("fileAccess")]
    public PluginFileAccess? FileAccess { get; init; }

    /// <summary>
    /// Projects this definition onto an <see cref="AgentDescriptor"/>. Every fenced member is left
    /// at its descriptor default, so the result is a candidate the fence can evaluate rather than
    /// one that has already been trusted.
    /// </summary>
    /// <param name="pluginName">Installing plugin, recorded in metadata for provenance.</param>
    public AgentDescriptor ToDescriptor(string pluginName) => new()
    {
        AgentId = AgentId.From(Id),
        DisplayName = string.IsNullOrWhiteSpace(DisplayName) ? Id : DisplayName,
        Emoji = Emoji,
        Description = Description,
        ModelId = Model ?? string.Empty,
        ApiProvider = Provider ?? string.Empty,
        SystemPrompt = SystemPrompt,
        SystemPromptFiles = SystemPromptFiles?.ToArray() ?? [],
        ToolIds = ToolIds?.ToArray() ?? [],
        AllowedModelIds = AllowedModels?.ToArray() ?? [],
        Thinking = Thinking,
        ContextWindow = ContextWindow,
        MaxConcurrentSessions = MaxConcurrentSessions ?? 0,
        // Provenance, not capability: it lets the portal and diagnostics say which plugin an agent
        // came from without the descriptor growing a plugin-shaped member.
        Metadata = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["plugin"] = pluginName
        },
        FileAccess = FileAccess?.ToPolicy()
    };
}

/// <summary>
/// Plugin-declared file-access request. Mirrors <see cref="FileAccessPolicy"/> so a plugin can ask
/// for path grants; what it actually receives is decided by the fence, not by this document.
/// </summary>
public sealed record PluginFileAccess
{
    /// <summary>Read paths requested.</summary>
    [JsonPropertyName("allowedReadPaths")]
    public IReadOnlyList<string>? AllowedReadPaths { get; init; }

    /// <summary>Write paths requested.</summary>
    [JsonPropertyName("allowedWritePaths")]
    public IReadOnlyList<string>? AllowedWritePaths { get; init; }

    /// <summary>Paths the plugin asks to be denied. Unioned with the ceiling's denials.</summary>
    [JsonPropertyName("deniedPaths")]
    public IReadOnlyList<string>? DeniedPaths { get; init; }

    /// <summary>Projects the request onto the domain policy shape.</summary>
    public FileAccessPolicy ToPolicy() => new()
    {
        AllowedReadPaths = AllowedReadPaths?.ToArray() ?? [],
        AllowedWritePaths = AllowedWritePaths?.ToArray() ?? [],
        DeniedPaths = DeniedPaths?.ToArray() ?? []
    };
}
