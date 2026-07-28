namespace BotNexus.Gateway.Abstractions.Conversations;

/// <summary>
/// Thrown by <see cref="IConversationStore.SaveAsync"/> when the conversation aggregate being
/// saved was loaded from an older revision than the one currently committed (issue #2131).
/// </summary>
/// <remarks>
/// <para>
/// <c>SaveAsync</c> is a full-row upsert of the whole aggregate. Without a compare-and-swap guard,
/// a caller that read a snapshot, mutated only <c>Title</c>/<c>Purpose</c>/<c>Instructions</c> and
/// then saved would silently write back the <em>stale</em> values of every other column -
/// clobbering a pin, canvas html, todo json or override that a narrow mutation committed after
/// that read.
/// </para>
/// <para>
/// The store therefore stamps each conversation with a monotonically increasing
/// <c>Conversation.Version</c> and refuses a save whose expected version no longer matches the
/// committed one. Silent loss is forbidden: the caller must re-read, re-apply its intent and retry
/// (or use one of the narrow patch operations, which never round-trip the whole aggregate and so
/// cannot conflict).
/// </para>
/// </remarks>
public sealed class ConversationConcurrencyException : InvalidOperationException
{
    /// <summary>Gets the conversation whose save was rejected.</summary>
    public string ConversationId { get; }

    /// <summary>Gets the version the caller's snapshot was loaded at.</summary>
    public long ExpectedVersion { get; }

    /// <summary>Gets the version currently committed in the store.</summary>
    public long ActualVersion { get; }

    /// <summary>
    /// Initialises a new instance of the <see cref="ConversationConcurrencyException"/> class.
    /// </summary>
    /// <param name="conversationId">The conversation whose save was rejected.</param>
    /// <param name="expectedVersion">The version the caller's snapshot was loaded at.</param>
    /// <param name="actualVersion">The version currently committed in the store.</param>
    public ConversationConcurrencyException(string conversationId, long expectedVersion, long actualVersion)
        : base($"Conversation '{conversationId}' was modified by another writer since it was read (expected version {expectedVersion}, found {actualVersion}). Re-read the conversation and retry, or use a narrow patch operation.")
    {
        ConversationId = conversationId;
        ExpectedVersion = expectedVersion;
        ActualVersion = actualVersion;
    }
}
