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

    [Theory]
    [InlineData(null, "[dim]unknown[/]")]
    [InlineData(false, "no")]
    [InlineData(true, "[green]yes[/]")]
    public void FormatPremium_preserves_provider_presence(bool? value, string expected)
    {
        CopilotProviderSubcommand.FormatPremium(value).ShouldBe(expected);
    }

    [Theory]
    [InlineData(null, "[dim]unknown[/]")]
    [InlineData(0d, "0×")]
    [InlineData(1.5d, "1.5×")]
    public void FormatMultiplier_preserves_provider_presence(double? value, string expected)
    {
        CopilotProviderSubcommand.FormatMultiplier(value).ShouldBe(expected);
    }

    [Fact]
    public async Task Login_subcommand_defaults_to_canonical_github_copilot_instance()
    {
        var verbose = new Option<bool>("--verbose");
        var captured = new List<(string ConfigPath, string Home, bool Verbose, string Instance)>();
        Func<string, string, bool, string, CancellationToken, Task<int>> alias = (configPath, home, v, instance, _) =>
        {
            captured.Add((configPath, home, v, instance));
            return Task.FromResult(0);
        };

        var copilot = CopilotProviderSubcommand.Build(verbose, new Option<string?>("--target"), alias);

        var root = new RootCommand();
        root.AddCommand(copilot);
        var exit = await root.InvokeAsync(new[] { "copilot", "login" });

        exit.ShouldBe(0);
        captured.Count.ShouldBe(1);
        captured[0].ConfigPath.ShouldEndWith("config.json");
        captured[0].Home.ShouldNotBeNullOrWhiteSpace();
        captured[0].Instance.ShouldBe("github-copilot");
    }

    [Fact]
    public async Task Login_subcommand_passes_selected_named_instance_to_setup()
    {
        var verbose = new Option<bool>("--verbose");
        string? capturedInstance = null;
        Func<string, string, bool, string, CancellationToken, Task<int>> alias = (_, _, _, instance, _) =>
        {
            capturedInstance = instance;
            return Task.FromResult(0);
        };

        var copilot = CopilotProviderSubcommand.Build(verbose, new Option<string?>("--target"), alias);
        var root = new RootCommand();
        root.AddCommand(copilot);

        var exit = await root.InvokeAsync(new[] { "copilot", "login", "--instance", "copilot-work" });

        exit.ShouldBe(0);
        capturedInstance.ShouldBe("copilot-work");
    }

    [Theory]
    [InlineData("login")]
    [InlineData("whoami")]
    [InlineData("models")]
    [InlineData("quota")]
    [InlineData("test")]
    public void Copilot_subcommands_accept_named_instance(string subcommand)
    {
        var verbose = new Option<bool>("--verbose");
        Func<string, string, bool, string, CancellationToken, Task<int>> alias = (_, _, _, _, _) => Task.FromResult(0);
        var copilot = CopilotProviderSubcommand.Build(verbose, new Option<string?>("--target"), alias);
        var root = new RootCommand();
        root.AddCommand(copilot);

        var result = root.Parse($"copilot {subcommand} --instance copilot-work");

        result.Errors.ShouldBeEmpty();
    }
}
