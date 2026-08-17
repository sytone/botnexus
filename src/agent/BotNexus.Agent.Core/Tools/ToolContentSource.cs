namespace BotNexus.Agent.Core.Tools;

/// <summary>
/// The closed vocabulary describing <b>where the content a tool returns came from</b>, and the
/// normalisation rules that make an unknown or malformed value fail closed (issue #2519).
/// </summary>
/// <remarks>
/// <para>
/// This answers a different question from "what does this tool do". A tool is classified by the
/// <i>origin of the bytes in its result</i>, not by its capability or its risk of side effects.
/// <c>shell</c> is enormously powerful and is nevertheless <see cref="Local"/>, because the text it
/// hands back is produced on the machine the agent already controls. <c>web_fetch</c> is read-only
/// and is nevertheless <see cref="Network"/>, because every byte of its result was authored by a
/// remote party. Conflating power with provenance is what makes prompt-injection laundering
/// invisible: the dangerous tool is not the one that can act, it is the one that can <i>speak</i>
/// with a stranger's words.
/// </para>
/// <para>
/// <b>Fail closed by construction.</b> <see cref="Normalize"/> maps null, whitespace and every
/// unrecognised string to <see cref="Unknown"/>, and <see cref="IsTainting"/> reports
/// <see langword="true"/> for it. A tool whose content source cannot be established therefore
/// taints the turn rather than silently passing as trusted. <see cref="IAgentTool"/> defaults to
/// <see cref="Unknown"/> for exactly this reason: classification is opt-in and explicit, so a tool
/// added later - including a third-party contributed one - cannot inherit trust it never declared.
/// </para>
/// <para>
/// This is the <b>write-time</b> half of the trust boundary. It pairs with the recording half in
/// <c>MemoryProvenance</c> (#2480) and is deliberately kept separate from the retrieval-time trust
/// tiers tracked in #3232, which consume provenance rather than produce it.
/// </para>
/// </remarks>
public static class ToolContentSource
{
    /// <summary>
    /// Content produced on the machine or within the trust domain the agent already controls -
    /// the filesystem, a local process, the gateway's own stores, the agent's own memory.
    /// </summary>
    public const string Local = "local";

    /// <summary>
    /// Content retrieved from a remote network endpoint the agent does not control - fetched web
    /// pages, search results, remote API bodies. The bytes were authored by a stranger.
    /// </summary>
    public const string Network = "network";

    /// <summary>
    /// Content from a party that is neither local nor merely remote, but actively outside any
    /// trust assumption - a bridged MCP server, an externally driven browser session, an
    /// unverified third-party integration.
    /// </summary>
    public const string Untrusted = "untrusted";

    /// <summary>
    /// The content source could not be established. This is the <b>fail-closed default</b>, not an
    /// error: it is what every unclassified tool and every unrecognised value resolves to, and it
    /// taints the turn.
    /// </summary>
    public const string Unknown = "unknown";

    /// <summary>Every value <see cref="Normalize"/> can return, for validation and documentation.</summary>
    public static IReadOnlyList<string> All { get; } = [Local, Network, Untrusted, Unknown];

    /// <summary>
    /// Coerces an arbitrary declared value to a member of the closed vocabulary.
    /// </summary>
    /// <remarks>
    /// Case- and whitespace-insensitive, because the value is authored by hand on each tool and
    /// may cross a configuration or serialisation boundary. Anything unrecognised becomes
    /// <see cref="Unknown"/> rather than being preserved verbatim, so a typo or a hostile value
    /// cannot invent a new trust level downstream. Note the asymmetry with a permissive parser:
    /// a misspelt <c>"locel"</c> does not become <see cref="Local"/>, it becomes
    /// <see cref="Unknown"/> and therefore taints - the safe direction to be wrong in.
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
    /// Whether content from this source taints the turn it was consumed on. Only
    /// <see cref="Local"/> does not; <see cref="Network"/>, <see cref="Untrusted"/> and
    /// <see cref="Unknown"/> all do.
    /// </summary>
    /// <remarks>
    /// Expressed as "everything except <see cref="Local"/>" rather than as a list of tainting
    /// values on purpose. Written the other way, adding a member to the vocabulary would default
    /// it to trusted and silently open a hole; written this way, a new member defaults to
    /// tainting and the failure mode of forgetting to update this method is over-caution.
    /// </remarks>
    public static bool IsTainting(string? value) => Normalize(value) != Local;
}
