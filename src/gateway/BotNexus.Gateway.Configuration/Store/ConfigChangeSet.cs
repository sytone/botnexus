namespace BotNexus.Gateway.Configuration.Store;

/// <summary>
/// The keys one write actually changes, scoped to a subtree of the configuration document (#3532).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a change set rather than a document.</b> A whole-document write cannot distinguish "this
/// field is unchanged" from "this field is absent because the caller never modelled it", so it
/// rewrites both identically. That is the #2816 shape: a write of <c>channels</c> carrying only
/// <c>{"enabled": true}</c> replaced the section wholesale and destroyed the Service Bus settings and
/// two Telegram bot tokens underneath it. Teams was silently dead for four days and the credentials
/// were unrecoverable. Naming the changed keys explicitly removes the ambiguity: a key not in this
/// set is not written, so it cannot be lost.
/// </para>
/// <para>
/// <b><see cref="PathPrefix"/> is the blast radius, and it is the whole safety property.</b> Removals
/// are computed only within the prefix, so a write to <c>agents.nova</c> can never remove a key under
/// <c>channels</c> - not because the differ is careful, but because it never looks there. This is what
/// makes deletion expressible at all: in the eight keyed dictionaries (<c>agents</c>, <c>providers</c>,
/// <c>channels</c>, <c>apiKeys</c>, <c>locations</c>, <c>satellites</c>, <c>peers</c>,
/// <c>promptTemplates</c>) a removed entry is visible only as absence, which is indistinguishable from
/// "not supplied" unless the caller states the subtree it is speaking for.
/// </para>
/// <para>
/// An empty prefix means the DTO speaks for the entire document, which is the only case that behaves
/// like the old whole-document write. It is legal - a full import genuinely means it - but it is opt-in
/// rather than the default.
/// </para>
/// </remarks>
/// <param name="PathPrefix">
/// Canonical dotted path of the subtree this change set speaks for, e.g. <c>agents.nova</c>. Empty
/// means the whole document.
/// </param>
/// <param name="Upserts">
/// Keys to write, with their state and canonical value. Carries <see cref="ConfigEntry"/> rather than
/// a plain string value so the tri-state survives: an <see cref="ConfigValueState.ExplicitNull"/> entry
/// means "suppress the inherited value" and is a write, not a removal.
/// </param>
/// <param name="Removals">
/// Fully-qualified keys that existed under <see cref="PathPrefix"/> and are absent from the DTO.
/// Expressed explicitly because absence alone cannot carry intent.
/// </param>
public sealed record ConfigChangeSet(
    string PathPrefix,
    IReadOnlyList<ConfigEntry> Upserts,
    IReadOnlyList<string> Removals)
{
    /// <summary>
    /// True when the write would change nothing.
    /// </summary>
    /// <remarks>
    /// Callers use this to skip the write entirely. A no-op that still rewrites the file would churn
    /// the backup history and the file mtime for no reason, and on the store it would burn a
    /// transaction to write the rows it just read.
    /// </remarks>
    public bool IsEmpty => Upserts.Count == 0 && Removals.Count == 0;

    /// <summary>
    /// A short human-readable summary for diagnostics, e.g. <c>agents.nova: 2 changed, 1 removed</c>.
    /// </summary>
    /// <remarks>
    /// Deliberately reports counts and the prefix but never values: configuration carries API keys and
    /// connection strings, and a diagnostic line is exactly the place a secret leaks into a log file
    /// (#3469).
    /// </remarks>
    public string Describe()
    {
        var scope = PathPrefix.Length == 0 ? "(root)" : PathPrefix;
        return $"{scope}: {Upserts.Count} changed, {Removals.Count} removed";
    }
}
