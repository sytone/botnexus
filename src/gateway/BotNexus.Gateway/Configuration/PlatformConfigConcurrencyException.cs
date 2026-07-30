namespace BotNexus.Gateway.Configuration;

/// <summary>
/// Thrown by <see cref="PlatformConfigWriter.UpdatePlatformConfigAsync"/> when the caller supplies
/// an expected revision that no longer matches the revision currently on disk (issue #2134).
/// </summary>
/// <remarks>
/// <para>
/// Replacing the whole platform configuration document is a full-aggregate write: a caller that
/// read a <see cref="PlatformConfig"/> snapshot, mutated one field and then wrote the whole
/// document back would silently write back the <em>stale</em> value of every other key -
/// clobbering a location, provider entry or channel subtree that another writer committed after
/// that read.
/// </para>
/// <para>
/// This mirrors the compare-and-swap guard <c>SqliteConversationStore</c> applies to conversation
/// aggregates via <c>ConversationConcurrencyException</c> (issue #2131). The difference is only in
/// how the revision token is derived: conversations carry a monotonic <c>version</c> column, while
/// config.json is a plain file with no place to keep a counter that is not itself user-visible
/// configuration, so the revision is a content digest of the committed document (an ETag). The
/// observable contract is identical - a save made against a revision that is no longer current is
/// rejected loudly instead of silently losing the other writer's changes.
/// </para>
/// <para>
/// Silent loss is forbidden: the caller must re-read, re-apply its intent and retry, or use one of
/// the narrow in-lock mutation operations (<see cref="PlatformConfigWriter.MutateSectionAsync"/>,
/// <see cref="PlatformConfigWriter.MutateValidatedAsync"/>) which never round-trip the whole
/// document and so cannot conflict.
/// </para>
/// </remarks>
public sealed class PlatformConfigConcurrencyException : InvalidOperationException
{
    /// <summary>Gets the configuration file whose save was rejected.</summary>
    public string ConfigPath { get; }

    /// <summary>Gets the revision the caller's snapshot was loaded at.</summary>
    public string ExpectedRevision { get; }

    /// <summary>Gets the revision currently committed on disk.</summary>
    public string ActualRevision { get; }

    /// <summary>
    /// Initialises a new instance of the <see cref="PlatformConfigConcurrencyException"/> class.
    /// </summary>
    /// <param name="configPath">The configuration file whose save was rejected.</param>
    /// <param name="expectedRevision">The revision the caller's snapshot was loaded at.</param>
    /// <param name="actualRevision">The revision currently committed on disk.</param>
    public PlatformConfigConcurrencyException(string configPath, string expectedRevision, string actualRevision)
        : base($"Configuration '{configPath}' was modified by another writer since it was read (expected revision {expectedRevision}, found {actualRevision}). Re-read the configuration and retry, or use a narrow in-lock section mutation.")
    {
        ConfigPath = configPath;
        ExpectedRevision = expectedRevision;
        ActualRevision = actualRevision;
    }
}
