namespace BotNexus.Gateway.Notifications.Push;

/// <summary>
/// Base64url, the encoding every part of web push speaks.
/// </summary>
/// <remarks>
/// Subscription keys arrive from the browser this way, VAPID keys are exchanged this way, and JWT
/// segments are built this way - so it is worth one small shared helper rather than three private
/// copies that drift.
/// </remarks>
public static class Base64Url
{
    /// <summary>Encodes without padding, using the URL-safe alphabet.</summary>
    public static string Encode(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>Decodes, tolerating missing padding and either alphabet.</summary>
    public static byte[] Decode(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        var padded = value.Replace('-', '+').Replace('_', '/');

        // A browser sends these unpadded; Convert insists on padding.
        padded += (padded.Length % 4) switch
        {
            2 => "==",
            3 => "=",
            0 => "",
            _ => throw new FormatException($"'{value}' is not valid base64url."),
        };

        return Convert.FromBase64String(padded);
    }
}
