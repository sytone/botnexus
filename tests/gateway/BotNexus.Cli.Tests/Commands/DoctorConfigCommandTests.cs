using BotNexus.Cli.Commands;
using BotNexus.Cli.Commands.Doctor;
using BotNexus.Gateway.Configuration;
using BotNexus.Gateway.Configuration.Store;
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
                "extensionLoader": { "enabled": true },
                "compaction": { "summarizationModel": "claude-haiku-4.5" }
              },
              "cron": { "enabled": true, "tickIntervalSeconds": 60 },
              "agents": {
                "defaults": {
                  "memory": { "enabled": true, "indexing": "auto" },
                  "extensions": { "botnexus-skills": { "enabled": true } }
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
            var skillsEnabled = root["agents"]!["defaults"]!["extensions"]!["botnexus-skills"]!["enabled"]!.GetValue<bool>();
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
    [Fact]
    public async Task DoctorConfig_LegacyExtensions_DryRunPreviewsWithoutWritingJson()
    {
        var configPath = await WriteTempConfigAsync(LegacyExtensionsJson);
        try
        {
            var before = await File.ReadAllTextAsync(configPath);

            var result = await new DoctorConfigCommand().ExecuteAsync(
                configPath, autoApply: true, dryRun: true, verbose: false, CancellationToken.None);

            result.ShouldBe(0);
            (await File.ReadAllTextAsync(configPath)).ShouldBe(before);
            _consoleOutput.ToString().ShouldContain("legacy-gateway-extensions");
            _consoleOutput.ToString().ShouldContain("would apply");
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(configPath)!, recursive: true);
        }
    }

    [Fact]
    public async Task DoctorConfig_LegacyExtensions_YesWritesCanonicalJsonShape()
    {
        var configPath = await WriteTempConfigAsync(LegacyExtensionsJson);
        try
        {
            await new DoctorConfigCommand().ExecuteAsync(
                configPath, autoApply: true, dryRun: false, verbose: false, CancellationToken.None);

            var written = await File.ReadAllTextAsync(configPath);
            written.ShouldContain("legacy-only");
            written.ShouldContain("canonicalUnknown");
            written.ShouldContain("\"token\": null");
            written.ShouldNotContain("\"defaults\": {\n        \"legacy-only\"");
            var document = ConfigDocument.Parse(written);
            LegacyGatewayExtensionsMigration.IsApplicable(document).ShouldBeFalse();
            document.TryGetString("gateway.extensionLoader.path", out var loaderPath).ShouldBeTrue();
            loaderPath.ShouldBe("legacy/extensions");
            document.GetBool("gateway.extensionLoader.enabled").ShouldBeTrue(
                "the legacy migration preserves false, then the separately accepted extensions-block doctor check enables the loader");
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(configPath)!, recursive: true);
        }
    }

    [Fact]
    public async Task DoctorConfig_StoreOnlyLegacyExtensions_DryRunDoesNotCreateJsonOrChangeStore()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"botnexus-doctor-store-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var configPath = Path.Combine(directory, "config.json");
        var storePath = Path.Combine(directory, ConfigStoreBootstrap.StoreFileName);
        var store = new BotNexus.Gateway.Configuration.Store.SqliteConfigStore($"Data Source={storePath}");
        await store.WriteDocumentAsync(JsonNode.Parse(LegacyExtensionsJson)!.AsObject());
        var before = ConfigDocumentRehydrator.Rehydrate(await store.ReadEntriesAsync()).ToJsonString();
        try
        {
            var result = await new DoctorConfigCommand().ExecuteAsync(
                configPath, autoApply: true, dryRun: true, verbose: false, CancellationToken.None);

            result.ShouldBe(0);
            File.Exists(configPath).ShouldBeFalse();
            ConfigDocumentRehydrator.Rehydrate(await store.ReadEntriesAsync()).ToJsonString().ShouldBe(before);
        }
        finally
        {
            ConfigStoreBootstrap.ReleaseConnections(storePath);
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task DoctorConfig_StoreOnlyLegacyExtensions_YesWritesCanonicalStoreAndJsonMirror()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"botnexus-doctor-store-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var configPath = Path.Combine(directory, "config.json");
        var storePath = Path.Combine(directory, ConfigStoreBootstrap.StoreFileName);
        var store = new BotNexus.Gateway.Configuration.Store.SqliteConfigStore($"Data Source={storePath}");
        await store.WriteDocumentAsync(JsonNode.Parse(LegacyExtensionsJson)!.AsObject());
        try
        {
            var result = await new DoctorConfigCommand().ExecuteAsync(
                configPath, autoApply: true, dryRun: false, verbose: false, CancellationToken.None);

            result.ShouldBe(0);
            File.Exists(configPath).ShouldBeTrue();
            var stored = ConfigDocument.Parse(ConfigDocumentRehydrator.Rehydrate(await store.ReadEntriesAsync()).ToJsonString());
            LegacyGatewayExtensionsMigration.IsApplicable(stored).ShouldBeFalse();
            stored.TryGetString("gateway.extensionLoader.path", out var loaderPath).ShouldBeTrue();
            loaderPath.ShouldBe("legacy/extensions");
            stored.GetBool("gateway.extensionLoader.enabled").ShouldBeTrue(
                "the legacy migration preserves false, then the separately accepted extensions-block doctor check enables the loader");
            stored.ToJsonString().ShouldContain("canonicalUnknown");
            stored.ToJsonString().ShouldContain("\"token\": null");
        }
        finally
        {
            ConfigStoreBootstrap.ReleaseConnections(storePath);
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DoctorConfig_UnsafeLegacyMigration_ReturnsNonzeroAndDoesNotWrite(bool dryRun)
    {
        const string unsafeJson = """
            {
              "gateway": {
                "extensionLoader": null,
                "extensions": { "path": "legacy/extensions" }
              }
            }
            """;
        var configPath = await WriteTempConfigAsync(unsafeJson);
        try
        {
            var before = await File.ReadAllTextAsync(configPath);

            var result = await new DoctorConfigCommand().ExecuteAsync(
                configPath, autoApply: true, dryRun, verbose: false, CancellationToken.None);

            result.ShouldBe(1);
            (await File.ReadAllTextAsync(configPath)).ShouldBe(before);
            _consoleOutput.ToString().ShouldContain("gateway.extensionLoader");
            _consoleOutput.ToString().ShouldContain("not modified");
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(configPath)!, recursive: true);
        }
    }

    [Fact]
    public void ApplyAcceptedChecks_ReplaysOnlyAcceptedChecksAndRechecksApplicability()
    {
        var document = ConfigDocument.Parse("{}");
        var accepted = new PathSettingCheck("accepted", "gateway.extensionLoader.enabled");
        var rejected = new PathSettingCheck("rejected", "cron.enabled");

        DoctorConfigCommand.ApplyAcceptedChecks(document, [accepted]);

        document.GetBool("gateway.extensionLoader.enabled").ShouldBeTrue();
        document.GetBool("cron.enabled").ShouldBeNull();
        accepted.ApplyCount.ShouldBe(1);
        rejected.ApplyCount.ShouldBe(0);

        DoctorConfigCommand.ApplyAcceptedChecks(document, [accepted]);
        accepted.ApplyCount.ShouldBe(1);
    }

    private sealed class PathSettingCheck(string id, string path) : IConfigCheck
    {
        public string Id => id;
        public string Description => id;
        public string FixDescription => id;
        public int ApplyCount { get; private set; }
        public bool IsApplicable(ConfigDocument config) => config.GetBool(path) is not true;
        public void Apply(ConfigDocument config)
        {
            ApplyCount++;
            config.Set(path, true);
        }
    }

    private const string LegacyExtensionsJson = """
        {
          "gateway": {
            "extensions": {
              "path": "legacy/extensions",
              "enabled": false,
              "defaults": {
                "canonical": { "enabled": false, "legacy": "loses" },
                "legacy-only": { "token": null }
              }
            }
          },
          "agents": {
            "defaults": {
              "extensions": {
                "canonical": { "enabled": true, "canonicalUnknown": 9 }
              }
            }
          }
        }
        """;

}
