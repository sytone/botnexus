namespace BotNexus.Extensions.DebugTool;

/// <summary>
/// Provides runtime state information for the debug tool's runtime_status action.
/// Injected by the contributor so the tool doesn't depend on gateway internals directly.
/// </summary>
public interface IRuntimeStateProvider
{
    /// <summary>
    /// Gets the current runtime status as a JSON-friendly string.
    /// </summary>
    string GetRuntimeStatus();
}
