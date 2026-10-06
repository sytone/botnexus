namespace BotNexus.Architecture.Tests;

/// <summary>Fences startup extension loading behind the same legacy migration seam as options binding.</summary>
public sealed class GatewayStartupLegacyExtensionMigrationArchitectureTests : ArchitectureTest
{
    [Fact]
    public void StartupConfig_IsNormalisedBeforeConfiguredExtensionsAreLoaded()
    {
        var source = File.ReadAllText(Repository.Path("src", "gateway", "BotNexus.Gateway.Api", "Program.cs"));
        var migration = source.IndexOf(
            "PlatformConfigPostConfigure.ApplyAuthoritativeRawShape(builder.Configuration, startupPlatformConfig)",
            StringComparison.Ordinal);
        var extensionLoad = source.IndexOf(
            "LoadConfiguredExtensionsAsync(startupPlatformConfig",
            StringComparison.Ordinal);

        migration.ShouldBeGreaterThan(-1, "startup must invoke the shared legacy migration seam");
        extensionLoad.ShouldBeGreaterThan(migration, "legacy loader settings must exist before extension bootstrap");
    }
}
