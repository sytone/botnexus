namespace BotNexus.Extensions.Plugins.Lifecycle;

/// <summary>What an install or update actually did.</summary>
public enum PluginOperationOutcome
{
    /// <summary>Content was fetched and materialised.</summary>
    Installed,

    /// <summary>Content was re-resolved and replaced with a newer revision.</summary>
    Updated,

    /// <summary>The source resolved to the revision already on disk; nothing was replaced.</summary>
    AlreadyCurrent,

    /// <summary>Updates are disabled for this plugin, so it was deliberately left untouched.</summary>
    SkippedPinned,

    /// <summary>Installed content was deleted.</summary>
    Removed,

    /// <summary>The operation failed; see <see cref="PluginOperationResult.Errors"/>.</summary>
    Failed,
}

/// <summary>
/// Outcome of a lifecycle operation. Failure is a value rather than an exception so a caller
/// installing several plugins can report each one's fate instead of aborting on the first.
/// </summary>
public sealed record PluginOperationResult
{
    /// <summary>What happened.</summary>
    public required PluginOperationOutcome Outcome { get; init; }

    /// <summary>Plugin identifier the operation applied to.</summary>
    public required string Name { get; init; }

    /// <summary>
    /// The record as it stands after the operation, or <c>null</c> when the operation failed or
    /// removed the plugin.
    /// </summary>
    public InstalledPlugin? Plugin { get; init; }

    /// <summary>Revision on disk before the operation, or <c>null</c> when nothing was installed.</summary>
    public string? PreviousVersion { get; init; }

    /// <summary>Why the operation failed; empty on success.</summary>
    public IReadOnlyList<PluginValidationError> Errors { get; init; } = [];

    /// <summary>True when the operation did not fail.</summary>
    public bool IsSuccess => Outcome != PluginOperationOutcome.Failed;

    /// <summary>Creates a failure result carrying one field-naming error.</summary>
    /// <param name="name">Plugin identifier.</param>
    /// <param name="field">Field or aspect at fault.</param>
    /// <param name="message">Human readable explanation.</param>
    public static PluginOperationResult Failure(string name, string field, string message) => new()
    {
        Outcome = PluginOperationOutcome.Failed,
        Name = name,
        Errors = [new PluginValidationError(field, message)],
    };

    /// <summary>Creates a failure result carrying several errors.</summary>
    /// <param name="name">Plugin identifier.</param>
    /// <param name="errors">Every reason the operation failed.</param>
    public static PluginOperationResult Failure(string name, IReadOnlyList<PluginValidationError> errors) => new()
    {
        Outcome = PluginOperationOutcome.Failed,
        Name = name,
        Errors = errors,
    };
}

/// <summary>Describes one plugin to install.</summary>
public sealed record PluginInstallRequest
{
    /// <summary>Marketplace source - a git URL for the git transport.</summary>
    public required string Source { get; init; }

    /// <summary>
    /// Expected plugin identifier, or <c>null</c> to accept whatever the fetched manifest declares.
    /// When supplied and the manifest disagrees, the install is rejected rather than silently
    /// installing content under a name the caller did not ask for.
    /// </summary>
    public string? Name { get; init; }

    /// <summary>Branch, tag or commit to check out, or <c>null</c> for the default branch.</summary>
    public string? Reference { get; init; }

    /// <summary>
    /// Whether update may replace this plugin's content later. Defaults to <c>true</c>: pinning
    /// is opt-in, per the settled decision in #2623.
    /// </summary>
    public bool UpdatesEnabled { get; init; } = true;

    /// <summary>
    /// Whether the caller has accepted that this plugin may carry a gateway extension - code that
    /// runs in-process, at full trust, with no sandbox.
    /// </summary>
    /// <remarks>
    /// Defaults to <c>false</c>, so carrying code is opt-in at the call site. A skill is prompt
    /// content; a carried extension is an assembly the gateway loads. Whether a source carries one
    /// is only knowable AFTER it is fetched and its manifest parsed, so consent cannot be resolved
    /// from the URL alone: install refuses a carried extension it was not told to expect, and the
    /// caller re-issues having been told what it would be agreeing to.
    /// </remarks>
    public bool AllowCarriedExtension { get; init; }
}
