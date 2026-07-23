using System.Text.Json;
using System.Text.Json.Nodes;
using System.IO.Abstractions;
using BotNexus.Gateway.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BotNexus.Gateway.Tests.Configuration;

public class ConfigHydrationServiceTests
{
    [Fact]
    public void MergeAtPath_AddsTopLevelSection_WhenMissing()
    {
        var root = new JsonObject();
        var defaults = new JsonObject { ["enabled"] = true, ["interval"] = 60 };

        var added = ConfigHydrationService.MergeAtPath(root, "cron", defaults);

        Assert.Equal(2, added);
        Assert.True(root["cron"]!["enabled"]!.GetValue<bool>());
        Assert.Equal(60, root["cron"]!["interval"]!.GetValue<int>());
    }

    [Fact]
    public void MergeAtPath_AddsNestedSection_WhenMissing()
    {
        var root = new JsonObject { ["gateway"] = new JsonObject() };
        var defaults = new JsonObject { ["timeoutSeconds"] = 90 };

        var added = ConfigHydrationService.MergeAtPath(root, "gateway.compaction", defaults);

        Assert.Equal(1, added);
        Assert.Equal(90, root["gateway"]!["compaction"]!["timeoutSeconds"]!.GetValue<int>());
    }

    [Fact]
    public void MergeAtPath_CreatesIntermediateObjects_WhenPathDoesNotExist()
    {
        var root = new JsonObject();
        var defaults = new JsonObject { ["model"] = "gpt-4.1" };

        var added = ConfigHydrationService.MergeAtPath(root, "gateway.auxiliary.titling", defaults);

        Assert.Equal(1, added);
        Assert.Equal("gpt-4.1", root["gateway"]!["auxiliary"]!["titling"]!["model"]!.GetValue<string>());
    }

    [Fact]
    public void MergeAtPath_DoesNotOverwrite_WhenPathIsScalar()
    {
        var root = new JsonObject { ["gateway"] = JsonValue.Create("custom-value") };
        var defaults = new JsonObject { ["listenUrl"] = "http://localhost:5005" };

        var added = ConfigHydrationService.MergeAtPath(root, "gateway", defaults);

        Assert.Equal(0, added);
        Assert.Equal("custom-value", root["gateway"]!.GetValue<string>());
    }

    [Fact]
    public void DeepMergeDefaults_PreservesExistingValues()
    {
        var target = new JsonObject
        {
            ["enabled"] = true,
            ["requestsPerMinute"] = 100
        };
        var defaults = new JsonObject
        {
            ["enabled"] = false,
            ["requestsPerMinute"] = 300,
            ["windowSeconds"] = 60
        };

        var added = ConfigHydrationService.DeepMergeDefaults(target, defaults);

        Assert.Equal(1, added);
        Assert.True(target["enabled"]!.GetValue<bool>());
        Assert.Equal(100, target["requestsPerMinute"]!.GetValue<int>());
        Assert.Equal(60, target["windowSeconds"]!.GetValue<int>());
    }

    [Fact]
    public void DeepMergeDefaults_AddsKey_WhenTargetHasNullAssignment()
    {
        // JsonObject["key"] = null removes the key, so it's treated as missing
        var target = new JsonObject { ["timeout"] = 30 };
        var defaults = new JsonObject { ["model"] = "gpt-4.1", ["timeout"] = 60 };

        var added = ConfigHydrationService.DeepMergeDefaults(target, defaults);

        Assert.Equal(1, added); // model added
        Assert.Equal("gpt-4.1", target["model"]!.GetValue<string>());
        Assert.Equal(30, target["timeout"]!.GetValue<int>()); // preserved
    }

    [Fact]
    public void Hydration_UpgradesOnDiskTitlingModelNull_ToContributorDefault()
    {
        // #1994 seam: an existing deployment has titling.model persisted as a JSON null (the shape
        // the old contributor hydrated). Parse it from an actual on-disk JSON string — NOT a C#
        // null — so this exercises the real parsed-JSON-null path, then merge the LIVE
        // AuxiliarySchemaContributor defaults over it exactly as ConfigHydrationService does on
        // startup. The null model must self-heal to the contributor's default model so operators
        // don't have to hand-edit config.json.
        var root = JsonNode.Parse(
            """
            { "gateway": { "auxiliary": { "titling": { "model": null, "timeoutSeconds": 30, "enabled": true } } } }
            """)!.AsObject();

        var defaults = JsonSerializer.SerializeToNode(
            new AuxiliarySchemaContributor().GetDefaults(),
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })!.AsObject();

        ConfigHydrationService.MergeAtPath(root, "gateway.auxiliary", defaults);

        var model = root["gateway"]!["auxiliary"]!["titling"]!["model"];
        model.ShouldNotBeNull("a persisted titling.model:null must self-heal to the default titling model on startup");
        model!.GetValue<string>().ShouldBe("gpt-5.6-luna");
        // Operator's other values are preserved, never clobbered.
        root["gateway"]!["auxiliary"]!["titling"]!["timeoutSeconds"]!.GetValue<int>().ShouldBe(30);
        root["gateway"]!["auxiliary"]!["titling"]!["enabled"]!.GetValue<bool>().ShouldBeTrue();
    }

    [Fact]
    public void Hydration_PreservesOperatorChosenTitlingModel_DoesNotClobber()
    {
        // The self-heal must only fill a missing/null model. An operator who set a real model keeps it.
        var root = JsonNode.Parse(
            """
            { "gateway": { "auxiliary": { "titling": { "model": "my-cheap-model", "timeoutSeconds": 30 } } } }
            """)!.AsObject();

        var defaults = JsonSerializer.SerializeToNode(
            new AuxiliarySchemaContributor().GetDefaults(),
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })!.AsObject();

        ConfigHydrationService.MergeAtPath(root, "gateway.auxiliary", defaults);

        root["gateway"]!["auxiliary"]!["titling"]!["model"]!.GetValue<string>().ShouldBe("my-cheap-model");
    }

    [Fact]
    public void DeepMergeDefaults_RecursesIntoNestedObjects()
    {
        var target = new JsonObject
        {
            ["compaction"] = new JsonObject { ["preservedTurns"] = 5 }
        };
        var defaults = new JsonObject
        {
            ["compaction"] = new JsonObject
            {
                ["preservedTurns"] = 3,
                ["timeoutSeconds"] = 90
            }
        };

        var added = ConfigHydrationService.DeepMergeDefaults(target, defaults);

        Assert.Equal(1, added);
        Assert.Equal(5, target["compaction"]!["preservedTurns"]!.GetValue<int>());
        Assert.Equal(90, target["compaction"]!["timeoutSeconds"]!.GetValue<int>());
    }

    [Fact]
    public void DeepMergeDefaults_PreservesArrayValues_DoesNotMergeElements()
    {
        var target = new JsonObject
        {
            ["allowedOrigins"] = new JsonArray("http://custom:3000")
        };
        var defaults = new JsonObject
        {
            ["allowedOrigins"] = new JsonArray("http://localhost:5005"),
            ["enabled"] = true
        };

        var added = ConfigHydrationService.DeepMergeDefaults(target, defaults);

        Assert.Equal(1, added); // only "enabled" added
        var origins = target["allowedOrigins"]!.AsArray();
        Assert.Single(origins);
        Assert.Equal("http://custom:3000", origins[0]!.GetValue<string>());
    }

    [Fact]
    public void DeepMergeDefaults_EmptyTarget_GetsAllDefaults()
    {
        var target = new JsonObject();
        var defaults = new JsonObject
        {
            ["a"] = 1,
            ["b"] = "two",
            ["c"] = new JsonObject { ["nested"] = true }
        };

        var added = ConfigHydrationService.DeepMergeDefaults(target, defaults);

        Assert.Equal(3, added);
        Assert.Equal(1, target["a"]!.GetValue<int>());
        Assert.Equal("two", target["b"]!.GetValue<string>());
        Assert.True(target["c"]!["nested"]!.GetValue<bool>());
    }

    [Fact]
    public void GatewaySchemaContributor_ReturnsExpectedDefaults()
    {
        var contributor = new GatewaySchemaContributor();

        Assert.Equal("gateway", contributor.SectionPath);
        Assert.NotNull(contributor.GetDefaults());
    }

    [Fact]
    public void CompactionSchemaContributor_ReturnsExpectedDefaults()
    {
        var contributor = new CompactionSchemaContributor();

        Assert.Equal("gateway.compaction", contributor.SectionPath);
        Assert.NotNull(contributor.GetDefaults());
    }

    [Fact]
    public void CronSchemaContributor_ReturnsExpectedDefaults()
    {
        var contributor = new CronSchemaContributor();

        Assert.Equal("cron", contributor.SectionPath);
        Assert.NotNull(contributor.GetDefaults());
    }

    // --- Startup resilience (Docker / misconfiguration hardening) ---

    [Fact]
    public async Task StartAsync_DoesNotThrow_WhenConfigJsonIsMalformed()
    {
        // A malformed config.json must not crash the host. ConfigHydrationService runs as a
        // hosted service on startup; an unhandled JsonException here would take the gateway down.
        var dir = Path.Combine(Path.GetTempPath(), "botnexus-hydration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var configPath = Path.Combine(dir, "config.json");
        await File.WriteAllTextAsync(configPath, "this is not json at all {{{");
        try
        {
            var writer = new PlatformConfigWriter(configPath, new FileSystem());
            var service = new ConfigHydrationService(
                writer,
                [new GatewaySchemaContributor()],
                NullLogger<ConfigHydrationService>.Instance);

            // Should swallow the JsonException and return without throwing.
            var ex = await Record.ExceptionAsync(() => service.StartAsync(CancellationToken.None));
            Assert.Null(ex);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task StartAsync_NoOps_WhenNoContributors()
    {
        var dir = Path.Combine(Path.GetTempPath(), "botnexus-hydration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var configPath = Path.Combine(dir, "config.json");
        await File.WriteAllTextAsync(configPath, "{\"version\":1}");
        try
        {
            var writer = new PlatformConfigWriter(configPath, new FileSystem());
            var service = new ConfigHydrationService(writer, [], NullLogger<ConfigHydrationService>.Instance);

            var ex = await Record.ExceptionAsync(() => service.StartAsync(CancellationToken.None));
            Assert.Null(ex);
            // File untouched when there are no contributors.
            Assert.Equal("{\"version\":1}", await File.ReadAllTextAsync(configPath));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // --- Issue #2114: hydration must not rewrite config.json when nothing is missing ---

    [Fact]
    public async Task StartAsync_WithZeroMissingKeys_DoesNotRewriteFileOrCreateBackup()
    {
        var dir = Path.Combine(Path.GetTempPath(), "botnexus-hydration-noop-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var configPath = Path.Combine(dir, "config.json");
        var backupsDir = Path.Combine(dir, "backups");
        try
        {
            var contributor = new GatewaySchemaContributor();
            // Pre-populate the file with the full contributor defaults so hydration adds nothing.
            var defaults = JsonSerializer.SerializeToNode(
                contributor.GetDefaults(),
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })!.AsObject();
            var seeded = new JsonObject { ["gateway"] = defaults };
            var seededJson = seeded.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(configPath, seededJson);

            var fileSystem = new FileSystem();
            var backup = new ConfigBackupService(backupsDir, fileSystem);
            var writer = new PlatformConfigWriter(configPath, fileSystem, backup);
            var service = new ConfigHydrationService(writer, [contributor], NullLogger<ConfigHydrationService>.Instance);

            var before = File.GetLastWriteTimeUtc(configPath);
            var beforeBytes = await File.ReadAllBytesAsync(configPath);
            await Task.Delay(20);

            await service.StartAsync(CancellationToken.None);

            var after = File.GetLastWriteTimeUtc(configPath);
            var afterBytes = await File.ReadAllBytesAsync(configPath);

            after.ShouldBe(before, "Hydration with zero missing keys must not rewrite config.json (identity preserved).");
            afterBytes.ShouldBe(beforeBytes);
            (Directory.Exists(backupsDir) && Directory.GetFiles(backupsDir, "config-*.json").Length > 0)
                .ShouldBeFalse("A no-op hydration must not create a backup.");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

}
