using System.Text;
using BotNexus.Tools.Utils;

namespace BotNexus.CodingAgent.Tests.Tools;

public sealed class TextDecoderTests
{
    [Fact]
    public void DecodeBytes_WhenNull_ReturnsEmpty()
    {
        TextDecoder.DecodeBytes(null).ShouldBe(string.Empty);
    }

    [Fact]
    public void DecodeBytes_WhenEmpty_ReturnsEmpty()
    {
        TextDecoder.DecodeBytes([]).ShouldBe(string.Empty);
    }

    [Fact]
    public void DecodeBytes_WhenPlainAscii_ReturnsText()
    {
        var bytes = Encoding.ASCII.GetBytes("hello world");

        TextDecoder.DecodeBytes(bytes).ShouldBe("hello world");
    }

    [Fact]
    public void DecodeBytes_WhenValidUtf8WithMultiByteChars_ReturnsText()
    {
        // Valid UTF-8 must always win over the legacy fallback.
        var original = "caf\u00e9 \u2014 \u65e5\u672c\u8a9e \uD83D\uDE00";
        var bytes = new UTF8Encoding(false).GetBytes(original);

        TextDecoder.DecodeBytes(bytes).ShouldBe(original);
    }

    [Fact]
    public void DecodeBytes_WhenUtf8Bom_StripsBomAndDecodes()
    {
        byte[] bom = [0xEF, 0xBB, 0xBF];
        var bytes = Concat(bom, new UTF8Encoding(false).GetBytes("with bom \u00e9"));

        var decoded = TextDecoder.DecodeBytes(bytes);

        decoded.ShouldBe("with bom \u00e9");
        decoded.ShouldNotStartWith("\uFEFF");
    }

    [Fact]
    public void DecodeBytes_WhenUtf16LeBom_Decodes()
    {
        var enc = new UnicodeEncoding(bigEndian: false, byteOrderMark: true);
        var bytes = Concat(enc.GetPreamble(), enc.GetBytes("utf16 le \u00e9"));

        TextDecoder.DecodeBytes(bytes).ShouldBe("utf16 le \u00e9");
    }

    [Fact]
    public void DecodeBytes_WhenUtf16BeBom_Decodes()
    {
        var enc = new UnicodeEncoding(bigEndian: true, byteOrderMark: true);
        var bytes = Concat(enc.GetPreamble(), enc.GetBytes("utf16 be \u00e9"));

        TextDecoder.DecodeBytes(bytes).ShouldBe("utf16 be \u00e9");
    }

    [Fact]
    public void DecodeBytes_WhenWindows1252WithoutBom_FallsBackAndDecodesReadable()
    {
        // 0xE9 = 'e-acute', 0x93/0x94 = curly double quotes in windows-1252. As raw bytes with no
        // BOM these are NOT valid UTF-8, so the blind UTF-8 path would have produced mojibake.
        byte[] bytes = [0x63, 0x61, 0x66, 0xE9, 0x20, 0x93, 0x68, 0x69, 0x94]; // caf<e9> <93>hi<94>

        var decoded = TextDecoder.DecodeBytes(bytes);

        // The decoded text must contain a readable 'e-acute' (not the U+FFFD replacement char and not
        // a multi-char mojibake run). On a Western Windows host this resolves via windows-1252; on any
        // host the Latin-1 last resort still yields a single readable code point for 0xE9.
        decoded.ShouldStartWith("caf");
        decoded.ShouldContain("\u00e9");
        decoded.ShouldNotContain("\uFFFD");
        decoded.ShouldContain("hi");
    }

    [Fact]
    public void DecodeBytes_WhenInvalidUtf8_NeverThrowsAndPreservesLength()
    {
        // A lone continuation byte 0x80 is invalid UTF-8 in isolation; the helper must degrade
        // gracefully (Latin-1 last resort) rather than throw.
        byte[] bytes = [0x41, 0x80, 0x42]; // 'A' <80> 'B'

        var decoded = Should.NotThrow(() => TextDecoder.DecodeBytes(bytes));

        decoded.ShouldStartWith("A");
        decoded.ShouldEndWith("B");
        decoded.Length.ShouldBe(3);
    }

    private static byte[] Concat(byte[] a, byte[] b)
    {
        var result = new byte[a.Length + b.Length];
        Buffer.BlockCopy(a, 0, result, 0, a.Length);
        Buffer.BlockCopy(b, 0, result, a.Length, b.Length);
        return result;
    }
}
