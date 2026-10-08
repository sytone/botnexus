using System.Text.Json.Nodes;
using System.IO.Abstractions;
using BotNexus.Gateway.Configuration.Store;
using BotNexus.Agent.Core.Tools;
using BotNexus.Gateway.Configuration;
using BotNexus.Gateway.Extensions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BotNexus.Gateway.Tests.Tools;

/// <summary>Proves the policy consumes the canonical provider, not a JSON projection or ambient values.</summary>
public sealed class LocalChildEnvironmentConfigStoreTests
{
    [Theory]
    [InlineData("absent")]
    [InlineData("corrupt")]
    [InlineData("conflicting")]
    public async Task Policy_CanonicalStoreNamesBindWithoutTrustedProjection(string projection)
    {
        var root = Path.Combine(Path.GetTempPath(), "botnexus-environment-config-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var configPath = Path.Combine(root, "config.json");
        var storePath = ConfigStoreBootstrap.ResolveStorePath(configPath, new FileSystem());
        const string approved = "BN4749_STORE_APPROVED";
        const string excluded = "BN4749_PROJECTION_ONLY";
        try
        {
            var store = new SqliteConfigStore($"Data Source={storePath};Pooling=False");
            await store.WriteDocumentAsync(new JsonObject
            {
                ["gateway"] = new JsonObject
                {
                    ["localChildEnvironmentPassThrough"] = new JsonArray(approved)
                }
            });
            if (projection == "corrupt")
                await File.WriteAllTextAsync(configPath, "not valid json");
            else if (projection == "conflicting")
                await File.WriteAllTextAsync(configPath,
                    "{\"gateway\":{\"localChildEnvironmentPassThrough\":[\"BN4749_PROJECTION_ONLY\"]}}");

            var configuration = new ConfigurationBuilder().AddPlatformConfiguration(configPath).Build();
            using var configurationLifetime = (IDisposable)configuration;
            var services = new ServiceCollection();
            services.Configure<PlatformConfig>(configuration);
            services.AddBotNexusTools();
            using var provider = services.BuildServiceProvider();
            var policy = provider.GetRequiredService<LocalChildEnvironmentPolicy>();
            var child = new Dictionary<string, string?>();
            LocalChildEnvironment.Populate(child, new Dictionary<string, string?>
            {
                [approved] = "synthetic-store-value", [excluded] = "synthetic-projection-value"
            }, policy);
            child[approved].ShouldBe("synthetic-store-value");
            child.ShouldNotContainKey(excluded);
            var serialized = System.Text.Json.JsonSerializer.Serialize(
                configuration.Get<PlatformConfig>() ?? throw new InvalidOperationException("Missing bound config"));
            serialized.ShouldContain(approved);
            serialized.ShouldNotContain("synthetic-store-value");
            serialized.ShouldNotContain("synthetic-projection-value");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
