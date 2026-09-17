using System.Net.Http.Headers;

namespace BotNexus.Extensions.Channels.Matrix;

/// <summary>Validated components of a Matrix content URI.</summary>
internal readonly record struct MatrixContentUri(string ServerName, string MediaId)
{
    /// <summary>
    /// Accepts only absolute <c>mxc://server-name/media-id</c> references. HTTP(S) and encrypted
    /// file descriptors stay outside the download boundary by construction.
    /// </summary>
    public static bool TryParse(string? value, out MatrixContentUri contentUri)
    {
        contentUri = default;
        if (string.IsNullOrWhiteSpace(value)
            || !Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || !string.Equals(uri.Scheme, "mxc", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(uri.Authority)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment))
        {
            return false;
        }

        var escapedPath = uri.GetComponents(UriComponents.Path, UriFormat.UriEscaped);
        if (string.IsNullOrWhiteSpace(escapedPath))
            return false;

        string mediaId;
        try
        {
            mediaId = Uri.UnescapeDataString(escapedPath);
        }
        catch (UriFormatException)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(mediaId))
            return false;

        contentUri = new MatrixContentUri(uri.Authority, mediaId);
        return true;
    }
}

/// <summary>Normalises untrusted MIME metadata without discarding a valid explicit value.</summary>
internal static class MatrixMediaContentType
{
    public const string Fallback = "application/octet-stream";

    public static string Resolve(string? value) =>
        !string.IsNullOrWhiteSpace(value) && MediaTypeHeaderValue.TryParse(value, out _)
            ? value.Trim()
            : Fallback;
}
