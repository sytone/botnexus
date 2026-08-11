namespace BotNexus.Gateway.Abstractions.Agents;

/// <summary>
/// Whether the conversation a prompt is being built for is confined to its owner or has
/// non-owner participants in it (issue #2846).
/// </summary>
/// <remarks>
/// <para>
/// This exists because owner-private workspace content — <c>MEMORY.md</c>, <c>USER.md</c> and the
/// daily memory notes — is injected into the system prompt as authoritative context. In a
/// conversation the owner does not exclusively occupy, that content is disclosed to citizens who
/// have no claim on it. The tool surface already gates the equivalent read
/// (<c>MemorySearchTool</c> checks <c>ISharedMemoryStoreRegistry.CanRead</c>); before this type
/// existed the prompt surface had no gate at all and silently bypassed the tool-level control.
/// </para>
/// <para>
/// Callers that genuinely have no conversation (descriptor-only prompt builders) get
/// <see cref="Private"/>, which is byte-for-byte the pre-#2846 behaviour.
/// </para>
/// </remarks>
public enum ConversationScope
{
    /// <summary>
    /// The conversation is the owner's alone. Owner-private files are injected, exactly as they
    /// were before #2846. This is the default for every caller that does not state otherwise.
    /// </summary>
    Private = 0,

    /// <summary>
    /// The conversation carries participants beyond its owning agent and that agent's owner —
    /// a multi-participant conversation or a federated one. Owner-private files are withheld
    /// from the assembled prompt.
    /// </summary>
    Shared = 1,
}
