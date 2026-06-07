using System.Text;
using BotNexus.Cli;

namespace BotNexus.Cli.Tests;

public sealed class CliAppOutputEncodingTests
{
    [Fact]
    public void ApplyOutputEncoding_RequestsUtf8()
    {
        Encoding? requested = null;

        CliApp.ApplyOutputEncoding(encoding => requested = encoding);

        // UTF-8 (code page 65001) is required so the banner's Unicode box-drawing
        // and shaded-block glyphs render instead of '?'/'\uFFFD' replacement chars.
        requested.ShouldNotBeNull();
        requested.CodePage.ShouldBe(Encoding.UTF8.CodePage);
    }

    [Fact]
    public void ApplyOutputEncoding_SwallowsIOException()
    {
        // Locked-down / redirected consoles throw IOException when reassigning the
        // encoding; CLI startup must stay alive rather than crash before any command runs.
        Action act = () => CliApp.ApplyOutputEncoding(_ => throw new IOException("no tty"));

        Should.NotThrow(act);
    }

    [Fact]
    public void ApplyOutputEncoding_SwallowsArgumentException()
    {
        Action act = () => CliApp.ApplyOutputEncoding(_ => throw new ArgumentException("bad encoding"));

        Should.NotThrow(act);
    }

    [Fact]
    public void ApplyOutputEncoding_PropagatesUnexpectedExceptions()
    {
        // Only the two documented, host-specific failures are swallowed; anything else
        // is a real bug and must surface rather than be silently hidden.
        Action act = () => CliApp.ApplyOutputEncoding(_ => throw new InvalidOperationException("boom"));

        Should.Throw<InvalidOperationException>(act);
    }

    [Fact]
    public void ApplyOutputEncoding_ThrowsWhenSetterIsNull()
    {
        Action act = () => CliApp.ApplyOutputEncoding(null!);

        Should.Throw<ArgumentNullException>(act);
    }
}
