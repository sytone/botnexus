using BotNexus.Gateway.Channels;
using Shouldly;

namespace BotNexus.Gateway.Tests;

public sealed class AssistantTextSanitizerTests
{
    [Fact]
    public void StripThinkingTags_NoTags_ReturnsUnchanged()
    {
        var text = "Hello, this is a plain response.";
        AssistantTextSanitizer.StripThinkingTags(text).ShouldBe(text);
    }

    [Fact]
    public void StripThinkingTags_NullOrEmpty_ReturnsUnchanged()
    {
        AssistantTextSanitizer.StripThinkingTags(string.Empty).ShouldBe(string.Empty);
        AssistantTextSanitizer.StripThinkingTags(null!).ShouldBeNull();
    }

    [Fact]
    public void StripThinkingTags_SingleThinkingBlock_RemovesBlock()
    {
        var text = "<thinking>I should reason carefully here.</thinking>The answer is 42.";
        AssistantTextSanitizer.StripThinkingTags(text).ShouldBe("The answer is 42.");
    }

    [Fact]
    public void StripThinkingTags_AnthropicPrefixedBlock_RemovesBlock()
    {
        var text = "<thinking>Chain of thought goes here.</thinking>Here is my answer.";
        AssistantTextSanitizer.StripThinkingTags(text).ShouldBe("Here is my answer.");
    }

    [Fact]
    public void StripThinkingTags_MultilineThinkingBlock_RemovesBlock()
    {
        var text = "Intro.\n<thinking>\nLine one of reasoning.\nLine two of reasoning.\n</thinking>\nConclusion.";
        AssistantTextSanitizer.StripThinkingTags(text).ShouldBe("Intro.\n\nConclusion.");
    }

    [Fact]
    public void StripThinkingTags_MultipleBlocks_RemovesAll()
    {
        var text = "<thinking>First thought.</thinking>Part one.<thinking>Second thought.</thinking>Part two.";
        AssistantTextSanitizer.StripThinkingTags(text).ShouldBe("Part one.Part two.");
    }

    [Fact]
    public void StripThinkingTags_CaseInsensitive_RemovesBlock()
    {
        var text = "<THINKING>Uppercase tags.</THINKING>Answer.";
        AssistantTextSanitizer.StripThinkingTags(text).ShouldBe("Answer.");
    }

    [Fact]
    public void StripThinkingTags_OnlyThinkingBlock_ReturnsEmptyOrTrimmed()
    {
        var text = "<thinking>Only reasoning, no actual response.</thinking>";
        AssistantTextSanitizer.StripThinkingTags(text).ShouldBe(string.Empty);
    }
}
