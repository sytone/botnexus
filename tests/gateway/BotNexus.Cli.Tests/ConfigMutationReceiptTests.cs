using System.IO.Abstractions;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using BotNexus.Cli.Commands;
using BotNexus.Gateway.Configuration;
using BotNexus.Gateway.Configuration.Store;
using Spectre.Console;
using Shouldly;

namespace BotNexus.Cli.Tests;

[Collection("AnsiConsole")]
public sealed partial class ConfigMutationReceiptTests : IDisposable
{
    private const string Secret = "receipt-test-secret";

    private readonly string _home;
    private readonly string _configPath;
    private readonly string _storePath;
    private readonly IAnsiConsole _originalConsole;
    private readonly StringWriter _output = new();

    public ConfigMutationReceiptTests()
    {
        _home = Path.Combine(Path.GetTempPath(), $"botnexus-4194-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_home);
        _configPath = Path.Combine(_home, "config.json");
        _storePath = ConfigStoreBootstrap.ResolveStorePath(_configPath, new FileSystem());
        File.WriteAllText(_configPath, "{}");

        _originalConsole = AnsiConsole.Console;
        AnsiConsole.Console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Out = new AnsiConsoleOutput(_output),
            Ansi = AnsiSupport.No,
            Interactive = InteractionSupport.No
        });
        AnsiConsole.Console.Profile.Width = 300;
    }

    public void Dispose()
    {
        AnsiConsole.Console = _originalConsole;
        if (File.Exists(_storePath))
            ConfigStoreBootstrap.ReleaseConnections(_storePath);

        try { Directory.Delete(_home, recursive: true); }
        catch (IOException) { /* best effort after pooled SQLite handles */ }
    }

    [Fact]
    public async Task ProviderAdd_JsonOnly_PrintsConciseJsonReceiptAndPersists()
    {
        var exitCode = await AddProviderAsync();

        exitCode.ShouldBe(0);
        var output = Normalize(_output.ToString());
        output.ShouldContain(
            $"✓ Provider receipt-test added.{Environment.NewLine}" +
            $"  Config saved to: {_configPath}{Environment.NewLine}" +
            $"  Persistence: succeeded.{Environment.NewLine}" +
            $"  Runtime activation: not validated by this offline command.{Environment.NewLine}" +
            $"  Restart required: no when the running gateway receives the configuration reload; verify the provider appears in its live model catalogue before assigning an agent.");
        output.ShouldNotContain("SQLite:");
        output.ShouldNotContain(Secret);

        var json = JsonNode.Parse(await File.ReadAllTextAsync(_configPath))!.AsObject();
        json["providers"]!["receipt-test"]!["apiKey"]!.GetValue<string>().ShouldBe(Secret);
    }

    [Fact]
    public async Task ProviderAdd_WithStore_PrintsEveryBackendAndPersistsToBoth()
    {
        await ConfigStoreBootstrap.PopulateAsync(_storePath, new JsonObject());

        var exitCode = await AddProviderAsync();

        exitCode.ShouldBe(0);
        var output = Normalize(_output.ToString());
        output.ShouldContain(
            $"✓ Provider receipt-test added.{Environment.NewLine}" +
            $"  Updated configuration backends:{Environment.NewLine}" +
            $"    JSON:   {_configPath}{Environment.NewLine}" +
            $"    SQLite: {_storePath} (wins on read){Environment.NewLine}" +
            $"  Persistence: succeeded.{Environment.NewLine}" +
            $"  Runtime activation: not validated by this offline command.{Environment.NewLine}" +
            $"  Restart required: no when the running gateway receives the configuration reload; verify the provider appears in its live model catalogue before assigning an agent.");
        output.ShouldNotContain(Secret);

        var json = JsonNode.Parse(await File.ReadAllTextAsync(_configPath))!.AsObject();
        json["providers"]!["receipt-test"]!["apiKey"]!.GetValue<string>().ShouldBe(Secret);

        var entries = await new SqliteConfigStore($"Data Source={_storePath}").ReadEntriesAsync();
        entries["providers.receipt-test.apiKey"].Value.ShouldBe(System.Text.Json.JsonSerializer.Serialize(Secret));
    }

    [Fact]
    public void ProviderActivationReceipt_StatesPersistenceActivationAndRestartBoundaries()
    {
        ProviderCommand.PrintProviderActivationReceipt();

        var output = Normalize(_output.ToString());
        output.ShouldBe(
            $"  Persistence: succeeded.{Environment.NewLine}" +
            $"  Runtime activation: not validated by this offline command.{Environment.NewLine}" +
            $"  Restart required: no when the running gateway receives the configuration reload; verify the provider appears in its live model catalogue before assigning an agent.{Environment.NewLine}");
    }

    [Theory]
    [InlineData("github-copilot")]
    [InlineData("GITHUB-COPILOT")]
    public void ProviderActivationReceipt_CopilotType_RequiresRestart(string providerType)
    {
        ProviderCommand.PrintProviderActivationReceipt(providerType);

        var output = Normalize(_output.ToString());
        output.ShouldBe(
            $"  Persistence: succeeded.{Environment.NewLine}" +
            $"  Runtime activation: not validated by this offline command.{Environment.NewLine}" +
            $"  Restart required: yes for GitHub Copilot catalogue and discovery setup; restart the gateway, then verify the provider appears in its live model catalogue before assigning an agent.{Environment.NewLine}");
        output.ShouldNotContain("Restart required: no");
    }

    [Theory]
    [InlineData("openai")]
    [InlineData("microsoft-foundry")]
    [InlineData("ollama")]
    public void ProviderActivationReceipt_ConfigDefinedType_DoesNotRequireRestart(string providerType)
    {
        ProviderCommand.PrintProviderActivationReceipt(providerType);

        var output = Normalize(_output.ToString());
        output.ShouldContain("Runtime activation: not validated by this offline command.");
        output.ShouldContain("Restart required: no when the running gateway receives the configuration reload");
        output.ShouldNotContain("Restart required: yes");
    }

    [Theory]
    [InlineData("work-account", "github-copilot", true, false)]
    [InlineData("github-copilot", null, true, false)]
    [InlineData("work-account", "github-copilot", true, true)]
    [InlineData("github-copilot-compatible", "openai", false, false)]
    [InlineData("github-copilot", "openai", false, false)]
    public async Task ProviderAdd_ExistingEntry_UsesEffectiveTypeNotInstancePrefix(
        string name, string? providerType, bool requiresRestart, bool withStore)
    {
        var provider = new JsonObject { ["enabled"] = true };
        if (providerType is not null)
            provider["type"] = providerType;
        var seed = new JsonObject { ["providers"] = new JsonObject { [name] = provider } };
        await File.WriteAllTextAsync(_configPath, seed.ToJsonString());
        if (withStore)
        {
            await ConfigStoreBootstrap.PopulateAsync(_storePath, seed);
            // The saved effective type comes from SQLite, not this stale JSON entry.
            provider["type"] = "openai";
            await File.WriteAllTextAsync(_configPath, seed.ToJsonString());
        }

        var exitCode = await new ProviderCommand().ExecuteAddAsync(
            _configPath, name, api: null, apiKey: Secret, baseUrl: null,
            defaultModel: null, models: [], enabled: true, verbose: false, CancellationToken.None);

        exitCode.ShouldBe(0);
        var output = Normalize(_output.ToString());
        output.ShouldContain("Runtime activation: not validated by this offline command.");
        output.ShouldContain(requiresRestart ? "Restart required: yes for GitHub Copilot catalogue and discovery setup" :
            "Restart required: no when the running gateway receives the configuration reload");
        output.ShouldNotContain(requiresRestart ? "Restart required: no" : "Restart required: yes");
        output.ShouldNotContain(Secret);
    }

    [Fact]
    public async Task ProviderRemove_WithStore_PrintsEveryBackendAndRemovesFromBoth()
    {
        var seed = JsonNode.Parse($$"""{ "providers": { "receipt-test": { "apiKey": "{{Secret}}", "enabled": true } } }""")!.AsObject();
        await File.WriteAllTextAsync(_configPath, seed.ToJsonString());
        await ConfigStoreBootstrap.PopulateAsync(_storePath, seed);

        var exitCode = await new ProviderCommand().ExecuteRemoveAsync(
            _configPath, "receipt-test", verbose: false, CancellationToken.None);

        exitCode.ShouldBe(0);
        var output = Normalize(_output.ToString());
        output.ShouldContain(
            $"✓ Provider receipt-test removed.{Environment.NewLine}" +
            $"  Updated configuration backends:{Environment.NewLine}" +
            $"    JSON:   {_configPath}{Environment.NewLine}" +
            $"    SQLite: {_storePath} (wins on read)");
        output.ShouldNotContain(Secret);

        var json = JsonNode.Parse(await File.ReadAllTextAsync(_configPath))!.AsObject();
        json["providers"]!.AsObject().ContainsKey("receipt-test").ShouldBeFalse();

        var entries = await new SqliteConfigStore($"Data Source={_storePath}").ReadEntriesAsync();
        entries.Keys.ShouldNotContain(key => key.StartsWith("providers.receipt-test", StringComparison.Ordinal));
    }

    private Task<int> AddProviderAsync()
        => new ProviderCommand().ExecuteAddAsync(
            _configPath,
            "receipt-test",
            api: "openai-completions",
            apiKey: Secret,
            baseUrl: null,
            defaultModel: "receipt-model",
            models: ["receipt-model"],
            enabled: true,
            verbose: false,
            CancellationToken.None);

    private static string Normalize(string value)
        => AnsiEscapeSequence().Replace(value, string.Empty)
            .Replace("\r\n", Environment.NewLine, StringComparison.Ordinal)
            .Replace("\n", Environment.NewLine, StringComparison.Ordinal);

    [GeneratedRegex("\\x1B\\[[0-?]*[ -/]*[@-~]")]
    private static partial Regex AnsiEscapeSequence();
}
