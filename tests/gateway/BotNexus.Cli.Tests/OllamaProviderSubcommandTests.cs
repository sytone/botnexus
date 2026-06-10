using BotNexus.Cli.Commands.Provider;

namespace BotNexus.Cli.Tests;

public sealed class OllamaProviderSubcommandTests
{
    [Fact]
    public void DefaultBaseUrl_IsLocalhost11434()
    {
        OllamaProviderSubcommand.DefaultBaseUrl.ShouldBe("http://localhost:11434");
    }

    [Fact]
    public void DefaultApiCompatUrl_AppendsV1()
    {
        OllamaProviderSubcommand.DefaultApiCompatUrl.ShouldBe("http://localhost:11434/v1");
    }

    [Fact]
    public async Task ExecuteStatusAsync_UnreachableServer_ReturnsNonZero()
    {
        // Use a port that's almost certainly not running Ollama
        var result = await OllamaProviderSubcommand.ExecuteStatusAsync(
            "http://localhost:19999", CancellationToken.None);

        result.ShouldNotBe(0);
    }

    [Fact]
    public async Task ExecuteModelsAsync_UnreachableServer_ReturnsNonZero()
    {
        var result = await OllamaProviderSubcommand.ExecuteModelsAsync(
            "http://localhost:19999", CancellationToken.None);

        result.ShouldNotBe(0);
    }

    [Fact]
    public async Task ExecuteTestAsync_UnreachableServer_ReturnsNonZero()
    {
        var result = await OllamaProviderSubcommand.ExecuteTestAsync(
            "http://localhost:19999", "nonexistent-model", "hello", CancellationToken.None);

        result.ShouldNotBe(0);
    }

    [Fact]
    public void OllamaPickModelStep_HasCorrectName()
    {
        var step = new OllamaProviderSubcommand.OllamaPickModelStep();
        step.Name.ShouldBe("ollama-pick-model");
    }
}
