using BotNexus.Gateway.Channels;

namespace BotNexus.Gateway.Channels.Tests;

/// <summary>
/// Unit coverage for <see cref="AssistantTextSanitizer"/> - the outbound-text guard that
/// strips embedded thinking blocks and leaked tool-call XML before assistant text reaches
/// a channel (issues #1698 and the thinking-tag leak class). Covers happy paths (clean
/// text passes through untouched) and sad paths (malformed / leaked markup is removed).
/// </summary>
public sealed class AssistantTextSanitizerTests
{
    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void StripThinkingTags_NullOrEmpty_ReturnsInput(string? input)
    {
        Assert.Equal(input, AssistantTextSanitizer.StripThinkingTags(input!));
    }

    [Fact]
    public void StripThinkingTags_NoTags_ReturnsUnchanged()
    {
        const string text = "Just a normal reply with no markup.";
        Assert.Equal(text, AssistantTextSanitizer.StripThinkingTags(text));
    }

    [Fact]
    public void StripThinkingTags_RemovesThinkingBlock_KeepsVisibleText()
    {
        const string text = "<thinking>internal reasoning</thinking>Hello there";
        Assert.Equal("Hello there", AssistantTextSanitizer.StripThinkingTags(text));
    }

    [Fact]
    public void StripThinkingTags_IsCaseInsensitiveAndMultiline()
    {
        const string text = "<Thinking>\nline one\nline two\n</THINKING>\nvisible";
        Assert.Equal("visible", AssistantTextSanitizer.StripThinkingTags(text));
    }

    [Fact]
    public void IsThinkingOnlyResponse_OnlyThinkingBlock_ReturnsTrue()
    {
        const string text = "<thinking>all internal, nothing to say</thinking>";
        Assert.True(AssistantTextSanitizer.IsThinkingOnlyResponse(text));
    }

    [Fact]
    public void IsThinkingOnlyResponse_HasVisibleContent_ReturnsFalse()
    {
        const string text = "<thinking>reasoning</thinking>Actual answer.";
        Assert.False(AssistantTextSanitizer.IsThinkingOnlyResponse(text));
    }

    [Fact]
    public void IsThinkingOnlyResponse_NoThinkingBlock_ReturnsFalse()
    {
        Assert.False(AssistantTextSanitizer.IsThinkingOnlyResponse("plain text"));
        Assert.False(AssistantTextSanitizer.IsThinkingOnlyResponse(string.Empty));
    }

    [Fact]
    public void Sanitize_NullOrEmpty_ReturnsInput()
    {
        Assert.Null(AssistantTextSanitizer.Sanitize(null));
        Assert.Equal(string.Empty, AssistantTextSanitizer.Sanitize(string.Empty));
    }

    [Fact]
    public void Sanitize_CleanTextWithoutAngleBrackets_ReturnsUnchanged()
    {
        const string text = "no markup here at all";
        Assert.Equal(text, AssistantTextSanitizer.Sanitize(text));
    }

    [Fact]
    public void Sanitize_RemovesLeakedInvokeToolCallBlock()
    {
        const string text = "Answer: <invoke name=\"read\"><parameter name=\"path\">x</parameter></invoke>";
        var result = AssistantTextSanitizer.Sanitize(text);

        Assert.DoesNotContain("invoke", result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("parameter", result, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("Answer:", result);
    }

    [Fact]
    public void Sanitize_StripsCourtJunkPrefixBeforeToolCall()
    {
        const string text = "court<invoke name=\"x\"></invoke>";
        var result = AssistantTextSanitizer.Sanitize(text);
        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void Sanitize_RemovesBothThinkingAndToolCallMarkup()
    {
        const string text = "<thinking>plan</thinking>Done<invoke name=\"y\"></invoke>";
        var result = AssistantTextSanitizer.Sanitize(text);
        Assert.Equal("Done", result);
    }

    [Fact]
    public void StripLeakedToolCalls_PreservesThinkingBlock()
    {
        const string text = "<thinking>keep me</thinking><invoke name=\"z\"></invoke>";
        var result = AssistantTextSanitizer.StripLeakedToolCalls(text);

        Assert.Contains("<thinking>keep me</thinking>", result);
        Assert.DoesNotContain("invoke", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void StripLeakedToolCalls_NullOrClean_ReturnsInput()
    {
        Assert.Null(AssistantTextSanitizer.StripLeakedToolCalls(null));
        Assert.Equal("clean text", AssistantTextSanitizer.StripLeakedToolCalls("clean text"));
    }
}
