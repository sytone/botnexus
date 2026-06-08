using System.CommandLine;
using BotNexus.Cli.Commands;
using BotNexus.Cli.Commands.Provider;
using Shouldly;

namespace BotNexus.Cli.Tests.Commands.Provider;

/// <summary>
/// Wiring tests for the <c>botnexus provider copilot</c> subcommand tree.
/// These tests don't exercise the network — they confirm the subcommand and
/// option topology stays stable so renaming a verb or dropping an option is
/// caught here rather than by users in the field.
/// </summary>
public class CopilotProviderSubcommandTests
{
    [Fact]
    public void Build_registers_copilot_command_under_provider()
    {
        var verbose = new Option<bool>("--verbose");
        var providerCmd = new ProviderCommand().Build(verbose, new Option<string?>("--target"));

        var copilot = providerCmd.Subcommands.SingleOrDefault(c => c.Name == "copilot");
        copilot.ShouldNotBeNull();
        copilot!.Description.ShouldNotBeNullOrWhiteSpace();
    }

    [Theory]
    [InlineData("login")]
    [InlineData("whoami")]
    [InlineData("models")]
    [InlineData("quota")]
    [InlineData("test")]
    public void Build_registers_subcommand(string name)
    {
        var verbose = new Option<bool>("--verbose");
        var providerCmd = new ProviderCommand().Build(verbose, new Option<string?>("--target"));
        var copilot = providerCmd.Subcommands.Single(c => c.Name == "copilot");

        copilot.Subcommands.ShouldContain(c => c.Name == name);
    }

    [Fact]
    public void Test_subcommand_exposes_model_and_prompt_options()
    {
        var verbose = new Option<bool>("--verbose");
        var providerCmd = new ProviderCommand().Build(verbose, new Option<string?>("--target"));
        var copilot = providerCmd.Subcommands.Single(c => c.Name == "copilot");
        var test = copilot.Subcommands.Single(c => c.Name == "test");

        test.Options.ShouldContain(o => o.Name == "model");
        test.Options.ShouldContain(o => o.Name == "prompt");
    }

    [Fact]
    public void Copilot_command_exposes_target_global_option()
    {
        var verbose = new Option<bool>("--verbose");
        var targetOption = new Option<string?>("--target");
        var providerCmd = new ProviderCommand().Build(verbose, targetOption);

        // --target is now a root-level global option that all subcommands
        // (including copilot/login/whoami/models/quota/test) inherit.
        // Verify the provider command tree can parse --target without error.
        var root = new RootCommand();
        root.AddGlobalOption(targetOption);
        root.AddCommand(providerCmd);
        var result = root.Parse("provider copilot whoami --target /tmp/test");
        result.Errors.ShouldBeEmpty();
    }

    [Fact]
    public async Task Login_subcommand_invokes_setup_alias_with_github_copilot_preselected()
    {
        var verbose = new Option<bool>("--verbose");
        var captured = new List<(string ConfigPath, string Home, bool Verbose)>();
        Func<string, string, bool, CancellationToken, Task<int>> alias = (configPath, home, v, _) =>
        {
            captured.Add((configPath, home, v));
            return Task.FromResult(0);
        };

        var copilot = CopilotProviderSubcommand.Build(verbose, new Option<string?>("--target"), alias);

        // Build a root command so System.CommandLine can resolve handlers.
        var root = new RootCommand();
        root.AddCommand(copilot);
        var exit = await root.InvokeAsync(new[] { "copilot", "login" });

        exit.ShouldBe(0);
        captured.Count.ShouldBe(1);
        captured[0].ConfigPath.ShouldEndWith("config.json");
        captured[0].Home.ShouldNotBeNullOrWhiteSpace();
    }
}
