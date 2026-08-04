using BotNexus.Gateway.Channels;

namespace BotNexus.Gateway.Channels.Tests;

/// <summary>
/// AC5 of issue #2808: the escaped-encoding normalisation lives in exactly one place and
/// <see cref="AssistantTextSanitizer"/> CONSUMES it rather than restating the marker patterns
/// in a second, escaped spelling. These tests assert the consumption behaviourally - the
/// outbound path must neutralise escaped markup without owning its own escape vocabulary.
/// </summary>
public class AssistantTextSanitizerEscapedMarkerTests
{
    [Fact]
    public void EscapedToolCallBlock_IsStripped()
    {
        var result = AssistantTextSanitizer.Sanitize(
            @"answer \u003cinvoke name=""shell""\u003e\u003cparameter\u003ex\u003c/parameter\u003e\u003c/invoke\u003e");

        result.ShouldNotContain("invoke");
        result.ShouldNotContain("parameter");
        result.ShouldContain("answer");
    }

    [Fact]
    public void EntityEncodedToolCallBlock_IsStripped()
    {
        var result = AssistantTextSanitizer.Sanitize(
            "answer &lt;tool_use&gt;payload&lt;/tool_use&gt; done");

        result.ShouldNotContain("payload");
        result.ShouldContain("answer");
        result.ShouldContain("done");
    }

    [Fact]
    public void EscapedThinkingBlock_IsStripped()
    {
        var result = AssistantTextSanitizer.Sanitize(
            @"\u003cthinking\u003esecret reasoning\u003c/thinking\u003evisible");

        result.ShouldNotContain("secret reasoning");
        result.ShouldContain("visible");
    }

    [Fact]
    public void StripLeakedToolCalls_HandlesEscapedForm()
    {
        var result = AssistantTextSanitizer.StripLeakedToolCalls(
            @"text &lt;invoke name=""x""&gt;body&lt;/invoke&gt; tail");

        result.ShouldNotContain("body");
        result.ShouldContain("tail");
    }

    [Theory]
    [InlineData(@"Use \u003c and \u003e to escape angle brackets in JSON.")]
    [InlineData(@"Render &lt;b&gt; to show a bold tag in documentation.")]
    [InlineData(@"The token \x3c is a less-than sign.")]
    public void LegitimateProseContainingEscapes_IsPreservedUnchanged(string input)
    {
        AssistantTextSanitizer.Sanitize(input).ShouldBe(input);
    }
}
