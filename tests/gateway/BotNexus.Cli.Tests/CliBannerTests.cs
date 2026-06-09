using BotNexus.Cli;

namespace BotNexus.Cli.Tests;

public sealed class CliBannerTests
{
    [Fact]
    public void Text_includes_shaded_botnexus_wordmark()
    {
        CliBanner.Text.ShouldContain("BotNexus");
        CliBanner.Text.ShouldContain("░░▒▒▓▓████████");
        CliBanner.Text.ShouldContain("LLM ORCHESTRATION LAB :: BAD IDEA DETECTOR :: TOOL WRANGLER");
        CliBanner.Text.ShouldContain("Mostly harmless");
        CliBanner.Text.ShouldContain("SHELL ACCESS");
        CliBanner.Text.ShouldContain("╭────────────╮");
        CliBanner.Text.ShouldContain("■    ■");
        CliBanner.Text.ShouldContain("╰────╯");
        CliBanner.Text.ShouldContain("questionable choices enabled");
        CliBanner.Text.ShouldContain("lightly smoking");
        CliBanner.Text.ShouldContain("tiny chaos, pocket-sized");
        CliBanner.Text.ShouldContain("no body, just terminal confidence");
    }

    [Fact]
    public async Task RunAsync_writes_banner_before_help_dispatch()
    {
        var writer = new StringWriter();

        var exitCode = await CliApp.RunAsync(new[] { "--help" }, writer);

        exitCode.ShouldBe(0);
        var output = writer.ToString();
        output.ShouldStartWith(CliBanner.Text);
    }

    [Fact]
    public async Task RunAsync_writes_banner_before_subcommand_help_dispatch()
    {
        var writer = new StringWriter();

        var exitCode = await CliApp.RunAsync(new[] { "validate", "--help" }, writer);

        exitCode.ShouldBe(0);
        var output = writer.ToString();
        output.ShouldStartWith(CliBanner.Text);
    }

    [Fact]
    public void ResolveBannerWriter_returns_supplied_writer_when_output_is_a_terminal()
    {
        var consoleOut = new StringWriter();

        var resolved = CliApp.ResolveBannerWriter(consoleOut, isOutputRedirected: false);

        resolved.ShouldBeSameAs(consoleOut);
    }

    [Fact]
    public void ResolveBannerWriter_returns_null_writer_when_output_is_redirected()
    {
        var consoleOut = new StringWriter();

        var resolved = CliApp.ResolveBannerWriter(consoleOut, isOutputRedirected: true);

        // Suppresses the banner so piped / captured / tee'd CLI output stays
        // machine-readable. Cross-OS test fixtures (ConfigPathResolverTests,
        // CliTestFixture) and shell pipelines depend on this contract.
        resolved.ShouldBeSameAs(TextWriter.Null);
    }

    [Fact]
    public void ResolveBannerWriter_throws_when_console_writer_is_null()
    {
        Action act = () => CliApp.ResolveBannerWriter(null!, isOutputRedirected: false);

        Should.Throw<ArgumentNullException>(act);
    }
}
