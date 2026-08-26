using System.Text.Json.Nodes;
using BotNexus.Gateway.Configuration.Store;

namespace BotNexus.Gateway.Configuration.Writers;

/// <summary>
/// Persists a platform configuration document to one backing store (#3527).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a backend interface rather than one writer that knows about both stores.</b> The file path
/// carries five properties a store does not need at all - no-op detection against existing bytes,
/// backup before replace, atomic temp-then-replace with retry, owner-only permissions applied twice,
/// and directory creation. A single writer aware of both backends would have to branch on all five,
/// and every future backend would widen the same conditional. Each backend owning its own concerns
/// keeps the fan-out itself trivial.
/// </para>
/// <para>
/// <b>Every registered writer receives every write.</b> That is the point: it is what makes a
/// JSON-to-SQLite transition lossless. With both registered the two stay in lockstep, so a rollback
/// at any point loses nothing and neither backend can drift behind the other. It also removes the
/// split-state defect that appeared the moment the store became reachable (#3514) - reads resolving
/// from the store while writes went only to the file, so a portal edit looked silently discarded.
/// </para>
/// </remarks>
public interface IConfigurationWriter
{
    /// <summary>
    /// A short, stable name for this backend, used in diagnostics when one writer fails.
    /// </summary>
    /// <remarks>
    /// Present so a partial-write failure can name WHICH store rejected the document. "the write
    /// failed" is not actionable when two backends are registered.
    /// </remarks>
    string Name { get; }

    /// <summary>
    /// Persists <paramref name="document"/>.
    /// </summary>
    /// <param name="document">
    /// The complete configuration document. Writers replace, never merge - this is the IMPORT path.
    /// An edit must use <see cref="ApplyChangeSetAsync"/> instead, because a whole-document write
    /// cannot distinguish "unchanged" from "not supplied" and deletes anything the caller did not
    /// model (#2816).
    /// </param>
    /// <param name="reason">
    /// Why the write is happening, for backup labelling and diagnostics. Not persisted as data.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task WriteAsync(JsonObject document, string reason, CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies a change set the caller has already computed (#3532).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the contract callers should reach for; <see cref="WriteAsync"/> is for imports.</b> A
    /// whole-document write cannot distinguish "unchanged" from "not supplied", so it rewrites both
    /// identically and deletes anything the caller did not model. That is #2816: a <c>channels</c> write
    /// carrying one field destroyed the Service Bus settings and two Telegram bot tokens, and the
    /// credentials were unrecoverable.
    /// </para>
    /// <para>
    /// <c>PlatformConfigWriter</c> mutates a <see cref="JsonObject"/> against a pristine snapshot, so it
    /// diffs document-against-document and needs no CLR type. A DTO-shaped overload was built first and
    /// deleted unused: projecting a typed object over an already-correct document adds a lossy step,
    /// because 33 of the 34 configuration classes carry no <c>[JsonExtensionData]</c> and would drop
    /// every key they do not model. DTOs belong on the read side, bound through <c>IOptions</c>.
    /// </para>
    /// <para>
    /// Backends must apply exactly the named keys and touch nothing else, and must apply removals
    /// BEFORE upserts - see <see cref="ConfigChangeSet.Removals"/> for why.
    /// </para>
    /// </remarks>
    /// <param name="changes">The keys to upsert and remove.</param>
    /// <param name="reason">Why the write is happening, for backup labelling and diagnostics.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task ApplyChangeSetAsync(
        ConfigChangeSet changes,
        string reason,
        CancellationToken cancellationToken = default);
}
