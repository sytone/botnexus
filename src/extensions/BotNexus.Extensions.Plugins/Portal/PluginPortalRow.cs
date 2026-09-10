using System.Text.Json.Serialization;

namespace BotNexus.Extensions.Plugins.Portal;

/// <summary>
/// Whether a plugin's content on disk still matches what install recorded.
/// </summary>
/// <remarks>
/// Three states rather than a boolean because "we did not look" and "we looked and it is fine"
/// are different answers, and collapsing them would present an unverifiable plugin as a trusted
/// one. The same collapse is what made #3244 and #3210 undiagnosable: when several
/// distinguishable states fold into one outcome there is no defect left to report.
/// </remarks>
public enum PluginTrustState
{
    /// <summary>
    /// No content hash catalog exists for this plugin, so integrity cannot be attested. This is
    /// the expected state until the install-time trust catalog of #2682 lands.
    /// </summary>
    Unverified = 0,

    /// <summary>Every file install recorded is present, and every hashed file matches its catalog entry.</summary>
    Verified = 1,

    /// <summary>
    /// Content diverges from the installed record - a recorded file is missing, or a hashed file's
    /// content no longer matches the catalog. Reported rather than repaired: silently re-materialising
    /// content would destroy the evidence an operator needs.
    /// </summary>
    Modified = 2,
}

/// <summary>
/// Whether a newer revision is available at a plugin's source.
/// </summary>
/// <remarks>
/// <see cref="Unknown"/> is the default and is deliberately NOT collapsed into "up to date".
/// Answering the update question costs a network round trip against the source, so the plugin
/// list does not pay it unless asked; reporting "current" without having looked would be a
/// claim rather than a finding.
/// </remarks>
public enum PluginUpdateState
{
    /// <summary>The source was not probed, so update availability is genuinely unknown.</summary>
    Unknown = 0,

    /// <summary>The source resolves to the revision already on disk.</summary>
    Current = 1,

    /// <summary>The source resolves to a different revision than the one on disk.</summary>
    UpdateAvailable = 2,

    /// <summary>
    /// Updates are disabled for this plugin, so the source is not probed at all - a pinned plugin
    /// would not be replaced by whatever the probe found.
    /// </summary>
    Pinned = 3,

    /// <summary>The source could not be probed; <see cref="PluginPortalRow.UpdateProbeError"/> says why.</summary>
    ProbeFailed = 4,
}

/// <summary>
/// One row of the portal plugins list: an installed plugin projected together with the two
/// derived states an operator actually needs - is it current, and is its content intact.
/// </summary>
/// <remarks>
/// A projection rather than <see cref="Lifecycle.InstalledPlugin"/> itself because the installed
/// record is the authority on what install wrote and must not grow presentation concerns, and
/// because the file list it carries is large and of no use to a list view.
/// </remarks>
public sealed record PluginPortalRow
{
    /// <summary>Plugin identifier, and the route parameter that addresses it.</summary>
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    /// <summary>Marketplace source the content was fetched from.</summary>
    [JsonPropertyName("source")]
    public required string Source { get; init; }

    /// <summary>Branch or tag requested at install time, or <c>null</c> for the source's default branch.</summary>
    [JsonPropertyName("reference")]
    public string? Reference { get; init; }

    /// <summary>Exact revision currently on disk - a commit SHA for a git source.</summary>
    [JsonPropertyName("resolvedVersion")]
    public required string ResolvedVersion { get; init; }

    /// <summary>Version the plugin's own manifest advertises, or <c>null</c> when unversioned.</summary>
    [JsonPropertyName("manifestVersion")]
    public string? ManifestVersion { get; init; }

    /// <summary>Whether a scheduled update may replace this plugin's content.</summary>
    [JsonPropertyName("updatesEnabled")]
    public required bool UpdatesEnabled { get; init; }

    /// <summary>When the content currently on disk was materialised.</summary>
    [JsonPropertyName("installedAtUtc")]
    public required DateTimeOffset InstalledAtUtc { get; init; }

    /// <summary>Number of files the install recorded, so a modified count is interpretable.</summary>
    [JsonPropertyName("fileCount")]
    public required int FileCount { get; init; }

    /// <summary>Integrity state of the content on disk.</summary>
    [JsonPropertyName("trustState")]
    public required PluginTrustState TrustState { get; init; }

    /// <summary>
    /// Why the trust state is what it is, in operator-readable terms, or <c>null</c> when there
    /// is nothing to explain. A bare "Modified" badge with no reason is not actionable.
    /// </summary>
    [JsonPropertyName("trustDetail")]
    public string? TrustDetail { get; init; }

    /// <summary>Update availability at the plugin's source.</summary>
    [JsonPropertyName("updateState")]
    public PluginUpdateState UpdateState { get; init; } = PluginUpdateState.Unknown;

    /// <summary>Revision the source currently resolves to, when it was probed.</summary>
    [JsonPropertyName("availableVersion")]
    public string? AvailableVersion { get; init; }

    /// <summary>Why the update probe failed, when <see cref="UpdateState"/> is <see cref="PluginUpdateState.ProbeFailed"/>.</summary>
    [JsonPropertyName("updateProbeError")]
    public string? UpdateProbeError { get; init; }

    /// <summary>
    /// Extension this plugin deployed, or <c>null</c> for a skills-only plugin. The portal needs it
    /// to tell which contributed nav entries belong to which plugin.
    /// </summary>
    [JsonPropertyName("deployedExtensionId")]
    public string? DeployedExtensionId { get; init; }

    /// <summary>Whether this plugin's contributed nav entries are hidden from the sidebar.</summary>
    [JsonPropertyName("navHidden")]
    public bool NavHidden { get; init; }
}
