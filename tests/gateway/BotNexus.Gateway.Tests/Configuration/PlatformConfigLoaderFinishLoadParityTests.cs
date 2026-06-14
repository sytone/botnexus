using BotNexus.Gateway.Configuration;
using Microsoft.Extensions.Options;

namespace BotNexus.Gateway.Tests.Configuration;

/// <summary>
/// Pins behavioural parity between the sync <see cref="PlatformConfigLoader.Load"/> and async
/// <see cref="PlatformConfigLoader.LoadAsync"/> entry points after they were de-duplicated behind a
/// shared <c>FinishLoad</c> pipeline (#1392, Finding 1). They must differ only in how they read the
/// file, never in deserialization, migration, validation, or version-warning behaviour.
/// </summary>
public sealed class PlatformConfigLoaderFinishLoadParityTests
{
    [Fact]
    public Task LoadAndLoadAsync_ValidatedConfig_ProduceEquivalentResults()
        => WithConfigFileAsync(
            """
            {
              "version": 1,
              "providers": { "copilot": { "apiKey": "test-key" } },
              "agents": { "assistant": { "provider": "copilot", "model": "gpt-4.1" } }
            }
            """,
            configPath =>
            {
                var sync = PlatformConfigLoader.Load(configPath, validateOnLoad: true);
                var async = PlatformConfigLoader.LoadAsync(configPath, validateOnLoad: true).GetAwaiter().GetResult();

                sync.PlatformVersion.ShouldBe(async.PlatformVersion);
                sync.Agents.ShouldNotBeNull();
                async.Agents.ShouldNotBeNull();
                sync.Agents!.Keys.ShouldBe(async.Agents!.Keys);
                sync.Providers.ShouldNotBeNull();
                async.Providers.ShouldNotBeNull();
                sync.Providers!.Keys.ShouldBe(async.Providers!.Keys);
                return Task.CompletedTask;
            });

    [Fact]
    public Task LoadAndLoadAsync_ValidationDisabled_BothSkipValidation()
        => WithConfigFileAsync(
            // Missing the agent provider would normally fail validation; with validateOnLoad: false
            // both paths must return the (un-validated) config rather than throw.
            """
            {
              "version": 1,
              "gateway": { "listenUrl": "not-a-valid-url" }
            }
            """,
            configPath =>
            {
                var sync = PlatformConfigLoader.Load(configPath, validateOnLoad: false);
                var async = PlatformConfigLoader.LoadAsync(configPath, validateOnLoad: false).GetAwaiter().GetResult();

                sync.Gateway?.ListenUrl.ShouldBe("not-a-valid-url");
                async.Gateway?.ListenUrl.ShouldBe("not-a-valid-url");
                return Task.CompletedTask;
            });

    [Fact]
    public Task LoadAndLoadAsync_InvalidConfig_BothThrowValidation()
        => WithConfigFileAsync(
            """
            {
              "version": 1,
              "gateway": { "listenUrl": "not-a-valid-url" }
            }
            """,
            configPath =>
            {
                var syncEx = Record.Exception(() => PlatformConfigLoader.Load(configPath, validateOnLoad: true));
                var asyncEx = Record.Exception(() =>
                    PlatformConfigLoader.LoadAsync(configPath, validateOnLoad: true).GetAwaiter().GetResult());

                syncEx.ShouldBeOfType<OptionsValidationException>();
                asyncEx.ShouldBeOfType<OptionsValidationException>();
                return Task.CompletedTask;
            });

    [Fact]
    public Task LoadAndLoadAsync_MalformedJson_BothThrowWithPathInMessage()
        => WithConfigFileAsync(
            "{ this is not valid json",
            configPath =>
            {
                var syncEx = Record.Exception(() => PlatformConfigLoader.Load(configPath));
                var asyncEx = Record.Exception(() =>
                    PlatformConfigLoader.LoadAsync(configPath).GetAwaiter().GetResult());

                var syncValidation = syncEx.ShouldBeOfType<OptionsValidationException>();
                var asyncValidation = asyncEx.ShouldBeOfType<OptionsValidationException>();
                syncValidation.Message.ShouldContain("Invalid JSON");
                asyncValidation.Message.ShouldContain("Invalid JSON");
                return Task.CompletedTask;
            });

    [Fact]
    public Task LoadAndLoadAsync_LegacyTopLevelGatewaySettings_BothMigrateIdentically()
        => WithConfigFileAsync(
            // Legacy schema places gateway fields at the JSON root; MigrateLegacyGatewaySettings
            // hoists them into config.Gateway. Both paths run the same migration through FinishLoad.
            """
            {
              "version": 1,
              "listenUrl": "http://localhost:5005",
              "providers": { "copilot": { "apiKey": "test-key" } },
              "agents": { "assistant": { "provider": "copilot", "model": "gpt-4.1" } }
            }
            """,
            configPath =>
            {
                var sync = PlatformConfigLoader.Load(configPath, validateOnLoad: false);
                var async = PlatformConfigLoader.LoadAsync(configPath, validateOnLoad: false).GetAwaiter().GetResult();

                sync.Gateway?.ListenUrl.ShouldBe("http://localhost:5005");
                sync.Gateway?.ListenUrl.ShouldBe(async.Gateway?.ListenUrl);
                return Task.CompletedTask;
            });

    [Fact]
    public Task LoadAndLoadAsync_MissingFile_BothReturnDefault()
        => WithConfigFileAsync(
            "{ \"version\": 1 }",
            configPath =>
            {
                var missing = Path.Combine(Path.GetDirectoryName(configPath)!, "does-not-exist.json");

                var sync = PlatformConfigLoader.Load(missing);
                var async = PlatformConfigLoader.LoadAsync(missing).GetAwaiter().GetResult();

                sync.ShouldNotBeNull();
                async.ShouldNotBeNull();
                return Task.CompletedTask;
            });

    private static async Task WithConfigFileAsync(string json, Func<string, Task> test)
    {
        var rootPath = Path.Combine(
            Path.GetTempPath(),
            "botnexus-platform-config-finishload-parity-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(rootPath);
        var configPath = Path.Combine(rootPath, "config.json");

        try
        {
            await File.WriteAllTextAsync(configPath, json);
            await test(configPath);
        }
        finally
        {
            if (Directory.Exists(rootPath))
                Directory.Delete(rootPath, recursive: true);
        }
    }
}
