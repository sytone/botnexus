using System.Text.Json.Nodes;
using BotNexus.Gateway.Configuration;
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
}
