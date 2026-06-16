using System.Globalization;
using System.Text;

namespace BotNexus.Tools.Utils;

/// <summary>
/// Decodes a raw byte buffer to text with a UTF-8-first, system-code-page-fallback strategy.
/// </summary>
/// <remarks>
/// <para>
/// Tools that read arbitrary files (<c>read</c>, <c>edit</c>, ...) used to call
/// <c>Encoding.UTF8.GetString(bytes)</c> unconditionally. On Windows that returns mojibake for
/// any legacy single- or multi-byte code-page file (windows-1252, shift_jis, gbk, ...) — the exact
/// code-page corruption class the platform conventions warn about. This helper instead:
/// </para>
/// <list type="number">
///   <item>honours a leading byte-order mark (UTF-8, UTF-16 LE/BE, UTF-32 LE/BE),</item>
///   <item>validates the remaining bytes as <em>strict</em> UTF-8 (the modern, dominant case), and</item>
///   <item>only when UTF-8 validation fails, falls back to the host's ANSI system code page
///         (e.g. windows-1252 on a typical Western Windows install), and finally to
///         <see cref="Encoding.Latin1"/> if the system code page cannot be resolved.</item>
/// </list>
/// <para>
/// The fallback is deliberately last so a well-formed UTF-8 file is never misread, while a genuine
/// legacy file decodes to readable text instead of mojibake. Decoding is best-effort and never throws
/// for content reasons — an undecodable buffer degrades to a lossless byte-preserving Latin-1 read.
/// </para>
/// </remarks>
public static class TextDecoder
{
    private static int s_codePagesProviderRegistered;

    /// <summary>
    /// Decodes <paramref name="bytes"/> to a string, preferring UTF-8 and falling back to the
    /// host's ANSI system code page for legacy text files.
    /// </summary>
    /// <param name="bytes">The raw file bytes. A <see langword="null"/> buffer is treated as empty.</param>
    /// <returns>The decoded text. Never <see langword="null"/>.</returns>
    public static string DecodeBytes(byte[]? bytes)
    {
        if (bytes is null || bytes.Length == 0)
        {
            return string.Empty;
        }

        // 1) Honour an explicit byte-order mark — the unambiguous case.
        if (TryDecodeWithBom(bytes, out var bomDecoded))
        {
            return bomDecoded;
        }

        // 2) Prefer strict UTF-8. A throwing decoder rejects any invalid sequence so we never
        //    silently mojibake a legacy file as if it were UTF-8.
        if (TryDecodeStrictUtf8(bytes, out var utf8Decoded))
        {
            return utf8Decoded;
        }

        // 3) Fall back to the host's ANSI system code page (windows-1252 / shift_jis / gbk / ...).
        var ansi = ResolveSystemAnsiEncoding();
        if (ansi is not null)
        {
            try
            {
                return ansi.GetString(bytes);
            }
            catch (Exception ex) when (ex is ArgumentException or DecoderFallbackException)
            {
                // Code page resolved but rejected the bytes — fall through to the lossless reader.
            }
        }

        // 4) Last resort: Latin-1 maps every byte 0x00-0xFF to a code point, so this never throws
        //    and preserves the raw bytes rather than losing data.
        return Encoding.Latin1.GetString(bytes);
    }

    private static bool TryDecodeWithBom(byte[] bytes, out string decoded)
    {
        // UTF-8 BOM: EF BB BF
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            decoded = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetString(bytes, 3, bytes.Length - 3);
            return true;
        }

        // UTF-32 LE BOM: FF FE 00 00 (check before UTF-16 LE, which shares the FF FE prefix).
        if (bytes.Length >= 4 && bytes[0] == 0xFF && bytes[1] == 0xFE && bytes[2] == 0x00 && bytes[3] == 0x00)
        {
            decoded = new UTF32Encoding(bigEndian: false, byteOrderMark: false).GetString(bytes, 4, bytes.Length - 4);
            return true;
        }

        // UTF-32 BE BOM: 00 00 FE FF
        if (bytes.Length >= 4 && bytes[0] == 0x00 && bytes[1] == 0x00 && bytes[2] == 0xFE && bytes[3] == 0xFF)
        {
            decoded = new UTF32Encoding(bigEndian: true, byteOrderMark: false).GetString(bytes, 4, bytes.Length - 4);
            return true;
        }

        // UTF-16 LE BOM: FF FE
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            decoded = new UnicodeEncoding(bigEndian: false, byteOrderMark: false).GetString(bytes, 2, bytes.Length - 2);
            return true;
        }

        // UTF-16 BE BOM: FE FF
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            decoded = new UnicodeEncoding(bigEndian: true, byteOrderMark: false).GetString(bytes, 2, bytes.Length - 2);
            return true;
        }

        decoded = string.Empty;
        return false;
    }

    private static bool TryDecodeStrictUtf8(byte[] bytes, out string decoded)
    {
        try
        {
            // throwOnInvalidBytes: any byte sequence that is not valid UTF-8 raises, so we can
            // distinguish a genuine UTF-8 file from a legacy code-page file.
            var strict = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
            decoded = strict.GetString(bytes);
            return true;
        }
        catch (DecoderFallbackException)
        {
            decoded = string.Empty;
            return false;
        }
    }

    private static Encoding? ResolveSystemAnsiEncoding()
    {
        // On .NET, Encoding.GetEncoding for a non-built-in code page (e.g. 1252, 932) requires the
        // CodePages provider to be registered. Register it once, best-effort.
        EnsureCodePagesProviderRegistered();

        try
        {
            var ansiCodePage = CultureInfo.CurrentCulture.TextInfo.ANSICodePage;
            if (ansiCodePage > 0)
            {
                return Encoding.GetEncoding(ansiCodePage);
            }
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            // Code page unavailable on this host (e.g. a stripped Linux runtime without the
            // CodePages provider for that page) — caller falls back to Latin-1.
        }

        return null;
    }

    private static void EnsureCodePagesProviderRegistered()
    {
        if (Interlocked.Exchange(ref s_codePagesProviderRegistered, 1) == 0)
        {
            try
            {
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            }
            catch (Exception)
            {
                // Provider already registered or unavailable — non-fatal; GetEncoding will simply
                // throw for an unsupported page and the caller falls back to Latin-1.
            }
        }
    }
}
