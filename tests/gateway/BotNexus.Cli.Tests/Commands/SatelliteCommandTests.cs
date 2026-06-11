using System.CommandLine;
using System.Text.Json;
using BotNexus.Cli.Commands;
using Spectre.Console;

namespace BotNexus.Cli.Tests.Commands;

[Collection("AnsiConsole")]
public sealed class SatelliteCommandTests : IDisposable
{
    private readonly string _tempHome;
    private readonly string _configPath;
    private readonly IAnsiConsole _originalConsole;
    private readonly StringWriter _consoleOutput;

    public SatelliteCommandTests()
    {
        _tempHome = Path.Combine(Path.GetTempPath(), $"botnexus-sat-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempHome);
        _configPath = Path.Combine(_tempHome, "config.json");

        _originalConsole = AnsiConsole.Console;
        _consoleOutput = new StringWriter();
        AnsiConsole.Console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Out = new AnsiConsoleOutput(_consoleOutput),
            Interactive = InteractionSupport.No
        });
    }

    public void Dispose()
    {
        AnsiConsole.Console = _originalConsole;
        _consoleOutput.Dispose();
        if (Directory.Exists(_tempHome))
            Directory.Delete(_tempHome, recursive: true);
    }

    [Fact]
    public async Task List_NoConfig_ShowsNoConfigMessage()
    {
        // Arrange — no config file exists
        var root = BuildRootCommand();

        // Act
        var exitCode = await root.InvokeAsync(new[] { "satellite", "list", "--target", _tempHome });

        // Assert
        Assert.NotEqual(0, exitCode);
        var output = _consoleOutput.ToString();
        Assert.Contains("No config.json found", output);
    }

    [Fact]
    public async Task List_EmptyConfig_ShowsNoSatellites()
    {
        // Arrange
        await File.WriteAllTextAsync(_configPath, """{"gateway": {}}""");
        var root = BuildRootCommand();

        // Act
        var exitCode = await root.InvokeAsync(new[] { "satellite", "list", "--target", _tempHome });

        // Assert
        Assert.Equal(0, exitCode);
        var output = _consoleOutput.ToString();
        Assert.Contains("No satellites registered", output);
    }

    [Fact]
    public async Task List_WithSatellites_RendersTable()
    {
        // Arrange
        await File.WriteAllTextAsync(_configPath, """
        {
            "gateway": {
                "satellites": {
                    "sat_test": {
                        "displayName": "Test Satellite",
                        "platform": "windows",
                        "apiKey": "sat_abc123",
                        "capabilities": ["notify", "canvas"],
                        "ownerUserId": "user@test.com",
                        "enabled": true
                    }
                }
            }
        }
        """);
        var root = BuildRootCommand();

        // Act
        var exitCode = await root.InvokeAsync(new[] { "satellite", "list", "--target", _tempHome });

        // Assert
        Assert.Equal(0, exitCode);
        var output = _consoleOutput.ToString();
        Assert.Contains("sat_test", output);
        Assert.Contains("windows", output);
        Assert.Contains("1 satellite(s) registered", output);
    }

    [Fact]
    public async Task Register_ValidArgs_CreatesSatelliteEntry()
    {
        // Arrange
        await File.WriteAllTextAsync(_configPath, """{"gateway": {}}""");
        var root = BuildRootCommand();

        // Act
        var exitCode = await root.InvokeAsync(new[]
        {
            "satellite", "register", "sat_home",
            "--platform", "windows",
            "--capabilities", "notify,canvas",
            "--owner", "user@test.com",
            "--target", _tempHome
        });

        // Assert
        Assert.Equal(0, exitCode);

        var json = await File.ReadAllTextAsync(_configPath);
        using var doc = JsonDocument.Parse(json);
        var satellites = doc.RootElement.GetProperty("gateway").GetProperty("satellites");
        Assert.True(satellites.TryGetProperty("sat_home", out var sat));
        Assert.Equal("windows", sat.GetProperty("platform").GetString());
        Assert.Equal("user@test.com", sat.GetProperty("ownerUserId").GetString());

        // API key must start with sat_
        var apiKey = sat.GetProperty("apiKey").GetString();
        Assert.NotNull(apiKey);
        Assert.StartsWith("sat_", apiKey);
    }

    [Fact]
    public async Task Register_InvalidPlatform_ReturnsError()
    {
        // Arrange
        await File.WriteAllTextAsync(_configPath, """{"gateway": {}}""");
        var root = BuildRootCommand();

        // Act
        var exitCode = await root.InvokeAsync(new[]
        {
            "satellite", "register", "sat_bad",
            "--platform", "android",
            "--capabilities", "notify",
            "--owner", "user@test.com",
            "--target", _tempHome
        });

        // Assert
        Assert.Equal(1, exitCode);
        var output = _consoleOutput.ToString();
        Assert.Contains("Invalid platform", output);
    }

    [Fact]
    public async Task Register_InvalidCapabilities_ReturnsError()
    {
        // Arrange
        await File.WriteAllTextAsync(_configPath, """{"gateway": {}}""");
        var root = BuildRootCommand();

        // Act
        var exitCode = await root.InvokeAsync(new[]
        {
            "satellite", "register", "sat_bad",
            "--platform", "windows",
            "--capabilities", "notify,teleport",
            "--owner", "user@test.com",
            "--target", _tempHome
        });

        // Assert
        Assert.Equal(1, exitCode);
        var output = _consoleOutput.ToString();
        Assert.Contains("Invalid capabilities", output);
    }

    [Fact]
    public async Task Register_DuplicateSatellite_ReturnsError()
    {
        // Arrange
        await File.WriteAllTextAsync(_configPath, """
        {
            "gateway": {
                "satellites": {
                    "sat_existing": {
                        "displayName": "Existing",
                        "platform": "windows",
                        "apiKey": "sat_old",
                        "capabilities": ["notify"],
                        "ownerUserId": "user@test.com",
                        "enabled": true
                    }
                }
            }
        }
        """);
        var root = BuildRootCommand();

        // Act
        var exitCode = await root.InvokeAsync(new[]
        {
            "satellite", "register", "sat_existing",
            "--platform", "windows",
            "--capabilities", "notify",
            "--owner", "user@test.com",
            "--target", _tempHome
        });

        // Assert
        Assert.NotEqual(0, exitCode);
    }

    [Fact]
    public async Task Remove_ExistingSatellite_RemovesEntry()
    {
        // Arrange
        await File.WriteAllTextAsync(_configPath, """
        {
            "gateway": {
                "satellites": {
                    "sat_remove_me": {
                        "displayName": "To Remove",
                        "platform": "linux",
                        "apiKey": "sat_xyz",
                        "capabilities": ["exec"],
                        "ownerUserId": "admin@test.com",
                        "enabled": true
                    }
                }
            }
        }
        """);
        var root = BuildRootCommand();

        // Act
        var exitCode = await root.InvokeAsync(new[] { "satellite", "remove", "sat_remove_me", "--target", _tempHome });

        // Assert
        Assert.Equal(0, exitCode);

        var json = await File.ReadAllTextAsync(_configPath);
        using var doc = JsonDocument.Parse(json);
        var satellites = doc.RootElement.GetProperty("gateway").GetProperty("satellites");
        Assert.False(satellites.TryGetProperty("sat_remove_me", out _));

        var output = _consoleOutput.ToString();
        Assert.Contains("removed", output);
    }

    [Fact]
    public async Task Remove_NonExistentSatellite_ShowsWarning()
    {
        // Arrange
        await File.WriteAllTextAsync(_configPath, """{"gateway": {"satellites": {}}}""");
        var root = BuildRootCommand();

        // Act
        var exitCode = await root.InvokeAsync(new[] { "satellite", "remove", "sat_ghost", "--target", _tempHome });

        // Assert — command succeeds with warning (exit 0 per current impl)
        var output = _consoleOutput.ToString();
        Assert.Contains("not found", output);
    }

    [Fact]
    public async Task Remove_NoConfigFile_ReturnsError()
    {
        // Arrange — no config file
        var root = BuildRootCommand();

        // Act
        var exitCode = await root.InvokeAsync(new[] { "satellite", "remove", "sat_ghost", "--target", _tempHome });

        // Assert
        Assert.Equal(1, exitCode);
        var output = _consoleOutput.ToString();
        Assert.Contains("No config.json found", output);
    }

    private static RootCommand BuildRootCommand()
    {
        var verboseOption = new Option<bool>("--verbose");
        var targetOption = new Option<string?>("--target");
        var root = new RootCommand();
        root.AddGlobalOption(verboseOption);
        root.AddGlobalOption(targetOption);
        var satCmd = new SatelliteCommand();
        root.AddCommand(satCmd.Build(verboseOption, targetOption));
        return root;
    }
}
