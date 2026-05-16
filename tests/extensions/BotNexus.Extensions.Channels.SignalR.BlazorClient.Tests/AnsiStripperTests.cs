using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

public sealed class AnsiStripperTests
{
    [Fact]
    public void Strip_returns_null_for_null_input()
    {
        Assert.Null(AnsiStripper.Strip(null));
    }

    [Fact]
    public void Strip_returns_empty_for_empty_input()
    {
        Assert.Equal(string.Empty, AnsiStripper.Strip(string.Empty));
    }

    [Fact]
    public void Strip_returns_plain_text_unchanged()
    {
        const string plain = "Hello, world!";
        Assert.Equal(plain, AnsiStripper.Strip(plain));
    }

    [Fact]
    public void Strip_removes_color_codes()
    {
        // Bold red text followed by reset
        var input = "\u001b[31;1mERROR\u001b[0m: something failed";
        Assert.Equal("ERROR: something failed", AnsiStripper.Strip(input));
    }

    [Fact]
    public void Strip_removes_multiple_SGR_sequences()
    {
        var input = "\u001b[1m\u001b[32mOK\u001b[0m done \u001b[33mwarn\u001b[0m";
        Assert.Equal("OK done warn", AnsiStripper.Strip(input));
    }

    [Fact]
    public void Strip_removes_cursor_movement_sequences()
    {
        // Cursor up 2 lines, then erase line
        var input = "line1\u001b[2Aline2\u001b[K";
        Assert.Equal("line1line2", AnsiStripper.Strip(input));
    }

    [Fact]
    public void Strip_removes_osc_sequences_with_bel()
    {
        // OSC: set terminal title
        var input = "\u001b]0;My Title\u0007visible text";
        Assert.Equal("visible text", AnsiStripper.Strip(input));
    }

    [Fact]
    public void Strip_handles_mixed_ansi_and_plain_text()
    {
        var input = "Start \u001b[36mcyan\u001b[0m middle \u001b[4munderline\u001b[0m end";
        Assert.Equal("Start cyan middle underline end", AnsiStripper.Strip(input));
    }
}
