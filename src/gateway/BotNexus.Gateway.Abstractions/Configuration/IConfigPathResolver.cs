namespace BotNexus.Gateway.Abstractions.Configuration;

/// <summary>
/// Resolves and mutates values by dotted configuration path.
/// </summary>
public interface IConfigPathResolver
{
    /// <summary>
    /// Attempts to resolve a value from the supplied object graph.
    /// </summary>
    bool TryGetValue(object config, string path, out object? value, out string error);

    /// <summary>
    /// Attempts to set a value on the supplied object graph.
    /// </summary>
    bool TrySetValue(object config, string path, object? value, out string error);

    /// <summary>
    /// Gets available paths discovered from the current object graph.
    /// </summary>
    IReadOnlyList<string> GetAvailablePaths(object config);
}
