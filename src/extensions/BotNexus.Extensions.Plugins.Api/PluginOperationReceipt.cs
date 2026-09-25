using BotNexus.Domain.Text;
using BotNexus.Extensions.Plugins.Lifecycle;

namespace BotNexus.Extensions.Plugins.Api;

/// <summary>Identifies the lifecycle command represented by an API receipt.</summary>
public enum PluginLifecycleOperation
{
    /// <summary>Plugin installation.</summary>
    Install,

    /// <summary>Plugin update.</summary>
    Update,

    /// <summary>Plugin removal.</summary>
    Remove,

    /// <summary>Update-preference change.</summary>
    SetUpdatePreference,
}

/// <summary>A bounded validation error safe to return across the plugin API boundary.</summary>
/// <param name="Field">Request field or lifecycle aspect that failed.</param>
/// <param name="Message">Sanitized explanation that contains no source diagnostics.</param>
public sealed record PluginOperationReceiptError(string Field, string Message);

/// <summary>
/// Public lifecycle receipt that deliberately excludes installed-source metadata and transport
/// diagnostics, which can contain repository credentials.
/// </summary>
public sealed record PluginOperationReceipt
{
    private const int MaximumErrors = 8;
    private const int MaximumErrorLength = 256;

    /// <summary>Lifecycle command that produced this receipt.</summary>
    public required PluginLifecycleOperation Operation { get; init; }

    /// <summary>Outcome reported by the lifecycle manager.</summary>
    public required PluginOperationOutcome Outcome { get; init; }

    /// <summary>Plugin identifier the command addressed.</summary>
    public required string Name { get; init; }

    /// <summary>Requested branch, tag, or commit when the command supplied one.</summary>
    public string? RequestedReference { get; init; }

    /// <summary>Resolved commit now installed, when available.</summary>
    public string? ResolvedCommit { get; init; }

    /// <summary>Current update preference, when an installed record is available.</summary>
    public bool? UpdatesEnabled { get; init; }

    /// <summary>Resolved commit present before the command, when applicable.</summary>
    public string? PreviousVersion { get; init; }

    /// <summary>Bounded, sanitized errors that contain no raw fetch output.</summary>
    public IReadOnlyList<PluginOperationReceiptError> Errors { get; init; } = [];

    /// <summary>Projects an internal result onto the credential-safe public wire contract.</summary>
    /// <param name="operation">Lifecycle command being reported.</param>
    /// <param name="result">Internal lifecycle result.</param>
    /// <param name="requestedReference">Reference explicitly supplied by the caller, if any.</param>
    public static PluginOperationReceipt FromResult(
        PluginLifecycleOperation operation,
        PluginOperationResult result,
        string? requestedReference = null) => new()
    {
        Operation = operation,
        Outcome = result.Outcome,
        Name = result.Name,
        RequestedReference = requestedReference ?? result.Plugin?.Reference,
        ResolvedCommit = result.Plugin?.ResolvedVersion,
        UpdatesEnabled = result.Plugin?.UpdatesEnabled,
        PreviousVersion = result.PreviousVersion,
        Errors = result.Errors
            .Take(MaximumErrors)
            .Select(SanitizeError)
            .ToArray(),
    };

    private static PluginOperationReceiptError SanitizeError(PluginValidationError error)
    {
        var containsSourceDiagnostic = string.Equals(error.Field, "source", StringComparison.OrdinalIgnoreCase) ||
            error.Message.Contains("plugin source", StringComparison.OrdinalIgnoreCase);
        var field = containsSourceDiagnostic ? "fetch" : error.Field;
        var message = containsSourceDiagnostic
            ? "The plugin could not be fetched or validated."
            : error.Message;
        message = TextTruncation.SafeTruncate(message, MaximumErrorLength) ?? string.Empty;
        return new PluginOperationReceiptError(field, message);
    }
}
