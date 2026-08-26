using System.Text.Json.Nodes;

namespace BotNexus.Gateway.Configuration.Store;

/// <summary>
/// The keys one write actually changes (#3532).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a change set rather than a document.</b> A whole-document write cannot distinguish "this
/// field is unchanged" from "this field is absent because the caller never modelled it", so it
/// rewrites both identically. That is the #2816 shape: a write of <c>channels</c> carrying only
/// <c>{"enabled": true}</c> replaced the section wholesale and destroyed the Service Bus settings and
/// two Telegram bot tokens. Teams was silently dead for four days and the credentials were
/// unrecoverable. Naming the changed keys explicitly removes the ambiguity: a key not in this set is
/// not written, so it cannot be lost.
/// </para>
/// <para>
/// <b>Removals are explicit because absence cannot carry intent.</b> In the eight keyed dictionaries
/// (<c>agents</c>, <c>providers</c>, <c>channels</c>, <c>apiKeys</c>, <c>locations</c>,
/// <c>satellites</c>, <c>peers</c>, <c>promptTemplates</c>) a removed entry is visible only as
/// absence, which a diff cannot distinguish from "not supplied" unless it is stated.
/// </para>
/// </remarks>
/// <param name="Upserts">
/// Keys to write, with their state and canonical value. Carries <see cref="ConfigEntry"/> rather than
/// a plain string value so the tri-state survives: an <see cref="ConfigValueState.ExplicitNull"/> entry
/// means "suppress the inherited value" and is a write, not a removal.
/// </param>
/// <param name="Removals">
/// Fully-qualified keys that existed before the change and are gone after it.
///
/// <para>
/// <b>Backends must apply removals BEFORE upserts.</b> A key can legitimately appear as both a removal
/// and the ancestor of an upsert, because the flattener treats an empty object and a scalar as leaves:
/// populating <c>"locations": {}</c>, or turning <c>"auth": "none"</c> into an object, removes the old
/// leaf and writes keys beneath it. Removals-first makes that sequence correct on both a document store
/// (the leaf is cleared, then the branch is built) and a row store (the stale leaf row is deleted, then
/// the child rows are inserted). Upserts-first would delete what was just written in the document case,
/// and filtering such removals out would strand an unreachable leaf row in the SQL case - which the
/// rehydrator rejects as an inconsistent store. Both mistakes were made and caught by
/// <c>ConfigWriteMatrixTests</c>; the ordering is load-bearing, not incidental.
/// </para>
/// </param>
public sealed record ConfigChangeSet(
    IReadOnlyList<ConfigEntry> Upserts,
    IReadOnlyList<string> Removals)
{
    /// <summary>
    /// True when the write would change nothing.
    /// </summary>
    /// <remarks>
    /// Callers use this to skip the write entirely. A no-op that still rewrote would churn the backup
    /// history and the file mtime for no reason, and on the store it would burn a transaction to write
    /// the rows it just read.
    /// </remarks>
    public bool IsEmpty => Upserts.Count == 0 && Removals.Count == 0;

    /// <summary>
    /// A short human-readable summary for diagnostics, e.g. <c>2 changed, 1 removed</c>.
    /// </summary>
    /// <remarks>
    /// Deliberately reports counts but never values or key names: configuration carries API keys and
    /// connection strings, and a diagnostic line is exactly the place a secret leaks into a log file
    /// (#3469).
    /// </remarks>
    public string Describe() => $"{Upserts.Count} changed, {Removals.Count} removed";
}
