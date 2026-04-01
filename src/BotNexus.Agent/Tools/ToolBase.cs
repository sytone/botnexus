using BotNexus.Core.Abstractions;
using BotNexus.Core.Models;
using Microsoft.Extensions.Logging;

namespace BotNexus.Agent.Tools;

/// <summary>
/// Abstract base class for all agent tools.
/// Provides a consistent execution wrapper with error handling, argument parsing helpers,
/// and structured logging. Derived classes only need to implement <see cref="ExecuteCoreAsync"/>.
/// </summary>
public abstract class ToolBase : ITool
{
    /// <summary>Logger for this tool instance.</summary>
    protected readonly ILogger Logger;

    /// <summary>Initialises the tool with an optional logger.</summary>
    protected ToolBase(ILogger? logger = null)
    {
        Logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
    }

    /// <inheritdoc/>
    public abstract ToolDefinition Definition { get; }

    /// <inheritdoc/>
    public async Task<string> ExecuteAsync(
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default)
    {
        Logger.LogDebug("Executing tool '{ToolName}'", Definition.Name);
        try
        {
            var result = await ExecuteCoreAsync(arguments, cancellationToken).ConfigureAwait(false);
            Logger.LogDebug("Tool '{ToolName}' completed successfully", Definition.Name);
            return result;
        }
        catch (OperationCanceledException)
        {
            Logger.LogWarning("Tool '{ToolName}' was cancelled", Definition.Name);
            throw;
        }
        catch (ToolArgumentException ex)
        {
            Logger.LogWarning("Tool '{ToolName}' argument error: {Message}", Definition.Name, ex.Message);
            return $"Error: {ex.Message}";
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Tool '{ToolName}' threw an unexpected error", Definition.Name);
            return $"Error executing tool '{Definition.Name}': {ex.Message}";
        }
    }

    /// <summary>
    /// Performs the actual tool work. Exceptions are caught by <see cref="ExecuteAsync"/>
    /// and returned as error strings, except for <see cref="OperationCanceledException"/>
    /// which is re-thrown.
    /// </summary>
    protected abstract Task<string> ExecuteCoreAsync(
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken);

    // ── Argument helpers ────────────────────────────────────────────────────

    /// <summary>Gets a required string argument, throwing <see cref="ToolArgumentException"/> if absent or empty.</summary>
    protected static string GetRequiredString(IReadOnlyDictionary<string, object?> args, string key)
    {
        var value = args.GetValueOrDefault(key)?.ToString();
        if (string.IsNullOrWhiteSpace(value))
            throw new ToolArgumentException($"'{key}' is required and must be a non-empty string.");
        return value;
    }

    /// <summary>Gets an optional string argument, returning <paramref name="defaultValue"/> when absent.</summary>
    protected static string GetOptionalString(IReadOnlyDictionary<string, object?> args, string key, string defaultValue = "")
        => args.GetValueOrDefault(key)?.ToString() ?? defaultValue;

    /// <summary>Gets an optional integer argument.</summary>
    protected static int GetOptionalInt(IReadOnlyDictionary<string, object?> args, string key, int defaultValue = 0)
    {
        var raw = args.GetValueOrDefault(key);
        if (raw is null) return defaultValue;
        if (raw is int i) return i;
        if (raw is long l) return (int)l;
        return int.TryParse(raw.ToString(), out var parsed) ? parsed : defaultValue;
    }

    /// <summary>Gets an optional boolean argument.</summary>
    protected static bool GetOptionalBool(IReadOnlyDictionary<string, object?> args, string key, bool defaultValue = false)
    {
        var raw = args.GetValueOrDefault(key);
        if (raw is null) return defaultValue;
        if (raw is bool b) return b;
        return bool.TryParse(raw.ToString(), out var parsed) ? parsed : defaultValue;
    }
}

/// <summary>Exception thrown when a required tool argument is invalid or missing.</summary>
public sealed class ToolArgumentException(string message) : Exception(message);
