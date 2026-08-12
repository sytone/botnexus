using System.Text.Json;
using System.Text.Json.Nodes;
using BotNexus.Gateway.Configuration;
using Shouldly;
using Xunit;

namespace BotNexus.Gateway.Tests.Configuration;

/// <summary>
/// Pins the feature-flag inventory introduced by #2767: one declaration per flag, addressable via
/// the config path resolver, with absence treated as a reportable gap rather than a silent default.
/// </summary>
public class FeatureFlagInventoryTests
{
    // ── AC1: a single declared inventory ────────────────────────────────────────────────

    [Fact]
    public void Inventory_DeclaresGatewayDevOriginEnforcement()
    {
        FeatureFlags.All.ShouldContain(flag => flag.Name == FeatureFlags.GatewayDevOriginEnforcement);
    }

    [Fact]
    public void Inventory_DeclaresEachFlagExactlyOnce()
    {
        // AC1 + AC8 mutation target: removing a flag from the inventory, or declaring one twice,
        // must redden this test by name.
        var names = FeatureFlags.All.Select(flag => flag.Name).ToList();
        names.ShouldBeUnique();
        names.ShouldContain(FeatureFlags.GatewayDevOriginEnforcement);
    }

    [Fact]
    public void Inventory_EveryFlagCarriesANonEmptyDescription()
    {
        // A flag an operator cannot understand is not meaningfully declared: doctor prints this
        // text as the explanation of the decision being seeded.
        foreach (var flag in FeatureFlags.All)
        {
            flag.Name.ShouldNotBeNullOrWhiteSpace();
            flag.Description.ShouldNotBeNullOrWhiteSpace();
        }
    }

    [Fact]
    public void DevOriginEnforcement_DefaultsToOff()
    {
        // Pins the documented default AND the security posture: this guard must remain opt-in so
        // enabling it cannot lock a keyless operator out of their own gateway.
        FeatureFlags.DefaultFor(FeatureFlags.GatewayDevOriginEnforcement).ShouldBeFalse();
    }

    [Fact]
    public void Find_ReturnsNullForAnUndeclaredName()
    {
        FeatureFlags.Find("GatewayDevOriginEnforcment").ShouldBeNull(); // deliberate misspelling
        FeatureFlags.IsDeclared("NoSuchFlag").ShouldBeFalse();
        FeatureFlags.Find(null).ShouldBeNull();
    }

    [Fact]
    public void Find_IsCaseInsensitiveSoAnOperatorsCasingDoesNotSilentlyMiss()
    {
        FeatureFlags.Find("gatewaydevoriginenforcement").ShouldNotBeNull();
    }

    [Fact]
    public void DefaultFor_UndeclaredNameIsOff()
    {
        // Sad path: an unknown flag already evaluates as off; the inventory must not invent a
        // different answer for it.
        FeatureFlags.DefaultFor("NoSuchFlag").ShouldBeFalse();
    }

    [Fact]
    public void SectionName_MatchesTheMicrosoftFeatureManagementSection()
    {
        // The binder reads "FeatureManagement"; drifting this constant would silently unbind
        // every flag while leaving the model looking correct.
        FeatureFlags.SectionName.ShouldBe("FeatureManagement");
    }

    // ── AC2: addressable via config get/set ─────────────────────────────────────────────

    [Fact]
    public void PlatformConfig_ExposesFeatureManagement()
    {
        typeof(PlatformConfig).GetProperty(nameof(PlatformConfig.FeatureManagement))
            .ShouldNotBeNull("config get/set cannot address a section that is not modelled (#2767).");
    }

    [Fact]
    public void ConfigGet_ResolvesADeclaredFlag()
    {
        var config = new PlatformConfig
        {
            FeatureManagement = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase)
            {
                [FeatureFlags.GatewayDevOriginEnforcement] = JsonDocument.Parse("true").RootElement
            }
        };

        var resolver = new ConfigPathResolver();
        resolver.TryGetValue(config, $"FeatureManagement.{FeatureFlags.GatewayDevOriginEnforcement}", out var value, out var error)
            .ShouldBeTrue(error);
        value.ShouldNotBeNull();
        ((JsonElement)value!).GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public void ConfigSet_WritesAFlagAndGetReturnsTheWrittenValue()
    {
        // AC2 round trip: set then get, which is the operator-visible contract.
        var config = new PlatformConfig();
        var resolver = new ConfigPathResolver();
        var path = $"FeatureManagement.{FeatureFlags.GatewayDevOriginEnforcement}";

        resolver.TrySetValue(config, path, "true", out var setError).ShouldBeTrue(setError);
        resolver.TryGetValue(config, path, out var value, out var getError).ShouldBeTrue(getError);

        ((JsonElement)value!).GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public void ConfigSet_PreservesTheFilterObjectForm()
    {
        // Sad path / lossiness guard: Microsoft.FeatureManagement permits an object with an
        // EnabledFor filter list, not just a bool. Modelling the section as Dictionary<string,bool>
        // would silently destroy that form on a typed round trip.
        var config = new PlatformConfig
        {
            FeatureManagement = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase)
            {
                [FeatureFlags.GatewayDevOriginEnforcement] =
                    JsonDocument.Parse("""{"EnabledFor":[{"Name":"Percentage"}]}""").RootElement
            }
        };

        var json = JsonSerializer.Serialize(config, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        });

        var round = JsonNode.Parse(json)!.AsObject();
        var section = round[FeatureFlags.SectionName]!.AsObject();
        section[FeatureFlags.GatewayDevOriginEnforcement]!["EnabledFor"].ShouldNotBeNull();
    }

    [Fact]
    public void FeatureManagement_SerializesUnderThePascalCaseSectionName()
    {
        // The rest of PlatformConfig is camelCased on write; this section must NOT be, or
        // Microsoft.FeatureManagement stops binding it. Regression-prone by construction.
        var config = new PlatformConfig
        {
            FeatureManagement = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase)
            {
                [FeatureFlags.GatewayDevOriginEnforcement] = JsonDocument.Parse("true").RootElement
            }
        };

        var json = JsonSerializer.Serialize(config, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        });

        JsonNode.Parse(json)!.AsObject()[FeatureFlags.SectionName].ShouldNotBeNull(
            "Microsoft.FeatureManagement binds the PascalCase 'FeatureManagement' section.");
    }
}
