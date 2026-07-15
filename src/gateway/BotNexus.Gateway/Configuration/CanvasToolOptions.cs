using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Gateway.Configuration;

/// <summary>
/// Bounds for the <c>canvas</c> tool's <c>set_state</c> action. These caps exist to stop an
/// agent (or canvas JavaScript writing through the same store path) from persisting arbitrarily
/// large values or unbounded distinct keys into a conversation's canvas state, which would bloat
/// the conversation store and inflate every prompt that surfaces canvas state.
/// </summary>
public sealed class CanvasToolOptions
{
    /// <summary>
    /// Maximum allowed length, in characters, of a canvas-state <c>key</c>.
    /// A <c>set_state</c> call with a longer key is rejected without writing to the store.
    /// </summary>
    [Display(
        Name = "Max key length",
        Description = "Maximum allowed length, in characters, of a canvas-state key. A set_state call with a longer key is rejected.",
        GroupName = "Canvas tool",
        Order = 0)]
    [DefaultValue(256)]
    [Range(1, int.MaxValue)]
    [ConfigField(Widget = ConfigFieldWidget.Number, Group = "canvas-tool", Order = 0)]
    public int MaxKeyLength { get; set; } = 256;

    /// <summary>
    /// Maximum allowed size, in bytes (UTF-8), of the serialised canvas-state <c>value</c>.
    /// A <c>set_state</c> call whose serialised value exceeds this size is rejected without
    /// writing to the store. Defaults to 64 KB.
    /// </summary>
    [Display(
        Name = "Max value bytes",
        Description = "Maximum allowed size, in UTF-8 bytes, of a serialised canvas-state value. A set_state call whose value exceeds this size is rejected. Defaults to 64 KB.",
        GroupName = "Canvas tool",
        Order = 1)]
    [DefaultValue(64 * 1024)]
    [Range(1, int.MaxValue)]
    [ConfigField(Widget = ConfigFieldWidget.Number, Group = "canvas-tool", Order = 1)]
    public int MaxValueBytes { get; set; } = 64 * 1024;
}
