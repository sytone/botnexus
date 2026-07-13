using System.IO;
using System.Linq;
using System.Text.Json;
using Xunit;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

/// <summary>
/// Validates the branded PWA icon assets are real artwork, not the solid-fill
/// placeholders that shipped originally (#1967). Guards against a regression to
/// empty single-colour squares and a mangled favicon glyph.
/// </summary>
public sealed class PwaIconAssetTests
{
    private static readonly string WwwrootPath = Path.GetFullPath(
        Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "..",
            "src", "extensions",
            "BotNexus.Extensions.Channels.SignalR.BlazorClient",
            "wwwroot"));

    [Theory]
    [InlineData("icon-192.png")]
    [InlineData("icon-512.png")]
    [InlineData("icon-192-maskable.png")]
    [InlineData("icon-512-maskable.png")]
    public void Icon_files_exist(string file)
    {
        var path = Path.Combine(WwwrootPath, file);
        Assert.True(File.Exists(path), $"{file} not found at {path}");
    }

    [Theory]
    [InlineData("icon-192.png")]
    [InlineData("icon-512.png")]
    [InlineData("icon-192-maskable.png")]
    [InlineData("icon-512-maskable.png")]
    public void Icon_files_are_not_trivial_placeholders(string file)
    {
        var path = Path.Combine(WwwrootPath, file);
        var bytes = File.ReadAllBytes(path);

        // The original placeholders were 642 B / 2251 B solid-colour fills. Real
        // artwork with a glyph compresses to several KB. A comfortable floor.
        Assert.True(bytes.Length > 4000,
            $"{file} is only {bytes.Length} bytes - looks like a solid-fill placeholder");

        Assert.True(HasMultipleColors(bytes),
            $"{file} decodes to a single colour - the glyph is missing");
    }

    [Fact]
    public void Maskable_icon_differs_from_any_icon()
    {
        // The manifest declares both purpose:any and purpose:maskable. Those must be
        // distinct files (maskable has extra safe-zone padding), not the same square.
        var any = File.ReadAllBytes(Path.Combine(WwwrootPath, "icon-512.png"));
        var maskable = File.ReadAllBytes(Path.Combine(WwwrootPath, "icon-512-maskable.png"));
        Assert.False(any.SequenceEqual(maskable),
            "maskable icon is byte-identical to the 'any' icon - safe-zone padding missing");
    }

    [Fact]
    public void Manifest_references_dedicated_maskable_icons()
    {
        var json = File.ReadAllText(Path.Combine(WwwrootPath, "manifest.webmanifest"));
        using var doc = JsonDocument.Parse(json);
        var icons = doc.RootElement.GetProperty("icons");

        var maskable = icons.EnumerateArray()
            .Where(i => i.TryGetProperty("purpose", out var p) && p.GetString() == "maskable")
            .Select(i => i.GetProperty("src").GetString())
            .ToList();

        Assert.NotEmpty(maskable);
        Assert.All(maskable, src => Assert.Contains("maskable", src));
    }

    [Fact]
    public void Favicon_has_no_mangled_glyph()
    {
        var svg = File.ReadAllText(Path.Combine(WwwrootPath, "favicon.svg"));
        // The bug was a literal "??" where a UTF-8 emoji should have been. The fix
        // uses an inline vector mark, so there must be no stray question-mark glyph.
        Assert.DoesNotContain("??", svg);
        // Real vector artwork uses shape primitives, not a single <text> fallback.
        Assert.True(
            svg.Contains("<circle") || svg.Contains("<path") || svg.Contains("<rect"),
            "favicon.svg has no vector shapes - it is not a real mark");
    }

    /// <summary>
    /// Decodes the IDAT stream of a PNG far enough to prove it contains more than
    /// one colour. Rather than depend on an image library, it inflates the raw
    /// pixel data and checks for pixel variation. Returns false for a solid fill.
    /// </summary>
    private static bool HasMultipleColors(byte[] png)
    {
        // Collect all IDAT chunk payloads.
        using var ms = new MemoryStream();
        var pos = 8; // skip PNG signature
        while (pos + 8 <= png.Length)
        {
            var len = (png[pos] << 24) | (png[pos + 1] << 16) | (png[pos + 2] << 8) | png[pos + 3];
            var type = System.Text.Encoding.ASCII.GetString(png, pos + 4, 4);
            var dataStart = pos + 8;
            if (type == "IDAT")
            {
                ms.Write(png, dataStart, len);
            }
            pos = dataStart + len + 4; // + CRC
            if (type == "IEND")
            {
                break;
            }
        }

        var compressed = ms.ToArray();
        if (compressed.Length < 3)
        {
            return false;
        }

        // zlib stream: skip 2-byte header, inflate the deflate body.
        using var inflate = new System.IO.Compression.DeflateStream(
            new MemoryStream(compressed, 2, compressed.Length - 2),
            System.IO.Compression.CompressionMode.Decompress);
        using var raw = new MemoryStream();
        inflate.CopyTo(raw);
        var bytes = raw.ToArray();

        // Filtered scanline bytes: if every byte is identical the image is a flat
        // single colour (placeholder). Any variation proves real content.
        if (bytes.Length == 0)
        {
            return false;
        }

        var first = bytes[0];
        return bytes.Any(b => b != first);
    }
}
