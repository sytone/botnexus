using BotNexus.Cli.Commands;
using Shouldly;
using System.Text.Json.Nodes;

namespace BotNexus.Cli.Tests.Commands;

public sealed class DoctorConfigCommandTests
{
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
