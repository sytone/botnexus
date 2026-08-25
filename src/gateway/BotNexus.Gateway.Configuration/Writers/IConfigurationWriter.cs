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
    /// <param name="document">The complete configuration document. Writers replace, never merge.</param>
    /// <param name="reason">
    /// Why the write is happening, for backup labelling and diagnostics. Not persisted as data.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task WriteAsync(JsonObject document, string reason, CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies an updated DTO for one subtree, writing only the keys that actually changed (#3532).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the contract callers should reach for; <see cref="WriteAsync"/> is for imports.</b> A
    /// whole-document write cannot distinguish "unchanged" from "not supplied", so it rewrites both
    /// identically and deletes anything the caller's DTO does not model. That is #2816: a
    /// <c>channels</c> write carrying one field destroyed the Service Bus settings and two Telegram bot
    /// tokens, and the credentials were unrecoverable.
    /// </para>
    /// <para>
    /// <paramref name="pathPrefix"/> is the blast radius. Removals are computed only within it, so a
    /// write to <c>agents.nova</c> cannot touch <c>channels</c> - not because the implementation is
    /// careful, but because it never looks there.
    /// </para>
    /// </remarks>
    /// <param name="dto">The updated object for <paramref name="pathPrefix"/>.</param>
    /// <param name="pathPrefix">
    /// Canonical dotted path the DTO speaks for, e.g. <c>agents.nova</c>. Empty means the whole document.
    /// </param>
    /// <param name="reason">Why the write is happening, for backup labelling and diagnostics.</param>
    /// <param name="options">Diff behaviour; defaults to <see cref="ConfigDiffOptions.Default"/>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The change set that was applied, so callers can report or assert on what moved.</returns>
    Task<ConfigChangeSet> ApplyAsync(
        object dto,
        string pathPrefix,
        string reason,
        ConfigDiffOptions? options = null,
        CancellationToken cancellationToken = default);
}
