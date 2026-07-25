using BotNexus.Gateway.Channels;

namespace BotNexus.Gateway.Channels.Tests;

/// <summary>
/// Covers the guarded outbound strip of the delimited internal runtime-context envelope (#1430).
/// The strip must remove balanced envelopes and must leave everything else byte-identical.
/// </summary>
public sealed class RuntimeContextRedactorTests
{
    private const string Begin = RuntimeContextRedactor.BeginDelimiter;
    private const string End = RuntimeContextRedactor.EndDelimiter;

    [Fact]
    public void Strip_RemovesBalancedRuntimeContextBlock()
    {
        var text = $"Hello.\n{Begin}\nRuntime: agent=farnsworth | host=SECRET-BOX\n{End}\nBye.";

        var result = RuntimeContextRedactor.Strip(text);

        Assert.Equal("Hello.\nBye.", result);
        Assert.DoesNotContain("SECRET-BOX", result);
        Assert.DoesNotContain(Begin, result);
        Assert.DoesNotContain(End, result);
    }

    [Fact]
    public void Strip_RemovesEveryBlock_WhenMultiplePresent()
    {
        var text = $"a\n{Begin}\nfirst\n{End}\nb\n{Begin}\nsecond\n{End}\nc";

        var result = RuntimeContextRedactor.Strip(text);

        Assert.Equal("a\nb\nc", result);
        Assert.DoesNotContain("first", result);
        Assert.DoesNotContain("second", result);
    }

    [Fact]
    public void Strip_ReturnsContentByteIdentical_WhenNoDelimitersPresent()
    {
        const string text = "Just an ordinary reply mentioning runtime and context, nothing delimited.";

        var result = RuntimeContextRedactor.Strip(text);

        Assert.Equal(text, result);
        Assert.Same(text, result);
    }

    [Theory]
    // Begin with no matching end.
    [InlineData("Please quote " + Begin + " for me")]
    // End with no matching begin.
    [InlineData("Please quote " + End + " for me")]
    // Out of order: end precedes begin.
    [InlineData(End + " then " + Begin)]
    // Unbalanced counts: two begins, one end.
    [InlineData(Begin + "\nx\n" + Begin + "\ny\n" + End)]
    public void Strip_LeavesContentUntouched_WhenDelimitersAreUnbalancedOrPartial(string text)
    {
        var result = RuntimeContextRedactor.Strip(text);

        Assert.Equal(text, result);
        Assert.Same(text, result);
    }

    [Fact]
    public void Strip_LeavesContentUntouched_WhenBlocksAreNested()
    {
        // Nested begins inside an envelope are malformed; the guarded clip refuses to mutate.
        var text = $"{Begin}\nouter\n{Begin}\ninner\n{End}\n{End}";

        var result = RuntimeContextRedactor.Strip(text);

        Assert.Equal(text, result);
    }

    [Fact]
    public void Strip_PreservesSurroundingContent_WhenBlockIsInline()
    {
        var text = $"before {Begin}leaked{End} after";

        var result = RuntimeContextRedactor.Strip(text);

        Assert.Equal("before  after", result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Strip_HandlesNullAndEmpty(string? text)
    {
        Assert.Equal(text, RuntimeContextRedactor.Strip(text));
    }
}
