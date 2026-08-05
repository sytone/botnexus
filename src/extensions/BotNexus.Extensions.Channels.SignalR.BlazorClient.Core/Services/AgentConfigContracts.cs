using System.Text.Json;
using System.Text.Json.Serialization;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;

/// <summary>
/// Wire shapes the Agent Configuration panel deserializes, plus the single projection
/// (<see cref="AgentConfigSnapshot"/>) that both the rendered markup and the copy control read.
/// </summary>
/// <remarks>
/// <para>
/// <b>#2795 — why these DTOs still exist rather than referencing the shared contract types.</b>
/// Acceptance criterion 7 prefers replacing these DTOs with the types the endpoints actually
/// serialize. That is <b>not reachable</b> here, for two independent reasons:
/// </para>
/// <list type="number">
/// <item>
/// <c>GET /api/agents/{agentId}</c> serializes <c>BotNexus.Gateway.Abstractions.Models.AgentDescriptor</c>,
/// which lives in <c>BotNexus.Domain</c>. This assembly is compiled into a
/// <c>Microsoft.NET.Sdk.BlazorWebAssembly</c> app, so every reachable assembly is downloaded by the
/// browser, and <c>WasmPayloadDependencyArchitectureTests</c> structurally forbids a
/// <c>BotNexus.Domain</c> reference from here (it drags <c>Vogen.SharedTypes</c> into the payload as a
/// runtime asset — see the long-form rationale in BlazorClient.Core.csproj). Referencing
/// <c>AgentDescriptor</c> would redden that fence, and relaxing the fence is not an option.
/// </item>
/// <item>
/// <c>GET /api/agents/{agentId}/sessions/{sessionId}/context</c> returns an <b>anonymous type</b>
/// (<c>AgentsController.GetContext</c>). There is no shared contract type to reference at all.
/// </item>
/// </list>
/// <para>
/// So the AC7 <i>fallback</i> is implemented instead: <c>AgentConfigContractTests</c> serializes the
/// REAL <c>AgentDescriptor</c> and the REAL controller response object and asserts that every
/// property name these DTOs declare is present in that payload. A future server-side rename
/// therefore reddens a named test instead of silently blanking the panel — which is exactly the
/// failure mode #2795 reported.
/// </para>
/// <para>
/// <b>Do not "simplify" these to flat properties.</b> The original defect was a hand-written flat
/// DTO (<c>Model</c>, <c>Provider</c>, <c>int ToolCount</c>, <c>int? SystemPromptTokens</c>) whose
/// names existed nowhere in either payload. System.Text.Json does not error on an unmatched name;
/// it leaves the default. The nesting below mirrors the wire shape deliberately.
/// </para>
/// </remarks>
public sealed record AgentDescriptorDto
{
    /// <summary>Maps <c>AgentDescriptor.AgentId</c>.</summary>
    public string? AgentId { get; init; }

    /// <summary>Maps <c>AgentDescriptor.DisplayName</c>.</summary>
    public string? DisplayName { get; init; }

    /// <summary>Maps <c>AgentDescriptor.ModelId</c> — NOT <c>Model</c>, which is what #2795 got wrong.</summary>
    public string? ModelId { get; init; }

    /// <summary>Maps <c>AgentDescriptor.ApiProvider</c> — NOT <c>Provider</c>.</summary>
    public string? ApiProvider { get; init; }

    /// <summary>
    /// Maps <c>AgentDescriptor.ToolIds</c>. Nullable on purpose: <see langword="null"/> means the
    /// payload did not carry the field at all, which must render as "unavailable", never as a
    /// confident <c>0</c>. #2795 reported <c>TOOL COUNT 0</c> being read as a real measurement.
    /// </summary>
    public IReadOnlyList<string>? ToolIds { get; init; }

    /// <summary>Maps <c>AgentDescriptor.Memory</c> (nested object; null means memory disabled).</summary>
    public AgentMemoryConfigDto? Memory { get; init; }

    /// <summary>Maps <c>AgentDescriptor.Heartbeat</c> (nested object; null means heartbeat disabled).</summary>
    public AgentHeartbeatConfigDto? Heartbeat { get; init; }

    /// <summary>Maps <c>AgentDescriptor.FileAccess</c> (nested object; null means workspace-only).</summary>
    public AgentFileAccessDto? FileAccess { get; init; }
}

/// <summary>Nested <c>AgentDescriptor.Memory</c> shape (<c>MemoryAgentConfig</c>).</summary>
public sealed record AgentMemoryConfigDto
{
    /// <summary>Maps <c>MemoryAgentConfig.Enabled</c>.</summary>
    public bool Enabled { get; init; }
}

/// <summary>Nested <c>AgentDescriptor.Heartbeat</c> shape (<c>HeartbeatAgentConfig</c>).</summary>
public sealed record AgentHeartbeatConfigDto
{
    /// <summary>Maps <c>HeartbeatAgentConfig.Enabled</c>.</summary>
    public bool Enabled { get; init; }

    /// <summary>Maps <c>HeartbeatAgentConfig.IntervalMinutes</c>.</summary>
    public int IntervalMinutes { get; init; }
}

/// <summary>Nested <c>AgentDescriptor.FileAccess</c> shape (<c>FileAccessPolicy</c>).</summary>
public sealed record AgentFileAccessDto
{
    /// <summary>Maps <c>FileAccessPolicy.AllowedReadPaths</c>.</summary>
    public IReadOnlyList<string>? AllowedReadPaths { get; init; }

    /// <summary>Maps <c>FileAccessPolicy.AllowedWritePaths</c>.</summary>
    public IReadOnlyList<string>? AllowedWritePaths { get; init; }
}

/// <summary>
/// Response of <c>GET /api/agents/{agentId}/sessions/{sessionId}/context</c>. The token counts are
/// <b>nested</b> under <c>sections.systemPrompt.tokens</c>; #2795 defect 2 was a flat
/// <c>SystemPromptTokens</c> that therefore never bound.
/// </summary>
public sealed record ContextInfoDto
{
    /// <summary>Maps the anonymous <c>sections</c> member.</summary>
    public ContextSectionsDto? Sections { get; init; }

    /// <summary>Maps the anonymous <c>totalEstimatedTokens</c> member.</summary>
    public int? TotalEstimatedTokens { get; init; }
}

/// <summary>The nested <c>sections</c> object of the context response.</summary>
public sealed record ContextSectionsDto
{
    /// <summary>Maps <c>sections.systemPrompt</c>.</summary>
    public ContextSectionDto? SystemPrompt { get; init; }

    /// <summary>Maps <c>sections.toolDefinitions</c>.</summary>
    public ContextSectionDto? ToolDefinitions { get; init; }

    /// <summary>Maps <c>sections.conversationHistory</c>.</summary>
    public ContextSectionDto? ConversationHistory { get; init; }
}

/// <summary>One entry of the context response's <c>sections</c> object.</summary>
public sealed record ContextSectionDto
{
    /// <summary>Maps <c>sections.*.tokens</c>.</summary>
    public int? Tokens { get; init; }
}

/// <summary>
/// One row of the Agent Configuration panel. The panel renders these and the Copy control
/// serializes these — there is exactly one list, so the copied payload cannot drift from the
/// rendered payload (#2795 AC6, satisfied by construction rather than by assertion).
/// </summary>
public sealed record AgentConfigField
{
    /// <summary>Stable machine key, used as the JSON property name in the copied payload.</summary>
    public required string Key { get; init; }

    /// <summary>Human label rendered in the panel.</summary>
    public required string Label { get; init; }

    /// <summary>
    /// The resolved value, or <see langword="null"/> when it could not be determined.
    /// <b>Null is meaningful</b>: it renders as <see cref="Placeholder"/> (default
    /// <see cref="Unavailable"/>) so that a future contract mismatch reads as a failure, not as
    /// data. #2795 shipped a mismatch that rendered as an em-dash and a plausible <c>0</c>.
    /// </summary>
    public string? Value { get; init; }

    /// <summary>Text rendered (and copied) when <see cref="Value"/> is null.</summary>
    public string Placeholder { get; init; } = Unavailable;

    /// <summary>Whether this field is inherited from <c>agents.defaults</c> (renders a badge).</summary>
    public bool IsInherited { get; init; }

    /// <summary>The single string both the markup and the copy payload use.</summary>
    public string Display => Value ?? Placeholder;

    /// <summary>Default placeholder. Deliberately NOT an em-dash — see <see cref="Value"/>.</summary>
    public const string Unavailable = "unavailable";
}

/// <summary>
/// The complete panel state: an ordered field list plus the copy payload derived from it.
/// </summary>
public sealed record AgentConfigSnapshot
{
    /// <summary>Ordered fields, rendered top to bottom.</summary>
    public required IReadOnlyList<AgentConfigField> Fields { get; init; }

    /// <summary>Look up a field by <see cref="AgentConfigField.Key"/>.</summary>
    public AgentConfigField? Field(string key) =>
        Fields.FirstOrDefault(f => string.Equals(f.Key, key, StringComparison.Ordinal));

    /// <summary>
    /// The clipboard payload (#2795 AC5/AC6). Built by projecting <see cref="Fields"/> — the same
    /// list the markup renders. There is no second hand-maintained field list to drift.
    /// </summary>
    public string ToClipboardJson()
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var inherited = new List<string>();
        foreach (var field in Fields)
        {
            values[field.Key] = field.Display;
            if (field.IsInherited)
                inherited.Add(field.Key);
        }

        return JsonSerializer.Serialize(
            new ClipboardPayload { Fields = values, InheritedFromWorldDefaults = inherited },
            ClipboardOptions);
    }

    private static readonly JsonSerializerOptions ClipboardOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        // The values are already display strings; do not let the encoder mangle the em-dash or
        // other glyphs a user will paste straight into a GitHub issue.
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private sealed record ClipboardPayload
    {
        [JsonPropertyName("fields")]
        public required IReadOnlyDictionary<string, string> Fields { get; init; }

        [JsonPropertyName("inheritedFromWorldDefaults")]
        public required IReadOnlyList<string> InheritedFromWorldDefaults { get; init; }
    }
}

/// <summary>
/// Builds the single <see cref="AgentConfigSnapshot"/> the Agent Configuration panel renders and
/// copies. Pure and free of Blazor/JS so it is directly unit-testable (#2795).
/// </summary>
public static class AgentConfigSnapshotBuilder
{
    /// <summary>Field key for the conversation ID row added by #2795 gap 3.</summary>
    public const string ConversationIdKey = "conversationId";

    /// <summary>Field key for the model row.</summary>
    public const string ModelKey = "model";

    /// <summary>Placeholder rendered when the agent has no active conversation (#2795 AC4).</summary>
    public const string NoActiveConversation = "no active conversation";

    /// <summary>Placeholder rendered when there is no live session to read context from.</summary>
    public const string NoActiveSession = "no active session";

    /// <summary>
    /// Projects client state and the two endpoint payloads into the ordered field list.
    /// </summary>
    /// <param name="agentId">The agent the panel was opened for.</param>
    /// <param name="displayName">Display name from client state.</param>
    /// <param name="conversationId">Active conversation ID from client state, or null.</param>
    /// <param name="sessionId">Active conversation's session ID from client state, or null.</param>
    /// <param name="channelType">Channel type from client state, or null.</param>
    /// <param name="descriptor">Deserialized <c>GET /api/agents/{id}</c> payload, or null when the fetch failed.</param>
    /// <param name="context">Deserialized context payload, or null when unavailable.</param>
    /// <param name="contextUnavailableReason">Overrides the system-prompt-size placeholder when context could not be read.</param>
    /// <param name="inheritedFields">Field path -&gt; inherited flag, from the effective-config endpoint.</param>
    public static AgentConfigSnapshot Build(
        string agentId,
        string? displayName,
        string? conversationId,
        string? sessionId,
        string? channelType,
        AgentDescriptorDto? descriptor,
        ContextInfoDto? context,
        string? contextUnavailableReason = null,
        IReadOnlyDictionary<string, bool>? inheritedFields = null)
    {
        bool Inherited(string path) =>
            inheritedFields is not null && inheritedFields.TryGetValue(path, out var v) && v;

        var fields = new List<AgentConfigField>
        {
            new() { Key = "agentId", Label = "Agent ID", Value = NullIfBlank(agentId) },
            new() { Key = "displayName", Label = "Display Name", Value = NullIfBlank(displayName) },
            // #2795 gap 3: the conversation ID is the identifier that correlates portal state, the
            // conversations API and the sessions store. It sits directly above the session ID it
            // derives, with the same monospace treatment.
            new()
            {
                Key = ConversationIdKey,
                Label = "Conversation ID",
                Value = NullIfBlank(conversationId),
                Placeholder = NoActiveConversation,
            },
            new()
            {
                Key = "sessionId",
                Label = "Session ID",
                Value = NullIfBlank(sessionId),
                Placeholder = NoActiveSession,
            },
            new() { Key = "channelType", Label = "Channel Type", Value = NullIfBlank(channelType) ?? "signalr" },
            new()
            {
                Key = ModelKey,
                Label = "Model",
                Value = NullIfBlank(descriptor?.ModelId),
                IsInherited = Inherited("model"),
            },
            new()
            {
                Key = "provider",
                Label = "Provider",
                Value = NullIfBlank(descriptor?.ApiProvider),
                IsInherited = Inherited("provider"),
            },
            new()
            {
                Key = "toolCount",
                Label = "Tool Count",
                // Null (field absent) stays null so it renders "unavailable". An agent that genuinely
                // has no tools carries an empty array and correctly renders "0".
                Value = descriptor?.ToolIds?.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                IsInherited = Inherited("toolIds"),
            },
        };

        if (descriptor?.Memory is { } memory)
        {
            fields.Add(new AgentConfigField
            {
                Key = "memory",
                Label = "Memory",
                Value = memory.Enabled ? "Enabled" : "Disabled",
                IsInherited = Inherited("memory.enabled"),
            });
        }

        if (descriptor?.Heartbeat is { } heartbeat)
        {
            fields.Add(new AgentConfigField
            {
                Key = "heartbeat",
                Label = "Heartbeat",
                Value = heartbeat.Enabled ? "Enabled" : "Disabled",
                IsInherited = Inherited("heartbeat.enabled"),
            });
            fields.Add(new AgentConfigField
            {
                Key = "heartbeatIntervalMinutes",
                Label = "Heartbeat Interval",
                Value = $"{heartbeat.IntervalMinutes} min",
                IsInherited = Inherited("heartbeat.intervalMinutes"),
            });
        }

        if (descriptor?.FileAccess is not null)
        {
            fields.Add(new AgentConfigField
            {
                Key = "fileAccess",
                Label = "File Access",
                Value = "Configured",
                IsInherited = Inherited("fileAccess"),
            });
        }

        var tokens = context?.Sections?.SystemPrompt?.Tokens;
        fields.Add(new AgentConfigField
        {
            Key = "systemPromptSize",
            Label = "System Prompt Size",
            Value = tokens is not null
                ? $"{tokens.Value.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)} tokens"
                : null,
            Placeholder = contextUnavailableReason ?? AgentConfigField.Unavailable,
        });

        return new AgentConfigSnapshot { Fields = fields };
    }

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;
}
