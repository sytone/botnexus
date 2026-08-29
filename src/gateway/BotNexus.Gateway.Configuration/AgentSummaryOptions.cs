using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Gateway.Configuration;

/// <summary>
/// Bounds for the agent-maintained <c>summary</c> field written through <c>update_agent</c> (#3596).
/// </summary>
/// <remarks>
/// The summary is projected into the agent listing that every peer reads before delegating, so an
/// unbounded self-written summary would inflate every other agent's prompt. The bound therefore
/// lives on the write seam - one place, configurable - rather than being duplicated at each
/// projection.
/// </remarks>
public sealed class AgentSummaryOptions
{
    /// <summary>
    /// Default ceiling, in characters. Roughly a short paragraph: long enough to say what changed
    /// about an agent's work, short enough that a dozen peers cost a few hundred tokens.
    /// </summary>
    public const int DefaultMaxLength = 500;

    /// <summary>Maximum allowed length, in characters, of an agent-written summary.</summary>
    [Display(
        Name = "Max summary length",
        Description = "Maximum length, in characters, of the agent-maintained summary. A longer summary is refused rather than silently truncated.",
        GroupName = "Agent summary",
        Order = 0)]
    [DefaultValue(DefaultMaxLength)]
    [Range(1, int.MaxValue)]
    [ConfigField(Widget = ConfigFieldWidget.Number, Group = "agent-summary", Order = 0)]
    public int MaxLength { get; set; } = DefaultMaxLength;
}
