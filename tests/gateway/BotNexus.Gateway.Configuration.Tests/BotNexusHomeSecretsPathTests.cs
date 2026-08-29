using System.IO.Abstractions.TestingHelpers;
using BotNexus.Gateway.Configuration;
using Shouldly;

namespace BotNexus.Gateway.Configuration.Tests;

/// <summary>
/// Pins <see cref="BotNexusHome.SecretsPath"/> (#3528 AC1) to the same resolution rules as the other
/// home paths, so a data-dir override cannot leave the secret store behind in the config root.
/// </summary>
public sealed class BotNexusHomeSecretsPathTests
{
    [Fact]
    public void SecretsPath_sits_beside_AgentsPath_under_the_data_directory()
    {
        var home = new BotNexusHome(new MockFileSystem(), homePath: @"C:\home", dataPath: null);

        home.SecretsPath.ShouldBe(Path.Combine(home.DataPath, "secrets"));
        Path.GetDirectoryName(home.SecretsPath).ShouldBe(Path.GetDirectoryName(home.AgentsPath));
    }

    [Fact]
    public void SecretsPath_follows_the_data_directory_override_rather_than_the_config_root()
    {
        // The whole reason this is a property on BotNexusHome: a hand-built
        // Path.Combine(root, "secrets") would keep pointing at the config root here, silently
        // splitting the store across two directories the moment BOTNEXUS_DATA_DIR is configured.
        var home = new BotNexusHome(new MockFileSystem(), homePath: @"C:\config-root", dataPath: @"C:\data-dir");

        home.RootPath.ShouldNotBe(home.DataPath);
        home.SecretsPath.ShouldStartWith(home.DataPath);
        home.SecretsPath.ShouldNotStartWith(home.RootPath);
    }

    [Fact]
    public void SecretsPath_honours_an_explicit_home_override()
    {
        var home = new BotNexusHome(new MockFileSystem(), homePath: @"C:\custom-home", dataPath: null);

        home.SecretsPath.ShouldBe(Path.Combine(Path.GetFullPath(@"C:\custom-home"), "secrets"));
    }
}
