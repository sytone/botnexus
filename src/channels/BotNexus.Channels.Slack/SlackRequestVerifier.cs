using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

namespace BotNexus.Channels.Slack;

internal static class SlackRequestVerifier
{
    private const string SignatureHeader = "X-Slack-Signature";
    private const string TimestampHeader = "X-Slack-Request-Timestamp";
    private const string SignatureVersion = "v0";
    private static readonly TimeSpan MaxRequestAge = TimeSpan.FromMinutes(5);

    public static bool IsValid(IHeaderDictionary headers, string body, string signingSecret, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(signingSecret))
            return false;

        if (!TryGetHeader(headers, TimestampHeader, out var timestampHeader) ||
            !long.TryParse(timestampHeader, out var timestampUnix))
            return false;

        var requestTime = DateTimeOffset.FromUnixTimeSeconds(timestampUnix);
        if (now - requestTime > MaxRequestAge || requestTime - now > MaxRequestAge)
            return false;

        if (!TryGetHeader(headers, SignatureHeader, out var signatureHeader))
            return false;

        var expectedSignature = ComputeSignature(signingSecret, timestampHeader, body);
        return FixedTimeEquals(expectedSignature, signatureHeader);
    }

    private static string ComputeSignature(string signingSecret, string timestamp, string body)
    {
        var baseString = $"{SignatureVersion}:{timestamp}:{body}";
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(signingSecret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(baseString));
        return $"{SignatureVersion}={Convert.ToHexString(hash).ToLowerInvariant()}";
    }

    private static bool TryGetHeader(IHeaderDictionary headers, string key, out string value)
    {
        value = string.Empty;
        if (!headers.TryGetValue(key, out StringValues values))
            return false;

        value = values.ToString();
        return !string.IsNullOrWhiteSpace(value);
    }

    private static bool FixedTimeEquals(string expected, string actual)
    {
        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        var actualBytes = Encoding.UTF8.GetBytes(actual);

        return expectedBytes.Length == actualBytes.Length &&
               CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes);
    }
}
