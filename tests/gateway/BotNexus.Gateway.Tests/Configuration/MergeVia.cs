using System.Text.Json;
using System.Text.Json.Nodes;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Configuration;

namespace BotNexus.Gateway.Tests.Configuration;

/// <summary>
/// Runs the historical <c>AgentConfigMerger</c> test corpus against the shared inheritance engine
/// (#3485 D2).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a shim rather than deleting the tests.</b> <c>AgentConfigMergerTests</c> is 723 lines of
/// behavioural coverage accumulated across #2137, #2423 and #2429 - the accreted answer to "what
/// does agent inheritance actually do". Deleting it alongside the implementation it was written
/// against would discard the only executable record of that behaviour, and the replacement's own
/// tests would then be checked against nothing but my reading of the old code.
/// </para>
/// <para>
/// Retargeting instead means every one of those cases now asserts the ENGINE's behaviour, so the
/// suite doubles as the parity gate. Each test body changed by exactly one identifier.
/// </para>
/// <para>
/// One case diverges deliberately and is documented at its call site: the old merger discarded
/// <c>memory.search.maxTopK</c> and <c>maxLimit</c> (#3497). The engine preserves them.
/// </para>
/// </remarks>
internal static class MergeVia
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Engine equivalent of <c>AgentConfigMerger.Merge(defaults, agent, agentRawElement)</c>.
    /// </summary>
    /// <remarks>
    /// When a raw element is supplied it is authoritative, because it is the only form carrying the
    /// absent-versus-explicit-null distinction. When it is null the bound object is serialised,
    /// which matches what the old merger did in that case - it had no presence information either.
    /// </remarks>
    public static AgentDefinitionConfig Engine(
        AgentDefaultsConfig? defaults,
        AgentDefinitionConfig agent,
        JsonElement? agentRawElement = null)
    {
        ArgumentNullException.ThrowIfNull(agent);

        // Preserve the old contract: no defaults means the agent config passes through untouched.
        if (defaults is null)
            return agent;

        var agentDocument = agentRawElement is { ValueKind: JsonValueKind.Object } element
            ? JsonNode.Parse(element.GetRawText())?.AsObject()
            : AgentConfigInheritance.ToDocument(agent);

        return AgentConfigInheritance.Overlay(
            AgentConfigInheritance.ToDocument(defaults),
            agentDocument).Effective;
    }

    /// <summary>Engine equivalent of the <c>MergeMemory</c> helper.</summary>
    public static MemoryAgentConfig? Memory(
        MemoryAgentConfig? defaults,
        MemoryAgentConfig? agent,
        JsonElement? agentObj)
    {
        var defaultsDoc = defaults is null ? null : Wrap("memory", defaults);
        var agentDoc = agentObj is { ValueKind: JsonValueKind.Object } el
            ? JsonNode.Parse(el.GetRawText())?.AsObject()
            : agent is null ? null : Wrap("memory", agent);

        return AgentConfigInheritance.Overlay(defaultsDoc, agentDoc).Effective.Memory;
    }

    /// <summary>Engine equivalent of the <c>MergeHeartbeat</c> helper.</summary>
    public static HeartbeatAgentConfig? Heartbeat(
        HeartbeatAgentConfig? defaults,
        HeartbeatAgentConfig? agent,
        JsonElement? agentObj)
    {
        var defaultsDoc = defaults is null ? null : Wrap("heartbeat", defaults);
        var agentDoc = agentObj is { ValueKind: JsonValueKind.Object } el
            ? JsonNode.Parse(el.GetRawText())?.AsObject()
            : agent is null ? null : Wrap("heartbeat", agent);

        return AgentConfigInheritance.Overlay(defaultsDoc, agentDoc).Effective.Heartbeat;
    }

    /// <summary>
    /// Wraps a nested config object in a single-key document, so the engine sees it at the same path
    /// it would occupy in a real agent block and therefore resolves the same declared policy.
    /// </summary>
    private static JsonObject Wrap<T>(string key, T value)
        => new() { [key] = JsonSerializer.SerializeToNode(value, Options) };
}
