using BotNexus.Gateway.Channels;

namespace BotNexus.Gateway.Channels.Tests;

/// <summary>
/// Covers the outbound strip of the delimited internal runtime-context envelope (#1430) and its
/// fail-closed behaviour on malformed delimiters (#2520). The strip must never emit envelope
/// content, and must leave prose that contains no BEGIN delimiter byte-identical.
/// </summary>
public sealed class RuntimeContextRedactorTests
{
    private const string Begin = RuntimeContextRedactor.BeginDelimiter;
    private const string End = RuntimeContextRedactor.EndDelimiter;

    /// <summary>Distinctive sentinel standing in for a real session id inside the envelope.</summary>
    private const string Sentinel = "sess-4f21c0de-LEAK-CANARY";

    private static string Envelope() =>
        $"{Begin}\nRuntime: agent=farnsworth | session={Sentinel} | host=SECRET-BOX\n{End}";

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
    public void Strip_NeverEmitsBeginDelimiter_WhenDelimitersAreUnbalancedOrPartial(string text)
    {
        var result = RuntimeContextRedactor.Strip(text);

        Assert.DoesNotContain(Begin, result);
    }

    /// <summary>
    /// #2520 core regression: a stray END echoed from untrusted text unbalanced the marker counts
    /// and previously suppressed the strip entirely, leaking the real envelope.
    /// </summary>
    [Fact]
    public void Strip_RemovesRealEnvelope_WhenStrayEndUnbalancesCounts()
    {
        var text = $"The page said {End} verbatim.\n{Envelope()}\nDone.";

        var result = RuntimeContextRedactor.Strip(text);

        Assert.DoesNotContain(Sentinel, result);
        Assert.DoesNotContain("SECRET-BOX", result);
        Assert.DoesNotContain(Begin, result);
    }

    /// <summary>#2520: an END appearing before the first BEGIN must not suppress the strip.</summary>
    [Fact]
    public void Strip_RemovesRealEnvelope_WhenEndPrecedesBegin()
    {
        var text = $"{End}\n{Envelope()}";

        var result = RuntimeContextRedactor.Strip(text);

        Assert.DoesNotContain(Sentinel, result);
        Assert.DoesNotContain("SECRET-BOX", result);
    }

    /// <summary>#2520: a nested/repeated BEGIN must not suppress the strip.</summary>
    [Fact]
    public void Strip_RemovesRealEnvelope_WhenBeginIsNested()
    {
        var text = $"before\n{Begin}\n{Begin}\nRuntime: session={Sentinel}\n{End}\n{End}\nafter";

        var result = RuntimeContextRedactor.Strip(text);

        Assert.DoesNotContain(Sentinel, result);
        Assert.DoesNotContain(Begin, result);
        Assert.Contains("before", result);
    }

    /// <summary>#2520: an unterminated BEGIN strips to end-of-text rather than emitting it.</summary>
    [Fact]
    public void Strip_StripsToEndOfText_WhenBeginIsUnterminated()
    {
        var text = $"Answer text.\n{Begin}\nRuntime: session={Sentinel} | host=SECRET-BOX";

        var result = RuntimeContextRedactor.Strip(text);

        Assert.DoesNotContain(Sentinel, result);
        Assert.DoesNotContain("SECRET-BOX", result);
        Assert.DoesNotContain(Begin, result);
        Assert.Equal("Answer text.\n", result);
    }

    /// <summary>
    /// #2520: an adversarial user string carrying a marker cannot suppress the strip of the real
    /// envelope appended after it.
    /// </summary>
    [Fact]
    public void Strip_RemovesRealEnvelope_WhenAdversarialUserTextCarriesMarkers()
    {
        var text = $"User asked about {Begin} and {End} and {End}.\n{Envelope()}";

        var result = RuntimeContextRedactor.Strip(text);

        Assert.DoesNotContain(Sentinel, result);
        Assert.DoesNotContain("SECRET-BOX", result);
        Assert.DoesNotContain(Begin, result);
    }

    /// <summary>#2520 inline/mid-line envelope must not survive.</summary>
    [Fact]
    public void Strip_RemovesRealEnvelope_WhenInlineMidLine()
    {
        var text = $"prefix {Begin}Runtime: session={Sentinel}{End} suffix";

        var result = RuntimeContextRedactor.Strip(text);

        Assert.DoesNotContain(Sentinel, result);
        Assert.Equal("prefix  suffix", result);
    }

    /// <summary>
    /// NEGATIVE case: ordinary prose that merely mentions the END marker contains no BEGIN, so it
    /// is returned byte-identical - the fail-closed change must not mangle legitimate replies.
    /// </summary>
    [Fact]
    public void Strip_ReturnsByteIdentical_WhenProseMentionsEndMarkerOnly()
    {
        var text = $"The log line to grep for is {End} - it terminates the block.";

        var result = RuntimeContextRedactor.Strip(text);

        Assert.Equal(text, result);
        Assert.Same(text, result);
    }

    [Fact]
    public void Strip_LeavesContentUntouched_WhenBlocksAreNested()
    {
        // Nested begins are swallowed by the scan; nothing marker-shaped survives.
        var text = $"{Begin}\nouter\n{Begin}\ninner\n{End}\n{End}";

        var result = RuntimeContextRedactor.Strip(text);

        Assert.DoesNotContain(Begin, result);
        Assert.DoesNotContain("outer", result);
        Assert.DoesNotContain("inner", result);
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
