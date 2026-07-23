using System.Security.Cryptography;
using System.Text;
using BotNexus.Tools.Extensions;

namespace BotNexus.Tools.Utils;

/// <summary>
/// Computes a stable optimistic-concurrency token for a file's textual content (issue #2101).
/// The <c>read</c> tool surfaces the token so an agent can pass it back to the <c>edit</c> tool as
/// <c>expectedHash</c>; <c>edit</c> then rejects a stale edit (the file changed since it was read)
/// with a deterministic outcome instead of a blind "found 0" fuzzy-match failure.
/// </summary>
/// <remarks>
/// The token is computed over the decoded text with line endings normalized, so the same logical
/// content produces the same token regardless of CRLF/LF differences or a leading UTF-8 BOM (which
/// the decoder already strips). Both tools decode bytes the same way, so their tokens match for an
/// unchanged file.
/// </remarks>
public static class ContentToken
{
    /// <summary>
    /// Produces the concurrency token for the given decoded file text. Callers pass the text exactly
    /// as returned by <c>TextDecoder.DecodeBytes</c> (BOM already stripped); this method normalizes
    /// line endings before hashing so the token is stable across platforms.
    /// </summary>
    /// <param name="decodedText">The decoded, BOM-free file text.</param>
    /// <returns>A short, stable token such as <c>sha256:0a1b2c3d4e5f</c>.</returns>
    public static string Compute(string decodedText)
    {
        var normalized = (decodedText ?? string.Empty).NormalizeLineEndings();
        var bytes = Encoding.UTF8.GetBytes(normalized);
        var hash = SHA256.HashData(bytes);
        // 6 bytes (12 hex chars) is ample to detect an intervening change; keeping it short avoids
        // bloating the read output the agent sees on every file read.
        var builder = new StringBuilder(12);
        for (var i = 0; i < 6; i++)
        {
            builder.Append(hash[i].ToString("x2"));
        }

        return $"sha256:{builder}";
    }
}
