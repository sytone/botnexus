using System.Diagnostics;
using FluentAssertions;

namespace BotNexus.Tests.Integration.Tests;

[CollectionDefinition("cli-integration", DisableParallelization = true)]
public sealed class CliIntegrationCollection;

[Collection("cli-integration")]
public sealed class CliIntegrationTests
{
    private static readonly string RepoRoot = FindRepoRoot();

    [Fact]
    [Trait("Category", "CLI")]
    public async Task Help_ShowsSubcommands_AndReturnsZero()
    {
        await using var home = await CliHomeScope.CreateAsync();
        var result = await RunCliAsync("--help", home.Path);

        result.ExitCode.Should().Be(0);
        result.StdOut.Should().ContainAll("config", "agent", "provider", "extension", "doctor", "status", "logs");
    }

    [Fact]
    [Trait("Category", "CLI")]
    public async Task ConfigInit_CreatesConfigJson_AndReturnsZero()
    {
        await using var home = await CliHomeScope.CreateAsync();
        var result = await RunCliAsync("config init", home.Path, standardInput: "\n\n\n");

        result.ExitCode.Should().Be(0);
        result.StdOut.Should().Contain("Initialized config");
        File.Exists(Path.Combine(home.Path, "config.json")).Should().BeTrue();
    }

    [Fact]
    [Trait("Category", "CLI")]
    public async Task ConfigValidate_WithValidConfig_ReturnsPassAndZero()
    {
        await using var home = await CliHomeScope.CreateAsync();
        _ = await RunCliAsync("config init", home.Path, standardInput: "\n\n\n");

        var result = await RunCliAsync("config validate", home.Path);

        result.ExitCode.Should().Be(0);
        result.StdOut.Should().Contain("Config is valid JSON and binds to BotNexusConfig");
    }

    [Fact]
    [Trait("Category", "CLI")]
    public async Task ConfigValidate_WithInvalidConfig_ReturnsFailAndOne()
    {
        await using var home = await CliHomeScope.CreateAsync();
        await File.WriteAllTextAsync(Path.Combine(home.Path, "config.json"), "{ invalid json");

        var result = await RunCliAsync("config validate", home.Path);

        result.ExitCode.Should().Be(1);
        result.StdOut.Should().Contain("Invalid JSON");
    }

    [Fact]
    [Trait("Category", "CLI")]
    public async Task ConfigShow_OutputsJson_AndReturnsZero()
    {
        await using var home = await CliHomeScope.CreateAsync();
        _ = await RunCliAsync("config init", home.Path, standardInput: "\n\n\n");

        var result = await RunCliAsync("config show", home.Path);

        result.ExitCode.Should().Be(0);
        result.StdOut.Should().Contain("\"BotNexus\"");
    }

    [Fact]
    [Trait("Category", "CLI")]
    public async Task AgentList_ShowsConfiguredAgentsOrEmpty_AndReturnsZero()
    {
        await using var home = await CliHomeScope.CreateAsync();
        _ = await RunCliAsync("config init", home.Path, standardInput: "\n\n\n");

        var result = await RunCliAsync("agent list", home.Path);

        result.ExitCode.Should().Be(0);
        result.StdOut.Should().Match(s => s.Contains("No named agents configured.") || s.Contains("name"));
    }

    [Fact]
    [Trait("Category", "CLI")]
    public async Task ProviderList_ShowsProvidersOrEmpty_AndReturnsZero()
    {
        await using var home = await CliHomeScope.CreateAsync();
        _ = await RunCliAsync("config init", home.Path, standardInput: "\n\n\n");

        var result = await RunCliAsync("provider list", home.Path);

        result.ExitCode.Should().Be(0);
        result.StdOut.Should().Match(s => s.Contains("No providers configured.") || s.Contains("auth type"));
    }

    [Fact]
    [Trait("Category", "CLI")]
    public async Task ExtensionList_ListsExtensionsOrEmpty_AndReturnsZero()
    {
        await using var home = await CliHomeScope.CreateAsync();
        _ = await RunCliAsync("config init", home.Path, standardInput: "\n\n\n");

        var result = await RunCliAsync("extension list", home.Path);

        result.ExitCode.Should().Be(0);
        result.StdOut.Should().Match(s => s.Contains("No installed extensions found.") || s.Contains("type"));
    }

    [Fact]
    [Trait("Category", "CLI")]
    public async Task Doctor_RunsCheckups_AndShowsSummary()
    {
        await using var home = await CliHomeScope.CreateAsync();
        _ = await RunCliAsync("config init", home.Path, standardInput: "\n\n\n");

        var result = await RunCliAsync("doctor", home.Path);

        result.ExitCode.Should().BeOneOf(0, 1);
        result.StdOut.Should().Contain("Summary:");
    }

    [Fact]
    [Trait("Category", "CLI")]
    public async Task Doctor_WithConfigurationCategory_ShowsFilteredResults()
    {
        await using var home = await CliHomeScope.CreateAsync();
        _ = await RunCliAsync("config init", home.Path, standardInput: "\n\n\n");

        var result = await RunCliAsync("doctor --category configuration", home.Path);

        result.ExitCode.Should().BeOneOf(0, 1);
        result.StdOut.Should().Contain("Configuration/ConfigValid");
    }

    [Fact]
    [Trait("Category", "CLI")]
    public async Task Status_ShowsOffline_WhenGatewayNotRunning()
    {
        await using var home = await CliHomeScope.CreateAsync();
        _ = await RunCliAsync("config init", home.Path, standardInput: "\n\n\n");

        var result = await RunCliAsync("status", home.Path);

        result.ExitCode.Should().Be(0);
        result.StdOut.Should().Contain("Gateway offline");
    }

    [Fact]
    [Trait("Category", "CLI")]
    public async Task Logs_ShowsLogContentOrNoLogsMessage()
    {
        await using var home = await CliHomeScope.CreateAsync();
        var logsPath = Path.Combine(home.Path, "logs");
        Directory.CreateDirectory(logsPath);
        await File.WriteAllLinesAsync(Path.Combine(logsPath, "gateway.log"), ["line 1", "line 2", "line 3"]);

        var result = await RunCliAsync("logs --lines 2", home.Path);

        result.ExitCode.Should().Be(0);
        result.StdOut.Should().ContainAll("line 2", "line 3");
    }

    private static async Task<CliRunResult> RunCliAsync(string command, string homePath, string? standardInput = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"run --project src\\BotNexus.Cli -- {command} --home \"{homePath}\"",
            WorkingDirectory = RepoRoot,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start CLI process.");

        if (!string.IsNullOrEmpty(standardInput))
            await process.StandardInput.WriteAsync(standardInput);

        process.StandardInput.Close();

        var stdOutTask = process.StandardOutput.ReadToEndAsync();
        var stdErrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        return new CliRunResult(process.ExitCode, await stdOutTask, await stdErrTask);
    }

    private static string FindRepoRoot()
    {
        var directory = AppContext.BaseDirectory;
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory, "BotNexus.slnx")))
                return directory;

            directory = Directory.GetParent(directory)?.FullName;
        }

        throw new InvalidOperationException("Cannot find repository root (BotNexus.slnx not found).");
    }

    private sealed record CliRunResult(int ExitCode, string StdOut, string StdErr);

    private sealed class CliHomeScope : IAsyncDisposable
    {
        private CliHomeScope(string path) => Path = path;

        public string Path { get; }

        public static Task<CliHomeScope> CreateAsync()
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "botnexus-cli-int", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return Task.FromResult(new CliHomeScope(path));
        }

        public ValueTask DisposeAsync()
        {
            if (Directory.Exists(Path))
            {
                try
                {
                    Directory.Delete(Path, recursive: true);
                }
                catch
                {
                    // best-effort cleanup
                }
            }

            return ValueTask.CompletedTask;
        }
    }
}
