using BotNexus.Gateway.Agents;

namespace BotNexus.Gateway.Tests.Agents;

/// <summary>
/// Regression coverage for #2150: pathological token-per-line sub-agent completion
/// summaries must be normalized into clean Markdown while intentional structure
/// (paragraphs, headings, lists, fenced code blocks) is preserved unchanged.
/// </summary>
public sealed class SubAgentSummaryNormalizerTests
{
    [Fact]
    public void Normalize_CollapsesPathologicalTokenPerLineWhitespace()
    {
        // Observed CRLF/LF mixture from a real sub-agent completion summary.
        var pathological =
            "##\r\n Audit\r\n result\r\n\n\n\r\nThe\r\n webhook\r\n-registration\r\n path\r\n is\r\n clean.";

        var normalized = SubAgentSummaryNormalizer.Normalize(pathological);

        normalized.ShouldBe("## Audit result\n\nThe webhook -registration path is clean.");
    }

    [Fact]
    public void Normalize_LeavesNormalMarkdownParagraphsUnchanged()
    {
        var markdown =
            "# Heading\n\nThis is a normal paragraph with several words.\n\nAnother paragraph here.";

        var normalized = SubAgentSummaryNormalizer.Normalize(markdown);

        normalized.ShouldBe(markdown);
    }

    [Fact]
    public void Normalize_PreservesListItems()
    {
        var markdown =
            "Summary of work:\n\n- First item done\n- Second item done\n- Third item done";

        var normalized = SubAgentSummaryNormalizer.Normalize(markdown);

        normalized.ShouldBe(markdown);
    }

    [Fact]
    public void Normalize_PreservesFencedCodeBlocksVerbatim()
    {
        var markdown =
            "Here is the code:\n\n```csharp\nvar x = 1;\nvar y = 2;\n```\n\nDone.";

        var normalized = SubAgentSummaryNormalizer.Normalize(markdown);

        normalized.ShouldBe(markdown);
    }

    [Fact]
    public void Normalize_DoesNotJoinAcrossBlankLines()
    {
        // Single-token lines separated by a blank line are distinct paragraphs
        // and must not be joined together.
        var markdown = "First\n\nSecond";

        var normalized = SubAgentSummaryNormalizer.Normalize(markdown);

        normalized.ShouldBe("First\n\nSecond");
    }

    [Fact]
    public void Normalize_CollapsesMultipleBlankLinesToSingleParagraphBreak()
    {
        var input = "Alpha beta\n\n\n\nGamma delta";

        var normalized = SubAgentSummaryNormalizer.Normalize(input);

        normalized.ShouldBe("Alpha beta\n\nGamma delta");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Normalize_ReturnsInputUnchangedForNullOrEmpty(string? input)
    {
        SubAgentSummaryNormalizer.Normalize(input).ShouldBe(input);
    }
}
