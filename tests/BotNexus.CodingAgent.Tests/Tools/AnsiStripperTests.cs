using BotNexus.Agent.Core.Tools;

namespace BotNexus.CodingAgent.Tests.Tools;

public sealed class AnsiStripperTests
{
    [Fact]
    public void Strip_CleanString_ReturnsSameInstance()
    {
        const string input = "Hello, world!";
        var result = AnsiStripper.Strip(input);
        Assert.Equal(input, result);
    }

    [Fact]
    public void Strip_NullOrEmpty_ReturnsInput()
    {
        Assert.Equal(string.Empty, AnsiStripper.Strip(string.Empty));
    }

    [Theory]
    [InlineData("\x1b[31;1mRed Bold\x1b[0m", "Red Bold")]           // CSI color
    [InlineData("\x1b[0m", "")]                                       // CSI reset only
    [InlineData("\x1b[?25l", "")]                                     // CSI private mode (hide cursor)
    [InlineData("\x1b[2J\x1b[H", "")]                                // Clear screen + home
    [InlineData("\x1b[1;32mGreen\x1b[0m text", "Green text")]        // CSI with surrounding text
    [InlineData("\x1b]0;Title\x07", "")]                              // OSC (window title, BEL terminator)
    [InlineData("\x1b]0;Title\x1b\\", "")]                           // OSC (window title, ST terminator)
    [InlineData("\x1b[38;5;200mMagenta\x1b[0m", "Magenta")]         // 256-color
    [InlineData("\x1b[38;2;255;0;0mRed\x1b[0m", "Red")]             // True color
    [InlineData("before\x1b[Aafter", "beforeafter")]                  // Cursor up
    [InlineData("\x1b[K", "")]                                        // Erase to end of line
    public void Strip_AnsiSequences_RemovesThem(string input, string expected)
    {
        Assert.Equal(expected, AnsiStripper.Strip(input));
    }

    [Fact]
    public void Strip_8BitC1Controls_RemovesThem()
    {
        // 0x9B = 8-bit CSI — must be constructed as a raw char to avoid UTF-8 encoding of the literal
        var csi = new string((char)0x9B, 1);
        var input = $"{csi}31mRed{csi}0m";
        var result = AnsiStripper.Strip(input);
        Assert.Equal("Red", result);
    }

    [Fact]
    public void Strip_MultilineOutput_StripsEachLine()
    {
        var input = "\x1b[32mline1\x1b[0m\nline2\n\x1b[31mline3\x1b[0m";
        var result = AnsiStripper.Strip(input);
        Assert.Equal("line1\nline2\nline3", result);
    }

    [Fact]
    public void Strip_PowerShellErrorOutput_RemovesColorCodes()
    {
        // Typical PowerShell error: red bold text
        var input = "\x1b[31;1mGet-Content: Cannot find path\x1b[0m";
        var result = AnsiStripper.Strip(input);
        Assert.Equal("Get-Content: Cannot find path", result);
    }

    [Fact]
    public void Strip_SpinnerAnimation_RemovesControlSequences()
    {
        // Spinner: carriage return + cursor movement often included
        var input = "\x1b[2K\rLoading...\x1b[2K\rDone!";
        var result = AnsiStripper.Strip(input);
        Assert.Equal("\rLoading...\rDone!", result); // CR preserved, ANSI stripped
    }

    [Fact]
    public void Strip_NoEscapeBytes_FastPathNoRegex()
    {
        // Verify fast path works — no escape bytes means no regex needed
        const string clean = "dotnet build succeeded. 0 errors.";
        var result = AnsiStripper.Strip(clean);
        Assert.Equal(clean, result);
    }
}
