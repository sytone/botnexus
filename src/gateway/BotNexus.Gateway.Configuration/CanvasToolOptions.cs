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

    /// <summary>
    /// The portal's external base URL, used to build the canvas deep link returned by a successful
    /// <c>render</c> (#2975). This is the single canonical source; when it is unset the tool falls
    /// back to <c>gateway.listenUrl</c> only if that names a concrete host, and otherwise emits NO
    /// link at all rather than a guessed one.
    /// </summary>
    /// <remarks>
    /// Not resolved from the inbound request host on purpose. A canvas render can originate from a
    /// cron job, a sub-agent or a Signal/Telegram turn, where there is no request to read - and a
    /// link that only works for renders triggered from the browser is worse than a consistent one.
    /// </remarks>
    [Display(
        Name = "Portal public base URL",
        Description = "External base URL of the portal (for example https://portal.example.com), used to build canvas deep links. When unset, a concrete gateway.listenUrl is used; if neither resolves, no link is emitted.",
        GroupName = "Canvas tool",
        Order = 2)]
    [ConfigField(Widget = ConfigFieldWidget.Text, Group = "canvas-tool", Order = 2)]
    public string? PublicBaseUrl { get; set; }

    /// <summary>
    /// Mirror of <c>gateway.listenUrl</c>, supplied by the tool provider so the canvas tool can fall
    /// back to it without taking a dependency on the wider gateway configuration graph. Not an
    /// operator-facing field of its own.
    /// </summary>
    public string? ListenUrl { get; set; }
}
