using System.Security.Cryptography;
using System.Text;

namespace BotNexus.Agent.Providers.Core.Embeddings;

/// <summary>
/// Derives the fingerprint half of a stored vector's identity for a HOSTED embedding endpoint (#2855).
/// </summary>
/// <remarks>
/// <para>
/// A local model artefact can be fingerprinted by hashing the file. A hosted endpoint offers no
/// such artefact: the operator gets a model NAME and a promise, and the weights behind that name
/// can be replaced by the vendor without notice. The fingerprint therefore hashes everything the
/// platform actually knows that could change the coordinate space:
/// </para>
/// <list type="bullet">
///   <item><description>the provider key - <c>openai</c> and <c>ollama</c> serving the same model name are not the same service;</description></item>
///   <item><description>the normalised base URL - a different deployment (Azure OpenAI resource, a self-hosted Ollama) is a different space in practice;</description></item>
///   <item><description>the model id;</description></item>
///   <item><description>the declared dimension count - a width change is unambiguously a different model.</description></item>
/// </list>
/// <para>
/// What it deliberately does NOT do is pretend to detect a silent vendor-side weight swap. Nothing
/// observable at this layer could. The fingerprint's job is to make every difference the platform
/// CAN see produce a non-matching identity, so <c>EmbeddingIdentity.Matches</c> refuses to compare
/// vectors across them; a vendor swap remains the residual risk that re-embedding (#2106) exists
/// to address.
/// </para>
/// </remarks>
public static class HostedEmbeddingFingerprint
{
    /// <summary>Length of the returned hex fingerprint. 16 hex chars = 64 bits, ample for collision avoidance across a handful of configured endpoints, and short enough to read in a log line.</summary>
    private const int FingerprintLength = 16;

    /// <summary>
    /// Returns a stable lowercase hex fingerprint for a hosted embedding endpoint.
    /// </summary>
    /// <param name="providerKey">Provider key, e.g. <c>ollama</c>.</param>
    /// <param name="baseUrl">Endpoint base URL. May be <see langword="null"/> when the provider has a fixed endpoint.</param>
    /// <param name="modelId">Model identifier.</param>
    /// <param name="dimensions">Declared vector width.</param>
    public static string Derive(string providerKey, string? baseUrl, string modelId, int dimensions)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);

        // Trailing slashes and casing are incidental to the endpoint's identity; normalising them
        // stops a cosmetic config edit from orphaning every vector already stored.
        var normalizedBase = (baseUrl ?? string.Empty).Trim().TrimEnd('/').ToLowerInvariant();

        // Unit separator: no component may contain it, so no two distinct component tuples can
        // ever produce the same concatenation.
        var material = string.Join(
            '\u001f',
            providerKey.Trim().ToLowerInvariant(),
            normalizedBase,
            modelId.Trim(),
            dimensions.ToString(System.Globalization.CultureInfo.InvariantCulture));

        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(material));
        return Convert.ToHexStringLower(digest)[..FingerprintLength];
    }
}
