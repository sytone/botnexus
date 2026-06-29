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

    // --- IsThinkingOnlyResponse ---

    [Fact]
    public void IsThinkingOnlyResponse_EmptyString_ReturnsFalse()
    {
        // Empty string has no thinking block, so it is not a thinking-only response.
        AssistantTextSanitizer.IsThinkingOnlyResponse(string.Empty).ShouldBeFalse();
    }

    [Fact]
    public void IsThinkingOnlyResponse_NullString_ReturnsFalse()
    {
        AssistantTextSanitizer.IsThinkingOnlyResponse(null!).ShouldBeFalse();
    }

    [Fact]
    public void IsThinkingOnlyResponse_PlainTextNoThinking_ReturnsFalse()
    {
        // Plain response with no thinking tags is not thinking-only.
        AssistantTextSanitizer.IsThinkingOnlyResponse("Here is the answer.").ShouldBeFalse();
    }

    [Fact]
    public void IsThinkingOnlyResponse_ThinkingBlockWithVisibleText_ReturnsFalse()
    {
        // Contains a thinking block but also has visible text outside it.
        var text = "<thinking>I should reason carefully.</thinking>Here is the answer.";
        AssistantTextSanitizer.IsThinkingOnlyResponse(text).ShouldBeFalse();
    }

    [Fact]
    public void IsThinkingOnlyResponse_OnlyThinkingBlock_ReturnsTrue()
    {
        // Response consists solely of a thinking block with no visible text.
        var text = "<thinking>Only reasoning, no actual response.</thinking>";
        AssistantTextSanitizer.IsThinkingOnlyResponse(text).ShouldBeTrue();
    }

    [Fact]
    public void IsThinkingOnlyResponse_AnthropicPrefixedOnlyThinkingBlock_ReturnsTrue()
    {
        // Anthropic-prefixed variant with no visible content.
        var text = "<thinking>Extended reasoning only.</thinking>";
        AssistantTextSanitizer.IsThinkingOnlyResponse(text).ShouldBeTrue();
    }

    [Fact]
    public void IsThinkingOnlyResponse_ThinkingBlockPlusWhitespace_ReturnsTrue()
    {
        // Whitespace around the thinking block is not user-visible content.
        var text = "  <thinking>Reasoning here.</thinking>  ";
        AssistantTextSanitizer.IsThinkingOnlyResponse(text).ShouldBeTrue();
    }

    // --- Sanitize: leaked tool-call XML (issue #1698) ---

    [Fact]
    public void Sanitize_LeakedInvokeBlock_RemovesBlock()
    {
        // opus-copilot serialises a tool call as <invoke> XML in the text channel instead of tool_use.
        var text = "Let me check.<invoke name=\"shell\"><parameter name=\"command\">ls</parameter></invoke>";
        AssistantTextSanitizer.Sanitize(text).ShouldBe("Let me check.");
    }

    [Fact]
    public void Sanitize_CourtJunkPrefixBeforeInvoke_StrippedClean()
    {
        // Observed signature: junk 'court' token prefixes the leaked XML.
        var text = "court<invoke name=\"read\"><parameter name=\"path\">x</parameter></invoke>The answer.";
        AssistantTextSanitizer.Sanitize(text).ShouldBe("The answer.");
    }

    [Fact]
    public void Sanitize_StrayInvokeAndParameterTags_Removed()
    {
        var text = "Intro </invoke> mid <parameter name=\"q\"> tail";
        AssistantTextSanitizer.Sanitize(text).ShouldBe("Intro  mid  tail");
    }

    [Fact]
    public void Sanitize_ToolUseAndFunctionCallsBlocks_Removed()
    {
        var text = "<tool_use>{}</tool_use><function_calls>noise</function_calls>real text";
        AssistantTextSanitizer.Sanitize(text).ShouldBe("real text");
    }

    [Fact]
    public void Sanitize_AlsoStripsThinking_Combined()
    {
        var text = "<thinking>plan</thinking>court<invoke name=\"x\"></invoke>Done.";
        AssistantTextSanitizer.Sanitize(text).ShouldBe("Done.");
    }

    [Fact]
    public void Sanitize_PlainText_Unchanged()
    {
        var text = "A normal reply with <angle> brackets in prose.";
        AssistantTextSanitizer.Sanitize(text).ShouldBe(text);
    }

    [Fact]
    public void Sanitize_NullOrEmpty_ReturnsUnchanged()
    {
        AssistantTextSanitizer.Sanitize(string.Empty).ShouldBe(string.Empty);
        AssistantTextSanitizer.Sanitize(null!).ShouldBeNull();
    }

    [Fact]
    public void StripLeakedToolCalls_RemovesInvokeBlock_PreservesProse()
    {
        var text = "Here is the result.<invoke name=\"read\"><parameter name=\"path\">x</parameter></invoke>";
        AssistantTextSanitizer.StripLeakedToolCalls(text).ShouldBe("Here is the result.");
    }

    [Fact]
    public void StripLeakedToolCalls_PreservesThinkingBlocks()
    {
        var text = "<thinking>reason</thinking>Answer.<invoke name=\"shell\">cmd</invoke>";
        var result = AssistantTextSanitizer.StripLeakedToolCalls(text);
        result.ShouldContain("<thinking>reason</thinking>");
        result.ShouldContain("Answer.");
        result.ShouldNotContain("invoke");
    }

    [Fact]
    public void StripLeakedToolCalls_StripsCourtPrefixAndStrayTags()
    {
        var text = "court<invoke name=\"x\">y</invoke> done <parameter>z</parameter>";
        var result = AssistantTextSanitizer.StripLeakedToolCalls(text);
        result.ShouldNotContain("court");
        result.ShouldNotContain("invoke");
        result.ShouldNotContain("parameter");
    }

    [Fact]
    public void StripLeakedToolCalls_PlainText_Unchanged()
    {
        var text = "A normal reply.";
        AssistantTextSanitizer.StripLeakedToolCalls(text).ShouldBe(text);
    }

    [Fact]
    public void StripLeakedToolCalls_NullOrEmpty_ReturnsUnchanged()
    {
        AssistantTextSanitizer.StripLeakedToolCalls(string.Empty).ShouldBe(string.Empty);
        AssistantTextSanitizer.StripLeakedToolCalls(null!).ShouldBeNull();
    }
}
