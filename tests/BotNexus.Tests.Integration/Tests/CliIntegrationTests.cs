using FluentAssertions;
using System.IO.Compression;

namespace BotNexus.Tests.Integration.Tests;

[CollectionDefinition("cli-integration", DisableParallelization = true)]
public sealed class CliIntegrationCollection;

[Collection("cli-integration")]
public sealed class CliIntegrationTests
{
    [Fact]
    [Trait("Category", "CLI")]
    public async Task Help_ShowsSubcommands_AndReturnsZero()
    {
        await using var home = await CliHomeScope.CreateAsync();
        var result = await CliTestHost.RunCliAsync("--help", home.Path);

        result.ExitCode.Should().Be(0);
        result.StdOut.Should().ContainAll("config", "agent", "provider", "extension", "doctor", "status", "logs", "install", "update");
    }

    [Fact]
    [Trait("Category", "CLI")]
    public async Task ConfigInit_CreatesConfigJson_AndReturnsZero()
    {
        await using var home = await CliHomeScope.CreateAsync();
        var result = await CliTestHost.RunCliAsync("config init", home.Path, standardInput: "\n\n\n");

        result.ExitCode.Should().Be(0);
        result.StdOut.Should().Contain("Initialized config");
        File.Exists(Path.Combine(home.Path, "config.json")).Should().BeTrue();
    }

    [Fact]
    [Trait("Category", "CLI")]
    public async Task ConfigValidate_WithValidConfig_ReturnsPassAndZero()
    {
        await using var home = await CliHomeScope.CreateAsync();
        _ = await CliTestHost.RunCliAsync("config init", home.Path, standardInput: "\n\n\n");

        var result = await CliTestHost.RunCliAsync("config validate", home.Path);

        result.ExitCode.Should().Be(0);
        result.StdOut.Should().Contain("Config").And.Contain("valid");
    }

    [Fact]
    [Trait("Category", "CLI")]
    public async Task ConfigValidate_WithInvalidConfig_ReturnsFailAndOne()
    {
        await using var home = await CliHomeScope.CreateAsync();
        await File.WriteAllTextAsync(Path.Combine(home.Path, "config.json"), "{ invalid json");

        var result = await CliTestHost.RunCliAsync("config validate", home.Path);

        result.ExitCode.Should().Be(1);
        result.StdOut.Should().Contain("Invalid JSON");
    }

    [Fact]
    [Trait("Category", "CLI")]
    public async Task ConfigShow_OutputsJson_AndReturnsZero()
    {
        await using var home = await CliHomeScope.CreateAsync();
        _ = await CliTestHost.RunCliAsync("config init", home.Path, standardInput: "\n\n\n");

        var result = await CliTestHost.RunCliAsync("config show", home.Path);

        result.ExitCode.Should().Be(0);
        result.StdOut.Should().Contain("\"BotNexus\"");
    }

    [Fact]
    [Trait("Category", "CLI")]
    public async Task AgentList_ShowsConfiguredAgentsOrEmpty_AndReturnsZero()
    {
        await using var home = await CliHomeScope.CreateAsync();
        _ = await CliTestHost.RunCliAsync("config init", home.Path, standardInput: "\n\n\n");

        var result = await CliTestHost.RunCliAsync("agent list", home.Path);

        result.ExitCode.Should().Be(0);
        result.StdOut.Should().Match(s => s.Contains("No named agents configured.") || s.Contains("name"));
    }

    [Fact]
    [Trait("Category", "CLI")]
    public async Task ProviderList_ShowsProvidersOrEmpty_AndReturnsZero()
    {
        await using var home = await CliHomeScope.CreateAsync();
        _ = await CliTestHost.RunCliAsync("config init", home.Path, standardInput: "\n\n\n");

        var result = await CliTestHost.RunCliAsync("provider list", home.Path);

        result.ExitCode.Should().Be(0);
        result.StdOut.Should().Match(s => s.Contains("No providers configured.") || s.Contains("auth type"));
    }

    [Fact]
    [Trait("Category", "CLI")]
    public async Task ExtensionList_ListsExtensionsOrEmpty_AndReturnsZero()
    {
        await using var home = await CliHomeScope.CreateAsync();
        _ = await CliTestHost.RunCliAsync("config init", home.Path, standardInput: "\n\n\n");

        var result = await CliTestHost.RunCliAsync("extension list", home.Path);

        result.ExitCode.Should().Be(0);
        result.StdOut.Should().Match(s => s.Contains("No installed extensions found.") || s.Contains("type"));
    }

    [Fact]
    [Trait("Category", "CLI")]
    public async Task Doctor_RunsCheckups_AndShowsSummary()
    {
        await using var home = await CliHomeScope.CreateAsync();
        _ = await CliTestHost.RunCliAsync("config init", home.Path, standardInput: "\n\n\n");

        var result = await CliTestHost.RunCliAsync("doctor", home.Path);

        result.ExitCode.Should().BeOneOf(0, 1);
        result.StdOut.Should().Contain("Summary:");
    }

    [Fact]
    [Trait("Category", "CLI")]
    public async Task Doctor_WithConfigurationCategory_ShowsFilteredResults()
    {
        await using var home = await CliHomeScope.CreateAsync();
        _ = await CliTestHost.RunCliAsync("config init", home.Path, standardInput: "\n\n\n");

        var result = await CliTestHost.RunCliAsync("doctor --category configuration", home.Path);

        result.ExitCode.Should().BeOneOf(0, 1);
        result.StdOut.Should().ContainAll("Configuration", "ConfigValid");
    }

    [Fact]
    [Trait("Category", "CLI")]
    public async Task Status_ShowsOffline_WhenGatewayNotRunning()
    {
        await using var home = await CliHomeScope.CreateAsync();
        var unusedPort = Random.Shared.Next(40000, 50000);
        _ = await CliTestHost.RunCliAsync("config init", home.Path, standardInput: $"\n\n{unusedPort}\n");

        var result = await CliTestHost.RunCliAsync("status", home.Path);

        result.ExitCode.Should().Be(0);
        result.StdOut.Should().ContainAll("BotNexus Status", "CLI version", "Installed version", "Gateway", "Version match");
        result.StdOut.Should().Contain("Offline");
    }

    [Fact]
    [Trait("Category", "CLI")]
    public async Task Logs_ShowsLogContentOrNoLogsMessage()
    {
        await using var home = await CliHomeScope.CreateAsync();
        var logsPath = Path.Combine(home.Path, "logs");
        Directory.CreateDirectory(logsPath);
        await File.WriteAllLinesAsync(Path.Combine(logsPath, "gateway.log"), ["line 1", "line 2", "line 3"]);

        var result = await CliTestHost.RunCliAsync("logs --lines 2", home.Path);

        result.ExitCode.Should().Be(0);
        result.StdOut.Should().ContainAll("line 2", "line 3");
    }

    [Fact]
    [Trait("Category", "CLI")]
    public async Task Install_DeploysPackages_AndSkipsCliPackage()
    {
        await using var home = await CliHomeScope.CreateAsync();
        var packagesPath = Path.Combine(home.Path, "packages");
        var installPath = Path.Combine(home.Path, "app-install");
        Directory.CreateDirectory(packagesPath);

        CreateNupkg(Path.Combine(packagesPath, "BotNexus.Gateway.nupkg"), ("lib/net10.0/BotNexus.Gateway.dll", "gateway"));
        CreateNupkg(Path.Combine(packagesPath, "BotNexus.Cli.nupkg"), ("lib/net10.0/BotNexus.Cli.dll", "cli"));
        CreateNupkg(Path.Combine(packagesPath, "BotNexus.Providers.Copilot.nupkg"), ("lib/net10.0/BotNexus.Providers.Copilot.dll", "provider"));

        var result = await CliTestHost.RunCliAsync($"install --install-path \"{installPath}\" --packages \"{packagesPath}\"", home.Path);

        result.ExitCode.Should().Be(0);
        File.Exists(Path.Combine(installPath, "gateway", "lib", "net10.0", "BotNexus.Gateway.dll")).Should().BeTrue();
        File.Exists(Path.Combine(installPath, "extensions", "providers", "copilot", "lib", "net10.0", "BotNexus.Providers.Copilot.dll")).Should().BeTrue();
        Directory.Exists(Path.Combine(installPath, "cli")).Should().BeFalse();
        File.Exists(Path.Combine(installPath, "version.json")).Should().BeTrue();
        File.ReadAllText(Path.Combine(installPath, "version.json")).Should().Contain("\"Version\"");
        File.ReadAllText(Path.Combine(home.Path, "config.json")).Should().Contain("ExtensionsPath");
    }

    private static void CreateNupkg(string packagePath, params (string EntryName, string Content)[] entries)
    {
        using var archive = ZipFile.Open(packagePath, ZipArchiveMode.Create);
        foreach (var (entryName, content) in entries)
        {
            var entry = archive.CreateEntry(entryName);
            using var writer = new StreamWriter(entry.Open());
            writer.Write(content);
        }
    }
}
