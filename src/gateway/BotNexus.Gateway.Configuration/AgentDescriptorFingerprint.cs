using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Gateway.Configuration;

/// <summary>
/// Single source of truth for "has this agent's effective configuration changed?".
/// <para>
/// Both the config source (which suppresses no-op <c>IOptionsMonitor</c> callbacks) and the
/// hosted service (which decides whether to re-register an agent in the registry) must agree on
/// exactly which descriptor fields are semantically significant. They previously each maintained
/// their own field list, and the hosted service's copy silently omitted <c>FileAccess</c> - so an
/// edited <c>fileAccess</c> policy propagated from the source but was judged "unchanged" by the
/// service and never re-registered, leaving the agent on a stale path validator for the lifetime
/// of the process (#2383). Keeping the field list here, and only here, makes that class of drift
/// impossible to reintroduce independently.
/// </para>
/// <para>
/// The list is <b>fenced</b>: <c>AgentDescriptorFingerprintFenceArchitectureTests</c> reflects
/// over every settable public property of <see cref="AgentDescriptor"/> and fails the build if
/// one is not referenced by <c>AppendDescriptor</c> (#2588). Adding a descriptor property
/// without appending it here is therefore a build error, not a silent loss of change detection.
/// If a new member genuinely must not participate - a secret or a volatile value; the descriptor
/// graph has neither today - exclude it deliberately in that fence, with a written reason.
/// </summary>
internal static class AgentDescriptorFingerprint
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// Computes a stable, order-independent fingerprint (SHA-256, hex) of the effective agent
    /// descriptors. Two descriptor sets that are semantically equal produce the same fingerprint
    /// even though config loading mints fresh record instances on every call, so unchanged
    /// reload callbacks can be suppressed before a registry apply.
    /// </summary>
    public static string ComputeEffective(IReadOnlyList<AgentDescriptor> descriptors)
    {
        var builder = new StringBuilder();
        foreach (var descriptor in descriptors.OrderBy(d => d.AgentId.Value, StringComparer.Ordinal))
            AppendDescriptor(builder, descriptor);

        return Hash(builder.ToString());
    }

    /// <summary>
    /// Computes the fingerprint of a single descriptor. Use this - never a hand-written field
    /// comparison - when deciding whether a reloaded descriptor differs from the applied one.
    /// </summary>
    public static string ComputeSingle(AgentDescriptor descriptor)
    {
        var builder = new StringBuilder();
        AppendDescriptor(builder, descriptor);
        return Hash(builder.ToString());
    }

    /// <summary>
    /// Semantic equality for two descriptors, defined as fingerprint equality so that every
    /// field considered significant by the config source is also considered significant here.
    /// </summary>
    public static bool AreEquivalent(AgentDescriptor a, AgentDescriptor b)
        => string.Equals(ComputeSingle(a), ComputeSingle(b), StringComparison.Ordinal);

    private static string Hash(string content)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));

    private static void AppendDescriptor(StringBuilder builder, AgentDescriptor d)
    {
        builder.Append(d.AgentId.Value).Append('\u001f');
        builder.Append(d.DisplayName).Append('\u001f');
        builder.Append(d.Kind).Append('\u001f');
        builder.Append(d.Emoji).Append('\u001f');
        builder.Append(d.Description).Append('\u001f');
        // #3596: the agent-owned summary participates in the fingerprint like every other persisted
        // field. A member the fingerprint ignores is judged 'unchanged' on config hot-reload and
        // silently never applies - the #2383 fileAccess defect.
        builder.Append(d.Summary).Append('\u001f');
        builder.Append(d.Order).Append('\u001f');
        builder.Append(d.ModelId).Append('\u001f');
        builder.Append(d.ApiProvider).Append('\u001f');
        builder.Append(d.SystemPrompt).Append('\u001f');
        builder.Append(d.SystemPromptFile).Append('\u001f');
        builder.Append(d.IsolationStrategy).Append('\u001f');
        builder.Append(d.CacheRetentionMode).Append('\u001f');
        builder.Append(d.Thinking).Append('\u001f');
        builder.Append(d.ContextWindow).Append('\u001f');
        builder.Append(d.MaxConcurrentSessions).Append('\u001f');
        builder.Append(d.SessionAccessLevel).Append('\u001f');
        builder.Append(d.ConversationAccessLevel).Append('\u001f');
        AppendList(builder, d.ToolIds);
        AppendList(builder, d.AllowedModelIds);
        AppendList(builder, d.SubAgentIds);
        AppendList(builder, d.SubAgentRoles);
        AppendList(builder, d.SystemPromptFiles);
        AppendList(builder, d.SessionAllowedAgents);
        AppendList(builder, d.ConversationAllowedAgents);
        AppendList(builder, d.ShellCommand);
        // Metadata, isolation options and extension config are serialized deterministically so
        // that inline config edits (e.g. metadata, extensions, memory) are also reflected.
        builder.Append(SerializeStable(d.Metadata)).Append('\u001f');
        builder.Append(SerializeStable(d.IsolationOptions)).Append('\u001f');
        builder.Append(SerializeExtensions(d.ExtensionConfig)).Append('\u001f');
        builder.Append(SerializeStable(d.Memory)).Append('\u001f');
        builder.Append(SerializeStable(d.Soul)).Append('\u001f');
        builder.Append(SerializeStable(d.Heartbeat)).Append('\u001f');
        builder.Append(SerializeStable(d.DateTimeInjection)).Append('\u001f');
        builder.Append(SerializeStable(d.ConversationRetention)).Append('\u001f');
        builder.Append(SerializeStable(d.FileAccess)).Append('\u001e');
    }

    private static void AppendList(StringBuilder builder, IReadOnlyList<string>? values)
    {
        if (values is not null)
        {
            foreach (var value in values)
                builder.Append(value).Append('\u001d');
        }
        builder.Append('\u001f');
    }

    private static string SerializeStable(object? value)
        => value is null ? string.Empty : JsonSerializer.Serialize(value, s_jsonOptions);

    private static string SerializeExtensions(IReadOnlyDictionary<string, JsonElement> extensions)
    {
        if (extensions is null || extensions.Count == 0)
            return string.Empty;

        var builder = new StringBuilder();
        foreach (var kvp in extensions.OrderBy(e => e.Key, StringComparer.Ordinal))
            builder.Append(kvp.Key).Append('=').Append(kvp.Value.GetRawText()).Append(';');
        return builder.ToString();
    }
}
