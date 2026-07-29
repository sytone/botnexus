using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace BotNexus.Gateway.Abstractions.Security;

/// <summary>
/// The single source of truth for the short, opaque actor pseudonym that security events carry
/// instead of a raw identity (#2442).
/// </summary>
/// <remarks>
/// <para>
/// Before #2442 five byte-identical private <c>HashActor</c> helpers existed
/// (<c>ExecApprovalManager</c>, <c>ApiKeyGatewayAuthHandler</c>, <c>DefaultSubAgentManager</c>,
/// <c>SessionsController</c>, <c>ToolPolicyHookHandler</c>). They all implemented the same
/// scheme; five copies simply meant five chances to drift.
/// </para>
/// <para>
/// <b>The digest form is a compatibility contract, not an implementation detail.</b> Security
/// events already emitted and stored carry pseudonyms computed with this exact scheme, and
/// operators correlate incidents across that history by pseudonym. Changing the algorithm, the
/// truncation length, the hex case, or the encoding silently invalidates that correlation
/// without any error surfacing. The scheme is therefore pinned by golden vectors in
/// <c>ActorPseudonymTests</c> and by the architecture fence
/// <c>ActorPseudonymCentralizationArchitectureTests</c>.
/// </para>
/// <para>
/// Scheme: SHA-256 over the UTF-8 bytes of the id, truncated to the first 8 bytes, rendered as
/// lowercase hex with invariant culture. <see langword="null"/> and empty are treated
/// identically (both hash the empty byte sequence). It is a pseudonym, not a secret: it is not
/// reversible and the plaintext is never stored, but it is unsalted and therefore guessable for
/// a known candidate id - it exists for correlation, not confidentiality.
/// </para>
/// </remarks>
public static class ActorPseudonym
{
    /// <summary>Number of leading SHA-256 bytes retained. Part of the pinned wire contract.</summary>
    private const int DigestBytes = 8;

    /// <summary>
    /// Computes the stable pseudonym for <paramref name="id"/>. Pure and process-independent:
    /// the same input always yields the same 16-character lowercase hex string, in any process,
    /// under any culture.
    /// </summary>
    /// <param name="id">The raw actor id (agent id, session id, caller id). May be null.</param>
    public static string For(string? id)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(id ?? string.Empty));
        var sb = new StringBuilder(DigestBytes * 2);
        for (var i = 0; i < DigestBytes; i++)
            sb.Append(hash[i].ToString("x2", CultureInfo.InvariantCulture));
        return sb.ToString();
    }
}
