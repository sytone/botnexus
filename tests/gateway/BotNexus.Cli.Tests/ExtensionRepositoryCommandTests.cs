using BotNexus.Cli;
using System.CommandLine;

namespace BotNexus.Cli.Tests;

/// <summary>
/// Pins the CLI surface for the configuration-only extension repository slice (#3899).
/// </summary>
public sealed class ExtensionRepositoryCommandTests
{
    [Fact]
    public void RootCommand_RegistersExtensionsLifecycleCommands()
    {
        var root = CliApp.CreateRootCommandForTesting();

        var extensions = root.Subcommands.Single(command => command.Name == "extensions");
        extensions.Subcommands.Select(command => command.Name).OrderBy(name => name, StringComparer.Ordinal)
            .ShouldBe(["add", "disable", "enable", "list", "remove", "update"]);
    }

    [Theory]
    [InlineData("extensions add --id community-tools --url https://example.test/tools.git --ref main --target instance")]
    [InlineData("extensions list --target instance")]
    [InlineData("extensions update --id community-tools --url ssh://git@example.test/tools.git --ref stable --no-updates --target instance")]
    [InlineData("extensions enable --id community-tools --target instance")]
    [InlineData("extensions disable --id community-tools --target instance")]
    [InlineData("extensions remove --id community-tools --target instance")]
    public void ExtensionsCommand_AcceptsDocumentedArgumentsAndGlobalTarget(string commandLine)
    {
        var result = CliApp.CreateRootCommandForTesting().Parse(commandLine);

        result.Errors.ShouldBeEmpty();
    }

    [Fact]
    public void ExtensionsCommand_UsesThePublicConfigurationRegistryBoundary()
    {
        typeof(BotNexus.Cli.Commands.ExtensionRepositoryCommand).Assembly.ShouldBe(typeof(CliApp).Assembly);
        typeof(BotNexus.Gateway.Configuration.ExtensionRepositoryRegistryService).IsPublic.ShouldBeTrue();
        typeof(BotNexus.Gateway.Configuration.ExtensionRepositoryRegistryService).Assembly
            .ShouldBe(typeof(BotNexus.Gateway.Configuration.PlatformConfig).Assembly);
    }

}
