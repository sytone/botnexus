using System.Text;
using BotNexus.Tools.Utils;

namespace BotNexus.Tools.Tests;

/// <summary>
/// Unit coverage for <see cref="TextDecoder"/> - the UTF-8-first, code-page-fallback
/// byte-to-text decoder that keeps the file-reading tools from emitting mojibake on
/// legacy encodings. Covers BOM handling, strict UTF-8, code-page fallback, and the
/// empty/null guards.
/// </summary>
public sealed class TextDecoderTests
{
    [Fact]
    public void DecodeBytes_Null_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, TextDecoder.DecodeBytes(null));
    }

    [Fact]
    public void DecodeBytes_Empty_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, TextDecoder.DecodeBytes(Array.Empty<byte>()));
    }

    [Fact]
    public void DecodeBytes_PlainAsciiUtf8_RoundTrips()
    {
        var bytes = Encoding.UTF8.GetBytes("hello world");
        Assert.Equal("hello world", TextDecoder.DecodeBytes(bytes));
    }

    [Fact]
    public void DecodeBytes_Utf8MultiByte_RoundTrips()
    {
        var text = "caf\u00e9 \u65e5\u672c\u8a9e \u2013 dash";
        var bytes = Encoding.UTF8.GetBytes(text);
        Assert.Equal(text, TextDecoder.DecodeBytes(bytes));
    }

    [Fact]
    public void DecodeBytes_Utf8Bom_IsStrippedAndDecoded()
    {
        var bom = new byte[] { 0xEF, 0xBB, 0xBF };
        var body = Encoding.UTF8.GetBytes("with bom");
        var bytes = bom.Concat(body).ToArray();

        var decoded = TextDecoder.DecodeBytes(bytes);

        Assert.Equal("with bom", decoded);
        Assert.DoesNotContain('\uFEFF', decoded);
    }

    [Fact]
    public void DecodeBytes_Utf16LeBom_IsDecoded()
    {
        var bytes = Encoding.Unicode.GetPreamble()
            .Concat(Encoding.Unicode.GetBytes("unicode le")).ToArray();
        Assert.Equal("unicode le", TextDecoder.DecodeBytes(bytes));
    }

    [Fact]
    public void DecodeBytes_Utf16BeBom_IsDecoded()
    {
        var bytes = Encoding.BigEndianUnicode.GetPreamble()
            .Concat(Encoding.BigEndianUnicode.GetBytes("unicode be")).ToArray();
        Assert.Equal("unicode be", TextDecoder.DecodeBytes(bytes));
    }

    [Fact]
    public void DecodeBytes_Utf32LeBom_IsDecoded()
    {
        var enc = new UTF32Encoding(bigEndian: false, byteOrderMark: true);
        var bytes = enc.GetPreamble().Concat(enc.GetBytes("utf32 le")).ToArray();
        Assert.Equal("utf32 le", TextDecoder.DecodeBytes(bytes));
    }

    [Fact]
    public void DecodeBytes_InvalidUtf8LegacyBytes_FallsBackWithoutThrowing()
    {
        // 0xE9 is 'é' in windows-1252 / Latin-1 but an invalid stand-alone UTF-8 lead byte.
        var bytes = new byte[] { (byte)'c', (byte)'a', (byte)'f', 0xE9 };

        var decoded = TextDecoder.DecodeBytes(bytes);

        // Must not throw and must not silently drop the byte; the legacy/Latin-1 fallback
        // maps 0xE9 to U+00E9 ('é').
        Assert.StartsWith("caf", decoded);
        Assert.Equal(4, decoded.Length);
        Assert.Equal('\u00e9', decoded[^1]);
    }
}
