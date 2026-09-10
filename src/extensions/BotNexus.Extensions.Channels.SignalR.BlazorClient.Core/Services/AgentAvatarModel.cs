using System.Globalization;
using System.Text;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;

/// <summary>
/// How one agent should be drawn: either its own emoji, or a generated monogram and hue.
/// Named ...Spec because <c>AgentAvatar</c> is the component that renders it.
/// </summary>
/// <param name="Emoji">The agent's configured emoji, or null when it has none.</param>
/// <param name="Monogram">One or two uppercase characters standing in for the agent.</param>
/// <param name="Hue">
/// Degrees on the colour wheel, 0-359, derived from the agent id. Only the HUE is decided here:
/// saturation and lightness belong to the stylesheet, which has to satisfy two themes and the
/// contrast floor the palette already documents.
/// </param>
public sealed record AgentAvatarSpec(string? Emoji, string Monogram, int Hue)
{
    /// <summary>True when the agent supplied its own emoji and the monogram is unused.</summary>
    public bool UsesEmoji => !string.IsNullOrWhiteSpace(Emoji);
}

/// <summary>
/// Gives every agent a distinguishable avatar without anyone having to configure one.
/// </summary>
/// <remarks>
/// <para>
/// Thirteen of sixteen agents on the reporting instance had no emoji set, so all thirteen rendered
/// the same default robot glyph: identity was carried entirely by reading a name, in the roster, the
/// picker and the transcript. Identity that depends on an operator remembering to set a field
/// degrades exactly as the roster grows, which is the wrong way round.
/// </para>
/// <para>
/// A configured emoji still wins. This only fills the gap, so nobody loses an avatar they chose.
/// </para>
/// </remarks>
public static class AgentAvatarModel
{
    /// <summary>Fallback monogram when neither name nor id yields a usable character.</summary>
    private const string Fallback = "?";

    /// <summary>
    /// Resolve the avatar for one agent.
    /// </summary>
    /// <param name="agentId">The agent's stable id. Seeds the hue.</param>
    /// <param name="displayName">The agent's display name. Seeds the monogram.</param>
    /// <param name="emoji">The agent's configured emoji, if any. Wins outright when present.</param>
    /// <param name="hueOverride">
    /// An operator-chosen hue. Null - the default - generates one from the agent id. An out-of-range
    /// value is wrapped rather than rejected, because a hue is an angle: 380 is 20, and refusing to
    /// draw an avatar over an arithmetic detail would be the wrong trade.
    /// </param>
    /// <returns>The avatar to draw.</returns>
    public static AgentAvatarSpec For(string? agentId, string? displayName, string? emoji, int? hueOverride = null)
    {
        var id = agentId ?? string.Empty;
        return new AgentAvatarSpec(
            string.IsNullOrWhiteSpace(emoji) ? null : emoji.Trim(),
            Monogram(displayName, id),
            hueOverride is int chosen ? Wrap(chosen) : HueFor(id));
    }

    /// <summary>Normalises any integer onto the colour wheel, negatives included.</summary>
    /// <param name="degrees">A hue in degrees, possibly out of range.</param>
    /// <returns>The equivalent hue in [0, 360).</returns>
    private static int Wrap(int degrees) => ((degrees % 360) + 360) % 360;

    /// <summary>
    /// A stable hue in [0, 360) for an agent id.
    /// </summary>
    /// <remarks>
    /// FNV-1a over the id's UTF-8 bytes, NOT <see cref="string.GetHashCode()"/>. String hashing in
    /// .NET is randomised per process, so the same agent would change colour on every gateway
    /// restart - and no test could pin the value. This has to be stable across processes, machines
    /// and releases, because it IS the agent's identity as far as a reader is concerned.
    /// <para>
    /// Hashed over raw UTF-8 bytes, so no culture-sensitive comparison sits between an id and its
    /// colour. Two ids differing only in case are two different ids and get two different hues -
    /// which is correct, because they are two different agents.
    /// </para>
    /// </remarks>
    /// <param name="agentId">The agent's stable id.</param>
    /// <returns>Hue in degrees, 0-359.</returns>
    public static int HueFor(string? agentId)
    {
        const uint offsetBasis = 2166136261;
        const uint prime = 16777619;

        var hash = offsetBasis;
        foreach (var b in Encoding.UTF8.GetBytes(agentId ?? string.Empty))
        {
            hash ^= b;
            hash *= prime;
        }

        return (int)(hash % 360u);
    }

    /// <summary>
    /// One or two uppercase characters for an agent, from its name where possible.
    /// </summary>
    /// <remarks>
    /// Words are split on whitespace and the separators these ids actually use
    /// (<c>-</c>, <c>_</c>, <c>.</c>, <c>:</c>), so <c>day-trader</c> reads as DT rather than DA.
    /// Only letters and digits are considered: an agent named with a leading emoji would otherwise
    /// take that emoji's surrogate pair as its "initial" and render as a broken glyph.
    /// <para>
    /// Private, and reached through <see cref="For"/>. A public static <c>string -&gt; string</c>
    /// that is not an extension method trips the repository's string-transformation fence, and the
    /// fence is right: this is one step of resolving an avatar, not a general text utility.
    /// </para>
    /// </remarks>
    /// <param name="displayName">Preferred source.</param>
    /// <param name="agentId">Used when the display name yields nothing.</param>
    /// <returns>A one- or two-character monogram, uppercased.</returns>
    private static string Monogram(string? displayName, string? agentId)
    {
        return FromWords(displayName) ?? FromWords(agentId) ?? Fallback;

        static string? FromWords(string? source)
        {
            if (string.IsNullOrWhiteSpace(source))
                return null;

            var words = source.Split(
                [' ', '\t', '\n', '\r', '-', '_', '.', ':', '/'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            var initials = new List<char>(2);
            foreach (var word in words)
            {
                var first = word.FirstOrDefault(char.IsLetterOrDigit);
                if (first != default)
                    initials.Add(first);

                if (initials.Count == 2)
                    break;
            }

            // A single word gives one initial; take a second letter from it so "assistant" reads as
            // AS rather than A - one character collides far too readily across a large roster.
            if (initials.Count == 1)
            {
                var word = words.FirstOrDefault(w => w.Any(char.IsLetterOrDigit));
                var rest = word?.Where(char.IsLetterOrDigit).Skip(1).FirstOrDefault() ?? default;
                if (rest != default)
                    initials.Add(rest);
            }

            return initials.Count == 0
                ? null
                : string.Concat(initials).ToUpper(CultureInfo.InvariantCulture);
        }
    }
}
