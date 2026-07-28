using System.IO.Abstractions.TestingHelpers;
using System.Text.Json.Nodes;
using BotNexus.Gateway.Configuration;

namespace BotNexus.Gateway.Tests.Configuration;

/// <summary>
/// Unit tests for the JSON merge and secret-restore logic <see cref="PlatformConfigWriter"/>
/// applies on the config-UI <c>GET -&gt; redact -&gt; edit -&gt; PUT</c> path. They lock in the
/// #1954 (dropped subtrees) and #1955 (clobbered secrets) fixes.
/// </summary>
/// <remarks>
/// <para>
/// These were previously filed as <c>BotNexus.Integration.ConfigSave.Tests</c> and described as
/// "real round trips". They are not: they run against <see cref="MockFileSystem"/>, so they can
/// only validate the in-memory JSON transformation. They cannot observe OS replacement semantics,
/// atomic temp-file staging and cleanup, physical backups, file locking, configuration-provider
/// reload, or any downstream <c>IOptionsMonitor</c> consumer - which is exactly why a merge bug
/// that survived them was still able to reach disk (#2066).
/// </para>
/// <para>
/// They are retained here, honestly labelled as unit tests, because the merge algebra they cover
/// is genuinely worth pinning cheaply and deterministically. The integration acceptance bar lives
/// in <c>tests/integration/BotNexus.Integration.ConfigDiskE2E.Tests</c>, which drives the same
/// writer against a real filesystem under a temporary <c>BOTNEXUS_HOME</c>. Do not re-promote
/// these to integration tests.
/// </para>
/// </remarks>
public sealed class ConfigSaveMergeUnitTests : IDisposable
{
    private readonly string _rootPath = Path.Combine(
        Path.GetTempPath(), "botnexus-config-merge-unit-tests", Guid.NewGuid().ToString("N"));
    private readonly string _configPath;
    private readonly MockFileSystem _fileSystem;

    public ConfigSaveMergeUnitTests()
    {
        _fileSystem = new MockFileSystem();
        _fileSystem.Directory.CreateDirectory(_rootPath);
        _configPath = Path.Combine(_rootPath, "config.json");
    }

    private const string LiveConfig = """
        {
          "apiKey": "sk-top-level-REAL-secret",
          "providers": {
            "github-copilot": {
              "apiKey": "sk-provider-REAL-secret",
              "model": "claude"
            }
          },
          "gateway": {
            "apiKeys": {
              "primary": { "apiKey": "gw-REAL-secret", "scopes": ["admin"] }
            },
            "sessionStore": { "provider": "sqlite", "connectionString": "Data Source=REAL.db" }
          },
          "channels": {
            "telegram": {
              "enabled": true,
              "bots": {
                "main": { "token": "123456:REAL-telegram-token", "name": "MainBot" }
              }
            },
            "serviceBus": {
              "namespace": "contoso.servicebus.windows.net",
              "queues": {
                "inbound": { "name": "inbound-q", "maxConcurrent": 4 }
              }
            }
          }
        }
        """;

    /// <summary>
    /// Exercises the merge algebra of the full UI flow: read the section, redact it (what GET
    /// returns), edit one unrelated field, and PUT the whole redacted section back. Asserts the
    /// real secrets survive (#1955) and the telegram/serviceBus subtrees the redacted payload
    /// carried survive (#1954).
    /// </summary>
    [Fact]
    public async Task GetEditPut_RoundTrip_PreservesSecretsAndChannelSubtrees()
    {
        await _fileSystem.File.WriteAllTextAsync(_configPath, LiveConfig);
        var writer = new PlatformConfigWriter(_configPath, _fileSystem);

        // --- GET: read gateway section and redact exactly as the controller does. ---
        var root = await writer.ReadAsync();
        var gatewayForUi = root["gateway"]!.DeepClone().AsObject();
        var wrapper = new JsonObject { ["gateway"] = gatewayForUi };
        ConfigSecretMerge.Redact(wrapper);
        gatewayForUi = wrapper["gateway"]!.AsObject();

        // The UI genuinely sees only the placeholder.
        gatewayForUi["apiKeys"]!["primary"]!["apiKey"]!.GetValue<string>()
            .ShouldBe(ConfigSecretMerge.Placeholder);
        gatewayForUi["sessionStore"]!["connectionString"]!.GetValue<string>()
            .ShouldBe(ConfigSecretMerge.Placeholder);

        // --- EDIT: change one unrelated field. ---
        gatewayForUi["sessionStore"]!["provider"] = "postgres";

        // --- PUT: send the redacted gateway section back verbatim. ---
        await writer.UpdateSectionAsync("gateway", gatewayForUi.DeepClone());

        // --- Assert on the resulting document. ---
        var after = await writer.ReadAsync();
        var gateway = after["gateway"]!;

        // #1955: real secrets survived instead of being clobbered by "***".
        gateway["apiKeys"]!["primary"]!["apiKey"]!.GetValue<string>().ShouldBe("gw-REAL-secret");
        gateway["sessionStore"]!["connectionString"]!.GetValue<string>().ShouldBe("Data Source=REAL.db");

        // The edited field landed.
        gateway["sessionStore"]!["provider"]!.GetValue<string>().ShouldBe("postgres");

        // Untouched top-level / provider secrets are still intact.
        after["apiKey"]!.GetValue<string>().ShouldBe("sk-top-level-REAL-secret");
        after["providers"]!["github-copilot"]!["apiKey"]!.GetValue<string>().ShouldBe("sk-provider-REAL-secret");
    }

    /// <summary>
    /// A save that binds through a typed model omitting subtrees (telegram bots, serviceBus
    /// queues) must NOT drop them (#1954). Simulate by PUTting a channels section that only
    /// carries a scalar edit, omitting the bots/queues.
    /// </summary>
    [Fact]
    public async Task PutChannels_WithOmittedSubtrees_KeepsExistingBotsAndQueues()
    {
        await _fileSystem.File.WriteAllTextAsync(_configPath, LiveConfig);
        var writer = new PlatformConfigWriter(_configPath, _fileSystem);

        // Typed/partial payload: toggles telegram.enabled but omits bots + all of serviceBus.
        var partialChannels = new JsonObject
        {
            ["telegram"] = new JsonObject { ["enabled"] = false }
        };

        await writer.UpdateSectionAsync("channels", partialChannels);

        var after = await writer.ReadAsync();
        var channels = after["channels"]!;

        // Scalar edit applied.
        channels["telegram"]!["enabled"]!.GetValue<bool>().ShouldBeFalse();

        // #1954: omitted subtrees survive the write.
        channels["telegram"]!["bots"]!["main"]!["token"]!.GetValue<string>()
            .ShouldBe("123456:REAL-telegram-token");
        channels["telegram"]!["bots"]!["main"]!["name"]!.GetValue<string>().ShouldBe("MainBot");
        channels["serviceBus"]!["queues"]!["inbound"]!["name"]!.GetValue<string>().ShouldBe("inbound-q");
        channels["serviceBus"]!["namespace"]!.GetValue<string>().ShouldBe("contoso.servicebus.windows.net");
    }

    /// <summary>
    /// The per-entry PUT path (providers.github-copilot) must apply the same secret-restore and
    /// deep-merge: a redacted single provider PUT back keeps its real apiKey (#1955) and any
    /// omitted sibling keys survive (#1954).
    /// </summary>
    [Fact]
    public async Task PutSectionEntry_RedactedProvider_PreservesSecretAndOmittedKeys()
    {
        await _fileSystem.File.WriteAllTextAsync(_configPath, LiveConfig);
        var writer = new PlatformConfigWriter(_configPath, _fileSystem);

        // GET provider entry and redact as the controller would.
        var root = await writer.ReadAsync();
        var providerForUi = root["providers"]!["github-copilot"]!.DeepClone().AsObject();
        var wrapper = new JsonObject { ["providers"] = new JsonObject { ["github-copilot"] = providerForUi } };
        ConfigSecretMerge.Redact(wrapper);
        providerForUi = wrapper["providers"]!["github-copilot"]!.AsObject();
        providerForUi["apiKey"]!.GetValue<string>().ShouldBe(ConfigSecretMerge.Placeholder);

        // EDIT: change model, leave apiKey as the placeholder, and omit nothing extra.
        providerForUi["model"] = "claude-3.7";

        // PUT the single entry back.
        await writer.UpdateSectionEntryAsync("providers", "github-copilot", providerForUi.DeepClone());

        var after = await writer.ReadAsync();
        var provider = after["providers"]!["github-copilot"]!;
        // #1955: real secret survived.
        provider["apiKey"]!.GetValue<string>().ShouldBe("sk-provider-REAL-secret");
        // Edit applied.
        provider["model"]!.GetValue<string>().ShouldBe("claude-3.7");
    }

    public void Dispose()
    {
        if (_fileSystem.Directory.Exists(_rootPath))
            _fileSystem.Directory.Delete(_rootPath, recursive: true);
    }
}
