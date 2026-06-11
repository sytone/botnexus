using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BotNexus.Gateway.Configuration;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace BotNexus.Gateway.Tests.Configuration;

/// <summary>
/// Fitness functions guarding against drift between the JSON shape that
/// <see cref="IConfigSchemaContributor"/> implementations hydrate into config.json and the
/// strongly-typed <see cref="PlatformConfig"/> model that <see cref="ConfigurationBinder"/>
/// binds it back into.
/// </summary>
/// <remarks>
/// A previous regression typed <c>gateway.auxiliary.titling</c> as a <c>string</c> while the
/// schema contributor hydrated it as an object (<c>{ model, timeoutSeconds }</c>). On startup the
/// hydrator wrote the object, then every <c>IOptionsMonitor&lt;PlatformConfig&gt;</c> rebind threw
/// <c>"Cannot create instance of type 'System.String'"</c>, taking down auth and the portal
/// (<c>/api/agents</c> 500s). These tests reproduce the exact hydrate → bind path so the drift
/// fails the build instead of production.
/// </remarks>
public class SchemaContributorBindingTests
{
    private static readonly JsonSerializerOptions SerializeOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    // Mirrors the built-in contributors registered in GatewayServiceCollectionExtensions.
    private static IReadOnlyList<IConfigSchemaContributor> BuiltInContributors() =>
    [
        new GatewaySchemaContributor(),
        new CompactionSchemaContributor(),
        new AuxiliarySchemaContributor(),
        new AutoUpdateSchemaContributor(),
        new CronSchemaContributor(),
        new SessionStoreSchemaContributor(),
        new RateLimitSchemaContributor(),
    ];

    [Fact]
    public void HydratedDefaults_FromAllSchemaContributors_BindIntoPlatformConfig()
    {
        var root = new JsonObject();

        foreach (var contributor in BuiltInContributors())
        {
            var defaults = contributor.GetDefaults();
            var defaultsJson = JsonSerializer.SerializeToNode(defaults, SerializeOptions);
            defaultsJson.ShouldBeOfType<JsonObject>();
            ConfigHydrationService.MergeAtPath(root, contributor.SectionPath, (JsonObject)defaultsJson!);
        }

        var config = BindToPlatformConfig(root);

        // Spot-check that the binder actually populated the drift-prone nested section.
        config.Gateway.ShouldNotBeNull();
        config.Gateway!.Auxiliary.ShouldNotBeNull();
        config.Gateway!.Auxiliary!.Titling.ShouldNotBeNull();
        config.Gateway!.Auxiliary!.Titling!.TimeoutSeconds.ShouldBe(30);
    }

    [Fact]
    public void AuxiliarySchemaContributorDefaults_BindIntoAuxiliaryConfig_WithoutThrowing()
    {
        var root = new JsonObject();
        var defaults = JsonSerializer.SerializeToNode(new AuxiliarySchemaContributor().GetDefaults(), SerializeOptions);
        ConfigHydrationService.MergeAtPath(root, "gateway.auxiliary", (JsonObject)defaults!);

        var config = BindToPlatformConfig(root);

        config.Gateway!.Auxiliary!.Titling!.Model.ShouldBeNull();
        config.Gateway!.Auxiliary!.Titling!.TimeoutSeconds.ShouldBe(30);
    }

    [Fact]
    public void TitlingObject_BindsModelAndTimeout()
    {
        var root = new JsonObject
        {
            ["gateway"] = new JsonObject
            {
                ["auxiliary"] = new JsonObject
                {
                    ["titling"] = new JsonObject
                    {
                        ["model"] = "gpt-4o-mini",
                        ["timeoutSeconds"] = 45,
                    },
                },
            },
        };

        var config = BindToPlatformConfig(root);

        config.Gateway!.Auxiliary!.Titling!.Model.ShouldBe("gpt-4o-mini");
        config.Gateway!.Auxiliary!.Titling!.TimeoutSeconds.ShouldBe(45);
    }

    private static PlatformConfig BindToPlatformConfig(JsonObject root)
    {
        // Reproduces the production binding path (GatewayServiceCollectionExtensions:
        // services.AddOptions<PlatformConfig>().Bind(configuration)) which is what crashed on the
        // titling shape drift.
        var json = root.ToJsonString();
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        var configuration = new ConfigurationBuilder()
            .AddJsonStream(stream)
            .Build();

        var config = new PlatformConfig();
        configuration.Bind(config);
        return config;
    }
}
