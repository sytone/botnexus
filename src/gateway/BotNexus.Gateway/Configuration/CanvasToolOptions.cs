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
    public int MaxKeyLength { get; set; } = 256;

    /// <summary>
    /// Maximum allowed size, in bytes (UTF-8), of the serialised canvas-state <c>value</c>.
    /// A <c>set_state</c> call whose serialised value exceeds this size is rejected without
    /// writing to the store. Defaults to 64 KB.
    /// </summary>
    public int MaxValueBytes { get; set; } = 64 * 1024;
}
