using System;
using BotNexus.Gateway.Api;

namespace BotNexus.Gateway.Api.Tests;

/// <summary>
/// Unit coverage for the gateway's single log-sanitisation seam (issue #3260).
/// </summary>
/// <remarks>
/// The hazard is log forgery: <c>Request.Path</c> and <c>Request.Method</c> are entirely
/// attacker-controlled, and a CR/LF inside either one lets a caller append a fabricated
/// record to any sink that renders structured properties to plain text. The auth middleware
/// is the site an unauthenticated caller reaches most easily, which is precisely the part of
/// the audit trail a security reviewer trusts.
/// </remarks>
public sealed class RequestLogTextTests
{
    // ---- clause 3: control characters are neutralised -------------------------------------

    [Fact]
    public void Safe_NeutralisesCarriageReturnAndLineFeed_SoASecondLogLineCannotBeForged()
    {
        // The canonical forgery: terminate the real record, then author a fake one that reads
        // like a successful admin authentication.
        const string craftedPath =
            "/api/health\r\n2026-08-18 00:00:00 [INF] Gateway request allowed: /api/config/agents. Identity: admin";

        var rendered = RequestLogText.Safe(craftedPath);

        Assert.DoesNotContain('\r', rendered);
        Assert.DoesNotContain('\n', rendered);

        // Anti-vacuity on the assertion itself: proving the newline is gone is only meaningful
        // if the forged text is still present as INERT content on the single surviving line.
        // A helper that silently dropped the whole tail would also pass a bare "no \n" check.
        Assert.Contains("Gateway request allowed", rendered);
        Assert.Single(rendered.Split('\n'));

        // The evidence of what was actually sent is preserved in escaped form, so an operator
        // reading the audit trail can still see the attempt.
        Assert.Contains("\\r\\n", rendered);
    }

    [Theory]
    [InlineData('\r')]
    [InlineData('\n')]
    [InlineData('\t')]
    [InlineData('\u0000')]
    [InlineData('\u0007')] // BEL
    [InlineData('\u0008')] // BS
    [InlineData('\u001b')] // ESC - the prefix of every ANSI/OSC/DCS sequence
    [InlineData('\u007f')] // DEL
    [InlineData('\u0085')] // NEL - a Unicode line break some sinks honour
    [InlineData('\u009b')] // C1 CSI
    public void Safe_EscapesEveryControlCharacter(char control)
    {
        var rendered = RequestLogText.Safe($"/api/x{control}y");

        Assert.DoesNotContain(control, rendered);
        Assert.StartsWith("/api/x", rendered, StringComparison.Ordinal);
        Assert.EndsWith("y", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void Safe_NeutralisesAnsiEscapeSequences_ByEscapingTheirIntroducer()
    {
        // OSC-52 (clipboard write) and CSI sequences reach an operator's terminal whenever the
        // log is tailed. Escaping ESC defeats every sequence by construction - there is no
        // sequence grammar to enumerate, because none of them survive without their introducer.
        var rendered = RequestLogText.Safe("/api/x\u001b]52;c;cGF5bG9hZA==\u0007");

        Assert.DoesNotContain('\u001b', rendered);
        Assert.DoesNotContain('\u0007', rendered);
        Assert.Contains("\\u001B", rendered);
    }

    [Fact]
    public void Safe_MethodValues_AreNeutralisedToo()
    {
        var rendered = RequestLogText.Safe("GET\r\nFORGED");

        Assert.DoesNotContain('\n', rendered);
        Assert.StartsWith("GET", rendered, StringComparison.Ordinal);
    }

    // ---- clause 4: legitimate values are unchanged ----------------------------------------

    [Theory]
    [InlineData("/api/config/agents")]
    [InlineData("/")]
    [InlineData("/api/agents/farnsworth/messages")]
    [InlineData("/api/conversations/c_2c33ba1b71834b7f80f81b21ee5a14f7")]
    [InlineData("/api/files/report%20final.md")]
    [InlineData("/api/search?q=a+b&limit=10")]
    [InlineData("/api/agents/f%C3%BCnf/memory")]
    [InlineData("GET")]
    [InlineData("DELETE")]
    [InlineData("WS")]
    public void Safe_LeavesLegitimateRequestValuesByteForByteIdentical(string legitimate)
    {
        // The guard must not degrade into mangling ordinary output: an operator grepping the
        // log for '/api/config/agents' must still find it, spelled exactly as it was requested.
        Assert.Equal(legitimate, RequestLogText.Safe(legitimate));
    }

    [Fact]
    public void Safe_PreservesNonAsciiText_WhichIsNotAControlHazard()
    {
        const string unicodePath = "/api/agents/日本語/memory";
        Assert.Equal(unicodePath, RequestLogText.Safe(unicodePath));
    }

    // ---- null / empty handling --------------------------------------------------------------

    [Fact]
    public void Safe_MapsNullToEmpty_SoCallSitesNeedNoNullCoalescing()
        => Assert.Equal(string.Empty, RequestLogText.Safe(null));

    [Fact]
    public void Safe_MapsEmptyToEmpty()
        => Assert.Equal(string.Empty, RequestLogText.Safe(string.Empty));

    /// <summary>
    /// The single load-bearing property, pinned independently of how the escaping is
    /// implemented: no input whatsoever may produce a CR or LF on the way out. This is also the
    /// property CodeQL's log-forging query checks for, and the reason the final
    /// line-break barrier is retained rather than folded into the escape loop.
    /// </summary>
    [Theory]
    [InlineData("/api/a\rb")]
    [InlineData("/api/a\nb")]
    [InlineData("/api/a\r\nb")]
    [InlineData("\r\n\r\n")]
    [InlineData("/api/\u001b[2K\rforged")]
    [InlineData("GET\r")]
    public void Safe_NeverEmitsALineBreak_ForAnyInput(string hostile)
    {
        var rendered = RequestLogText.Safe(hostile);

        Assert.DoesNotContain('\r', rendered);
        Assert.DoesNotContain('\n', rendered);
        Assert.Single(rendered.Split('\n'));
    }

    // ---- path-specific overload --------------------------------------------------------------

    [Fact]
    public void SafePath_SubstitutesRootForAnAbsentPath()
    {
        // Every converted site previously spelled its own '?? "/"' or '?? string.Empty'. The
        // seam owns that decision now so the sites cannot drift apart again.
        Assert.Equal("/", RequestLogText.SafePath(null));
        Assert.Equal("/", RequestLogText.SafePath(string.Empty));
    }

    [Fact]
    public void SafePath_SanitisesLikeSafe()
    {
        var rendered = RequestLogText.SafePath("/api/health\r\nFORGED");

        Assert.DoesNotContain('\n', rendered);
        Assert.DoesNotContain('\r', rendered);
        Assert.StartsWith("/api/health", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void SafePath_LeavesALegitimatePathIdentical()
        => Assert.Equal("/api/config/agents", RequestLogText.SafePath("/api/config/agents"));
}
