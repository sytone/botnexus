using BotNexus.Cli.Commands;
using Shouldly;
using Spectre.Console;
using System.Text.Json.Nodes;

namespace BotNexus.Cli.Tests.Commands;

[Collection("AnsiConsole")]
public sealed class DoctorConfigCommandTests : IDisposable
{
    private readonly IAnsiConsole _originalConsole;
    private readonly StringWriter _consoleOutput;

    public DoctorConfigCommandTests()
    {
        // DoctorConfig prompts via the ambient AnsiConsole. Under `dotnet test`
        // a terminal may be reported as interactive while stdin has no data, so
        // AnsiConsole.Confirm would block the test host forever (regression #2196).
        // Inject a non-interactive console so tests never prompt; the production
        // guard (Console.IsInputRedirected) covers the piped/automation path.
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
    }

    private static async Task<string> WriteTempConfigAsync(string json)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"botnexus-doctor-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "config.json");
        await File.WriteAllTextAsync(path, json);
        return path;
    }

    [Fact]
    public async Task DoctorConfig_ReturnsMissingFileError_WhenConfigAbsent()
    {
        var cmd = new DoctorConfigCommand();
        var result = await cmd.ExecuteAsync(
            "/nonexistent/path/config.json",
            autoApply: true, dryRun: false, verbose: false,
            CancellationToken.None);
        result.ShouldBe(1);
    }

    [Fact]
    public async Task DoctorConfig_ReturnsZero_WhenConfigAlreadyComplete()
    {
        var fullConfig = """
            {
              "gateway": {
                "extensions": {
                  "enabled": true,
                  "defaults": {
                    "botnexus-skills": { "enabled": true }
                  }
                }
              },
              "cron": { "enabled": true, "tickIntervalSeconds": 60 },
              "compaction": { "summarizationModel": "claude-haiku-4.5" },
              "agents": {
                "defaults": {
                  "memory": { "enabled": true, "indexing": "auto" }
                }
              }
            }
            """;
        var configPath = await WriteTempConfigAsync(fullConfig);
        try
        {
            var cmd = new DoctorConfigCommand();
            var result = await cmd.ExecuteAsync(
                configPath,
                autoApply: false, dryRun: false, verbose: false,
                CancellationToken.None);
            result.ShouldBe(0);
        }
        finally
        {
            File.Delete(configPath);
            Directory.Delete(Path.GetDirectoryName(configPath)!, recursive: true);
        }
    }

    [Fact]
    public async Task DoctorConfig_AppliesSkillsDefaultToMinimalConfig()
    {
        // Minimal config — missing extensions, skills default, and cron
        var minimal = """
            {
              "gateway": {
                "listenUrl": "http://0.0.0.0:5005"
              },
              "agents": {
                "defaults": {}
              }
            }
            """;
        var configPath = await WriteTempConfigAsync(minimal);
        try
        {
            var cmd = new DoctorConfigCommand();
            var result = await cmd.ExecuteAsync(
                configPath,
                autoApply: true, dryRun: false, verbose: false,
                CancellationToken.None);
            result.ShouldBe(0);

            var written = await File.ReadAllTextAsync(configPath);
            var root = JsonNode.Parse(written)!.AsObject();

            // skills default applied
            var skillsEnabled = root["gateway"]!["extensions"]!["defaults"]!["botnexus-skills"]!["enabled"]!.GetValue<bool>();
            skillsEnabled.ShouldBeTrue();

            // cron applied
            root["cron"]!["enabled"]!.GetValue<bool>().ShouldBeTrue();

            // memory applied
            root["agents"]!["defaults"]!["memory"]!["enabled"]!.GetValue<bool>().ShouldBeTrue();

            // existing gateway setting preserved
            written.ShouldContain("0.0.0.0");
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(configPath)!, recursive: true);
        }
    }

    [Fact]
    public async Task DoctorConfig_NonInteractive_WithApplicableCheck_DoesNotHang_AndSkips()
    {
        // Regression for #2196: a config that HAS applicable checks, run with
        // autoApply:false under a non-interactive stdin (as in `dotnet test`),
        // must NOT block on AnsiConsole.Confirm. It should skip the fixes,
        // leave the file unchanged, and return 0 promptly.
        var minimal = "{\"gateway\":{\"listenUrl\":\"http://0.0.0.0:5005\"}}";
        var configPath = await WriteTempConfigAsync(minimal);
        try
        {
            var originalContent = await File.ReadAllTextAsync(configPath);

            var cmd = new DoctorConfigCommand();
            // 20s guard: if the interactivity guard regresses this will block
            // forever, so fail fast instead of hanging the whole test host.
            var exec = cmd.ExecuteAsync(
                configPath,
                autoApply: false, dryRun: false, verbose: false,
                CancellationToken.None);
            var completed = await Task.WhenAny(exec, Task.Delay(TimeSpan.FromSeconds(20)));
            completed.ShouldBe((Task)exec, "doctor config blocked on an interactive prompt with no stdin (regression of #2196)");

            var result = await exec;
            result.ShouldBe(0);

            // Non-interactive + no --yes: nothing applied, file untouched.
            var afterContent = await File.ReadAllTextAsync(configPath);
            afterContent.ShouldBe(originalContent);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(configPath)!, recursive: true);
        }
    }

    [Fact]
    public async Task DoctorConfig_DryRun_DoesNotWriteChanges()
    {
        var minimal = "{\"gateway\":{\"listenUrl\":\"http://0.0.0.0:5005\"}}";
        var configPath = await WriteTempConfigAsync(minimal);
        try
        {
            var originalContent = await File.ReadAllTextAsync(configPath);

            var cmd = new DoctorConfigCommand();
            await cmd.ExecuteAsync(
                configPath,
                autoApply: true, dryRun: true, verbose: false,
                CancellationToken.None);

            var afterContent = await File.ReadAllTextAsync(configPath);
            afterContent.ShouldBe(originalContent);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(configPath)!, recursive: true);
        }
    }
}
