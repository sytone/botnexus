namespace BotNexus.Memory.Models;

/// <summary>
/// The closed vocabulary describing <b>where a memory entry's content came from</b>, and the
/// normalisation rules that make an unknown or malformed value fail safe (issue #2480).
/// </summary>
/// <remarks>
/// <para>
/// Before this existed the memory store recorded <i>what kind of write</i> produced a row
/// (<c>SourceType</c>: conversation, note, dreaming, manual) but not <i>whose words</i> the row
/// contains. Those are different questions, and conflating them opens a prompt-injection
/// laundering path: untrusted third-party text - a GitHub issue body, an inbound channel
/// message - is inert display-only input on the turn it arrives, but once summarised into a
/// memory row it reads back on a later session as first-party agent knowledge with its origin
/// erased. Provenance is the missing half of the trust boundary, and it is deliberately a
/// <i>separate</i> field from <see cref="MemoryEntry.SourceType"/> rather than more values
/// crammed into it.
/// </para>
/// <para>
/// <b>Fail-safe by construction.</b> <see cref="Normalize"/> maps null, whitespace and every
/// unrecognised string to <see cref="Unknown"/>, and <see cref="IsFirstParty"/> reports
/// <see langword="false"/> for it. A row whose provenance cannot be established is therefore
/// never silently promoted to trusted - the only way to be treated as first-party is to have
/// been explicitly stamped as such at write time. Pre-provenance rows read back as
/// <see cref="Unknown"/> for exactly this reason.
/// </para>
/// <para>
/// This issue is the <b>metadata</b> half only. Gating, quarantine and retrieval-time exclusion
/// of untrusted entries are tracked separately (#2519); nothing here filters or blocks anything.
/// </para>
/// </remarks>
public static class MemoryProvenance
{
    /// <summary>Content authored by the agent itself (its own reasoning, summaries, conclusions).</summary>
    public const string Agent = "agent";

    /// <summary>Content originating from the agent's own user/owner - a first-party human instruction.</summary>
    public const string User = "user";

    /// <summary>Content derived from a tool result executed by the agent.</summary>
    public const string Tool = "tool";

    /// <summary>
    /// Content ingested from a third party the agent does not control - an issue body, a
    /// comment, an inbound message from an unverified sender, fetched web content.
    /// </summary>
    public const string ExternalUntrusted = "external-untrusted";

    /// <summary>
    /// Provenance could not be established. This is the <b>fail-safe default</b>, not an error:
    /// it is what every pre-provenance row and every unrecognised value resolves to, and it is
    /// explicitly not first-party.
    /// </summary>
    public const string Unknown = "unknown";

    /// <summary>Every value <see cref="Normalize"/> can return, for validation and documentation.</summary>
    public static IReadOnlyList<string> All { get; } = [Agent, User, Tool, ExternalUntrusted, Unknown];

    /// <summary>
    /// Coerces an arbitrary stored or supplied value to a member of the closed vocabulary.
    /// </summary>
    /// <remarks>
    /// Case- and whitespace-insensitive, because the value crosses a JSON tool boundary and a
    /// SQLite text column, neither of which enforces casing. Anything not recognised becomes
    /// <see cref="Unknown"/> rather than being preserved verbatim, so a typo or a hostile value
    /// cannot invent a new trust level downstream.
    /// </remarks>
    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return Unknown;

        var trimmed = value.Trim();
        foreach (var candidate in All)
        {
            if (string.Equals(candidate, trimmed, StringComparison.OrdinalIgnoreCase))
                return candidate;
        }

        return Unknown;
    }

    /// <summary>
    /// Whether the (already normalised or raw) provenance denotes first-party content the agent
    /// may weigh as its own knowledge. <see cref="Unknown"/> and <see cref="ExternalUntrusted"/>
    /// are both <see langword="false"/>.
    /// </summary>
    public static bool IsFirstParty(string? value)
    {
        var normalized = Normalize(value);
        return normalized is Agent or User or Tool;
    }
}
