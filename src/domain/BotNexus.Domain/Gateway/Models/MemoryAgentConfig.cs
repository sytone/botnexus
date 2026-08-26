using System.ComponentModel.DataAnnotations;
using BotNexus.Gateway.Abstractions.Models;
namespace BotNexus.Gateway.Abstractions.Models;

/// <summary>
/// Represents memory agent config.
/// </summary>
public sealed class MemoryAgentConfig
{
    /// <summary>
    /// Gets or sets the enabled.
    /// </summary>
    [Display(
        Name = "Enabled",
        Description = "Whether persistent memory is enabled for this agent. When false, no memories are written or retrieved.",
        GroupName = "Memory",
        Order = 0)]
    [ConfigField(Widget = ConfigFieldWidget.Toggle, Group = "memory", Order = 0)]
    public bool Enabled { get; set; }

    /// <summary>
    /// Gets or sets the optional memory root or file override path.
    /// </summary>
    [Display(
        Name = "Path",
        Description = "Gets or sets the optional memory root or file override path.",
        GroupName = "Memory",
        Order = 1)]
    [ConfigField(Widget = ConfigFieldWidget.Text, Group = "memory", Order = 1)]
    public string? Path { get; set; }

    /// <summary>
    /// Gets or sets the indexing.
    /// </summary>
    [Display(
        Name = "Indexing",
        Description = "Settings controlling how new memories are embedded and indexed for later retrieval.",
        GroupName = "Memory",
        Order = 2)]
    [ConfigField(Widget = ConfigFieldWidget.Text, Group = "memory", Order = 2)]
    public string Indexing { get; set; } = "auto";

    /// <summary>
    /// Gets or sets the search.
    /// </summary>
    [Display(
        Name = "Search",
        Description = "Settings controlling how stored memories are searched and ranked at retrieval time.",
        GroupName = "Memory",
        Order = 3)]
    [ConfigField(Widget = ConfigFieldWidget.Select, Group = "memory", Order = 3)]
    public MemorySearchAgentConfig? Search { get; set; }

    /// <summary>
    /// Gets or sets prompt-memory injection mode: <c>full</c>, <c>summary</c>, or <c>none</c>.
    /// </summary>
    [Display(
        Name = "Prompt injection",
        Description = "Gets or sets prompt-memory injection mode: full, summary, or none.",
        GroupName = "Memory",
        Order = 4)]
    [ConfigField(Widget = ConfigFieldWidget.Text, Group = "memory", Order = 4)]
    public string? PromptInjection { get; set; } = "full";
}

/// <summary>
/// Represents memory search agent config.
/// </summary>
public sealed class MemorySearchAgentConfig
{
    /// <summary>
    /// Gets or sets the default top k.
    /// </summary>
    [Display(
        Name = "Default top k",
        Description = "Default number of memory results returned when a search does not request an explicit count.",
        GroupName = "Memory search",
        Order = 0)]
    [ConfigField(Widget = ConfigFieldWidget.Number, Group = "memory-search", Order = 0)]
    public int DefaultTopK { get; set; } = 10;

    /// <summary>
    /// Gets or sets the maximum number of results an agent-supplied <c>topK</c> can request.
    /// Caller-provided values above this ceiling are clamped so a single search cannot fan out
    /// over the entire store; protects against runaway embedding fetches and oversized tool results.
    /// </summary>
    [Display(
        Name = "Max top k",
        Description = "Gets or sets the maximum number of results an agent-supplied topK can request. Caller-provided values above this ceiling are clamped so a single search cannot fan out over the entire store; protects against runaway embedding fetches and oversized tool results.",
        GroupName = "Memory search",
        Order = 1)]
    [ConfigField(Widget = ConfigFieldWidget.Number, Group = "memory-search", Order = 1)]
    public int MaxTopK { get; set; } = 100;

    /// <summary>
    /// Gets or sets the maximum number of entries a <c>memory_get</c> session listing can return.
    /// Caller-provided <c>limit</c> values above this ceiling are clamped to bound the fetch and
    /// the resulting serialized payload.
    /// </summary>
    [Display(
        Name = "Max limit",
        Description = "Gets or sets the maximum number of entries a memory_get session listing can return. Caller-provided limit values above this ceiling are clamped to bound the fetch and the resulting serialized payload.",
        GroupName = "Memory search",
        Order = 2)]
    [ConfigField(Widget = ConfigFieldWidget.Number, Group = "memory-search", Order = 2)]
    public int MaxLimit { get; set; } = 100;

    /// <summary>
    /// Gets or sets the temporal decay.
    /// </summary>
    [Display(
        Name = "Temporal decay",
        Description = "Settings that down-rank older memories so recent context scores higher.",
        GroupName = "Memory search",
        Order = 3)]
    [ConfigField(Widget = ConfigFieldWidget.Select, Group = "memory-search", Order = 3)]
    public TemporalDecayAgentConfig? TemporalDecay { get; set; }
}

/// <summary>
/// Represents temporal decay agent config.
/// </summary>
public sealed class TemporalDecayAgentConfig
{
    /// <summary>
    /// Gets or sets the enabled.
    /// </summary>
    [Display(
        Name = "Enabled",
        Description = "Whether temporal decay is applied to memory search scores. When false, age does not affect ranking.",
        GroupName = "Temporal decay",
        Order = 0)]
    [ConfigField(Widget = ConfigFieldWidget.Toggle, Group = "temporal-decay", Order = 0)]
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Gets or sets the half life days.
    /// </summary>
    [Display(
        Name = "Half life days",
        Description = "Age in days at which a memory's relevance score is halved. Lower values forget faster.",
        GroupName = "Temporal decay",
        Order = 1)]
    [ConfigField(Widget = ConfigFieldWidget.Number, Group = "temporal-decay", Order = 1)]
    public int HalfLifeDays { get; set; } = 30;
}
