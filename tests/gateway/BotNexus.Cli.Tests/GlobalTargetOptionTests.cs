using System.CommandLine;
using BotNexus.Cli;
using BotNexus.Cli.Commands;
using Shouldly;

namespace BotNexus.Cli.Tests;

public sealed class GlobalTargetOptionTests
{
    [Fact]
    public void Root_command_has_global_target_option()
    {
        // Build a minimal root with just the validate command to verify structure.
        var verbose = new Option<bool>("--verbose");
        var target = new Option<string?>("--target");
        var root = new RootCommand("test");
        root.AddGlobalOption(verbose);
        root.AddGlobalOption(target);
        root.AddCommand(new ValidateCommand().Build(verbose, target));

        // Global options are inherited by all subcommands.
        var result = root.Parse("validate --target /tmp/my-instance");
        result.Errors.ShouldBeEmpty();
        result.GetValueForOption(target).ShouldBe("/tmp/my-instance");
    }

    [Fact]
    public void Target_option_is_accessible_from_nested_subcommands()
    {
        var verbose = new Option<bool>("--verbose");
        var target = new Option<string?>("--target");
        var root = new RootCommand("test");
        root.AddGlobalOption(verbose);
        root.AddGlobalOption(target);
        root.AddCommand(new AgentCommands().Build(verbose, target));

        // Verify --target works on nested subcommands (agent list).
        var result = root.Parse("agent list --target /tmp/dev");
        result.Errors.ShouldBeEmpty();
        result.GetValueForOption(target).ShouldBe("/tmp/dev");
    }

    [Fact]
    public void Target_option_defaults_to_null_when_not_specified()
    {
        var verbose = new Option<bool>("--verbose");
        var target = new Option<string?>("--target");
        var root = new RootCommand("test");
        root.AddGlobalOption(verbose);
        root.AddGlobalOption(target);
        root.AddCommand(new ValidateCommand().Build(verbose, target));

        var result = root.Parse("validate");
        result.Errors.ShouldBeEmpty();
        result.GetValueForOption(target).ShouldBeNull();
    }

    [Fact]
    public void CliPaths_ResolveTarget_uses_explicit_path_over_default()
    {
        var resolved = CliPaths.ResolveTarget("/custom/path");
        resolved.ShouldBe("/custom/path");
    }

    [Fact]
    public void CliPaths_ResolveTarget_falls_back_to_default_when_null()
    {
        var resolved = CliPaths.ResolveTarget(null);
        // Returns BOTNEXUS_HOME env var or ~/.botnexus — not null.
        resolved.ShouldNotBeNullOrWhiteSpace();
    }
}
