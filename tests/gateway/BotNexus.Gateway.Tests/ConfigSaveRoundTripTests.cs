using BotNexus.Gateway.Api.Controllers;
using BotNexus.Gateway.Configuration;
using Microsoft.AspNetCore.Mvc;
using System.IO.Abstractions;
using System.Text.Json.Nodes;

namespace BotNexus.Gateway.Tests;

/// <summary>
/// Full GET -> edit -> PUT round-trip tests that exercise the real ConfigController redaction
/// path together with the PlatformConfigWriter merge path against a temp config file. These would
/// have caught the config-UI save data-loss cluster (#1954 channel subtree drop, #1955 secret
/// clobber): a mock-only writer test could not, because both defects live in the interaction
/// between GET-side redaction and PUT-side persistence.
/// </summary>
public sealed class ConfigSaveRoundTripTests
{
    // #1955 - a redacted secret PUT back verbatim must NOT overwrite the real on-disk secret.
    [Fact]
    public async Task RoundTrip_SecretLeftAsPlaceholder_PreservesOriginalOnDiskValue()
    {
        const string raw = """
        {
          "providers": {
            "openai": { "apiKey": "sk-real-secret", "model": "gpt-4.1" }
          },
          "gateway": {
            "listenUrl": "http://localhost:5000",
            "apiKeys": { "tenant-a": { "apiKey": "tenant-secret" } },
            "sessionStore": { "connectionString": "Server=db;Password=hunter2" }
          }
        }
        """;

        await WithConfigFileAsync(raw, async (controller, writer, path) =>
        {
            // GET providers (redacted) then PUT it back verbatim (UI behaviour).
            var providersGet = await controller.GetSection("providers", writer, CancellationToken.None);
            var providers = ((OkObjectResult)providersGet.Result!).Value.ShouldBeOfType<JsonObject>();
            providers["openai"]!["apiKey"]!.GetValue<string>().ShouldBe("***");

            await writer.UpdateSectionAsync("providers", providers.DeepClone(), CancellationToken.None);

            // GET gateway (redacted) then PUT it back verbatim.
            var gatewayGet = await controller.GetSection("gateway", writer, CancellationToken.None);
            var gateway = ((OkObjectResult)gatewayGet.Result!).Value.ShouldBeOfType<JsonObject>();
            gateway["apiKeys"]!["tenant-a"]!["apiKey"]!.GetValue<string>().ShouldBe("***");
            gateway["sessionStore"]!["connectionString"]!.GetValue<string>().ShouldBe("***");

            await writer.UpdateSectionAsync("gateway", gateway.DeepClone(), CancellationToken.None);

            // On-disk secrets must survive the redacted round-trip.
            var onDisk = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
            onDisk["providers"]!["openai"]!["apiKey"]!.GetValue<string>().ShouldBe("sk-real-secret");
            onDisk["providers"]!["openai"]!["model"]!.GetValue<string>().ShouldBe("gpt-4.1");
            onDisk["gateway"]!["apiKeys"]!["tenant-a"]!["apiKey"]!.GetValue<string>().ShouldBe("tenant-secret");
            onDisk["gateway"]!["sessionStore"]!["connectionString"]!.GetValue<string>().ShouldBe("Server=db;Password=hunter2");
        });
    }

    // #1955 - a genuinely new secret value (not the placeholder) still writes.
    [Fact]
    public async Task RoundTrip_NewSecretValue_IsPersisted()
    {
        const string raw = """
        { "providers": { "openai": { "apiKey": "sk-old", "model": "gpt-4.1" } } }
        """;

        await WithConfigFileAsync(raw, async (controller, writer, path) =>
        {
            var providersGet = await controller.GetSection("providers", writer, CancellationToken.None);
            var providers = ((OkObjectResult)providersGet.Result!).Value.ShouldBeOfType<JsonObject>();

            // User types a real new secret over the mask.
            providers["openai"]!["apiKey"] = "sk-new-secret";
            await writer.UpdateSectionAsync("providers", providers.DeepClone(), CancellationToken.None);

            var onDisk = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
            onDisk["providers"]!["openai"]!["apiKey"]!.GetValue<string>().ShouldBe("sk-new-secret");
        });
    }

    // #1954 - editing one channel field must not drop unmodeled channel subtrees.
    [Fact]
    public async Task RoundTrip_EditingChannelField_PreservesUnmodeledSubtrees()
    {
        const string raw = """
        {
          "channels": {
            "telegram": {
              "enabled": true,
              "bots": {
                "main": { "token": "bot-token", "allowedChatIds": [ 1, 2, 3 ] }
              }
            },
            "serviceBusChannel": {
              "enabled": true,
              "namespace": "sb://example",
              "queueName": "inbound",
              "maxConcurrentCalls": 4
            }
          }
        }
        """;

        await WithConfigFileAsync(raw, async (controller, writer, path) =>
        {
            // Simulate the UI: it round-trips channels through the strongly-typed model, which
            // can only represent { enabled } and drops the unmodeled bots / serviceBus fields.
            var lossyIncoming = new JsonObject
            {
                ["telegram"] = new JsonObject { ["enabled"] = false },
                ["serviceBusChannel"] = new JsonObject { ["enabled"] = true }
            };

            await writer.UpdateSectionAsync("channels", lossyIncoming, CancellationToken.None);

            var onDisk = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();

            // The edited field is applied.
            onDisk["channels"]!["telegram"]!["enabled"]!.GetValue<bool>().ShouldBeFalse();

            // The unmodeled subtrees survive.
            onDisk["channels"]!["telegram"]!["bots"]!["main"]!["token"]!.GetValue<string>().ShouldBe("bot-token");
            onDisk["channels"]!["telegram"]!["bots"]!["main"]!["allowedChatIds"]!.AsArray().Count.ShouldBe(3);
            onDisk["channels"]!["serviceBusChannel"]!["namespace"]!.GetValue<string>().ShouldBe("sb://example");
            onDisk["channels"]!["serviceBusChannel"]!["queueName"]!.GetValue<string>().ShouldBe("inbound");
            onDisk["channels"]!["serviceBusChannel"]!["maxConcurrentCalls"]!.GetValue<int>().ShouldBe(4);
        });
    }

    // #1955 + #1954 combined via the per-entry PUT path (providers.openai single entry).
    [Fact]
    public async Task RoundTrip_SectionEntryPut_PreservesSecretAndSiblingFields()
    {
        const string raw = """
        { "providers": { "openai": { "apiKey": "sk-real", "model": "gpt-4.1", "organization": "org-x" } } }
        """;

        await WithConfigFileAsync(raw, async (controller, writer, path) =>
        {
            // GET whole providers section (redacted), edit only model, PUT single entry back.
            var providersGet = await controller.GetSection("providers", writer, CancellationToken.None);
            var providers = ((OkObjectResult)providersGet.Result!).Value.ShouldBeOfType<JsonObject>();
            var openai = providers["openai"]!.DeepClone().AsObject();
            openai["model"] = "gpt-4.1-mini"; // apiKey still "***"

            await writer.UpdateSectionEntryAsync("providers", "openai", openai, CancellationToken.None);

            var onDisk = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
            onDisk["providers"]!["openai"]!["apiKey"]!.GetValue<string>().ShouldBe("sk-real");
            onDisk["providers"]!["openai"]!["model"]!.GetValue<string>().ShouldBe("gpt-4.1-mini");
            onDisk["providers"]!["openai"]!["organization"]!.GetValue<string>().ShouldBe("org-x");
        });
    }

    private static async Task WithConfigFileAsync(
        string rawJson,
        Func<ConfigController, PlatformConfigWriter, string, Task> body)
    {
        var root = Path.Combine(Path.GetTempPath(), "botnexus-config-roundtrip-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "config.json");
        try
        {
            await File.WriteAllTextAsync(path, rawJson);
            var controller = new ConfigController();
            var writer = new PlatformConfigWriter(path, new FileSystem());
            await body(controller, writer, path);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
