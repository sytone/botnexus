using BotNexus.Gateway.Api.Export;

namespace BotNexus.Gateway.Tests.Export;

/// <summary>
/// Tests for <see cref="ExportFileName"/> (issue #3278, acceptance criterion 7): the download
/// filename must contain a slug plus the export date and must contain no character that is invalid
/// on Windows, Linux, or macOS.
/// </summary>
public sealed class ExportFileNameTests
{
    /// <summary>
    /// The union of characters rejected or dangerous across the three platforms, plus the header
    /// characters that would let a title forge <c>Content-Disposition</c> parameters.
    /// </summary>
    private static readonly char[] Invalid =
    [
        '<', '>', ':', '"', '/', '\\', '|', '?', '*', '\0', '\r', '\n', '\t', ';', ',', '\''
    ];

    [Theory]
    [InlineData("Quarterly planning", "quarterly-planning")]
    [InlineData("  spaced  out  ", "spaced-out")]
    [InlineData("Path/../../traversal", "path-traversal")]
    [InlineData("C:\\Windows\\System32", "c-windows-system32")]
    [InlineData("Café ☕ discussion", "cafe-discussion")]
    [InlineData("emoji only 🔬🔬", "emoji-only")]
    [InlineData("UPPER Case Title", "upper-case-title")]
    [InlineData("multi---hyphen", "multi-hyphen")]
    public void Slugify_ProducesTheExpectedSlug(string input, string expected)
        => ExportFileName.Slugify(input).ShouldBe(expected);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("🔬")]
    [InlineData("///")]
    public void Slugify_UnusableTitle_FallsBackToAConstant(string? input)
        => ExportFileName.Slugify(input).ShouldBe("transcript");

    [Theory]
    [InlineData("CON")]
    [InlineData("nul")]
    [InlineData("COM1")]
    [InlineData("LPT9")]
    [InlineData("AUX")]
    public void Slugify_ReservedDosDeviceName_IsPrefixed(string input)
    {
        // A file named CON.md cannot be created on Windows regardless of the extension, so the
        // download would fail at the last step for a conversation innocently titled "con".
        var slug = ExportFileName.Slugify(input);

        slug.ShouldNotBe(input.ToLowerInvariant());
        slug.ShouldStartWith("transcript-");
    }

    [Fact]
    public void Build_ContainsSlugAndExportDateAndExtension()
    {
        var generatedAt = new DateTimeOffset(2026, 8, 17, 13, 45, 0, TimeSpan.Zero);

        ExportFileName.Build("Quarterly planning", generatedAt, "md")
            .ShouldBe("quarterly-planning-2026-08-17.md");
        ExportFileName.Build("Quarterly planning", generatedAt, "html")
            .ShouldBe("quarterly-planning-2026-08-17.html");
    }

    [Theory]
    [InlineData("normal title")]
    [InlineData("../../etc/passwd")]
    [InlineData("a:b|c?d*e\"f<g>h")]
    [InlineData("line\r\nbreak; filename=\"evil.sh\"")]
    [InlineData("nul")]
    [InlineData("🔬 emoji 日本語")]
    [InlineData("an extremely long conversation title that goes well past any sensible filename length limit and just keeps going and going")]
    public void Build_NeverEmitsACharacterInvalidOnAnySupportedPlatform(string title)
    {
        var name = ExportFileName.Build(title, DateTimeOffset.UtcNow, "md");

        name.IndexOfAny(Invalid).ShouldBe(-1, $"'{name}' contains a filesystem- or header-unsafe character");
        name.ShouldNotContain("..");
        // Every character must be ASCII: a non-ASCII byte in a Content-Disposition filename
        // parameter is not portably interpretable by HTTP clients.
        name.ShouldAllBe(c => c < 128);
        // Keep the whole name inside the 255-byte limit every mainstream filesystem enforces.
        name.Length.ShouldBeLessThan(100);
        name.ShouldEndWith(".md");
    }
}
