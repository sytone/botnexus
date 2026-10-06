using System.Runtime.CompilerServices;
using System.Text.Json;
using BotNexus.Gateway.Configuration;

namespace BotNexus.Gateway.Tests;

/// <summary>
/// Gives the process-wide gateway test home one stable world identity for the lifetime of the test
/// assembly. Individual integration hosts may otherwise mint different identities for the same
/// runsettings home after removing hosted persistence services, which is no longer a valid harness
/// once gateway composition activates the home-world sentinel (#3411).
/// </summary>
[Collection("IntegrationTests")]
internal static class GatewayTestHomeBootstrap
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        var homePath = Environment.GetEnvironmentVariable(BotNexusHome.HomeOverrideEnvVar);
        if (string.IsNullOrWhiteSpace(homePath))
            return;

        homePath = Path.GetFullPath(homePath);
        if (Directory.Exists(homePath))
            Directory.Delete(homePath, recursive: true);

        Directory.CreateDirectory(homePath);
        File.WriteAllText(
            Path.Combine(homePath, "config.json"),
            JsonSerializer.Serialize(new Dictionary<string, string>
            {
                [WorldIdResolver.ConfigPropertyName] = Guid.NewGuid().ToString("D")
            }));
    }
}
