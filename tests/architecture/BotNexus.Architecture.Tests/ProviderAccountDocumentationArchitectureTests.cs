namespace BotNexus.Architecture.Tests;

/// <summary>
/// Keeps the provider documentation honest about the distinction between a provider instance,
/// its wire contract, and the one canonical GitHub Copilot credential/catalogue path (#4191).
/// </summary>
public sealed class ProviderAccountDocumentationArchitectureTests : ArchitectureTest
{
    [Fact]
    public void ProviderOverview_ExplainsMultiProviderAssignmentAndCurrentCopilotBoundary()
    {
        var overview = File.ReadAllText(Repository.Path("docs", "user-guide", "providers.md"));

        overview.ShouldContain("provider type");
        overview.ShouldContain("provider instance");
        overview.ShouldContain("API contract");
        overview.ShouldContain("credential");
        overview.ShouldContain("agents.copilot-agent.provider");
        overview.ShouldContain("agents.openai-agent.provider");
        overview.ShouldContain("botnexus validate");
        overview.ShouldContain("Reply with only: Copilot connection works");
        overview.ShouldContain("Reply with only: OpenAI connection works");
        overview.ShouldContain("one canonical GitHub Copilot account");
    }

    [Fact]
    public void CopilotGuide_ExplainsAliasAndRejectsUnsupportedNamedBuiltInInstances()
    {
        var guide = File.ReadAllText(Repository.Path("docs", "providers", "github-copilot.md"));

        guide.ShouldContain("`copilot` is an alias");
        guide.ShouldContain("not a second provider instance");
        guide.ShouldContain("does not support two independently authenticated Copilot accounts");
        guide.ShouldContain("#4191");
        guide.ShouldContain("overwrites the existing `github-copilot` auth entry");
    }

    [Fact]
    public void ConfigurationGuide_DoesNotTeachRetiredProviderExtensionOrTokenContracts()
    {
        var guide = File.ReadAllText(Repository.Path("docs", "configuration.md"));

        guide.ShouldNotContain("Keys are case-insensitive and match extension folder names under `extensions/providers/{name}/`");
        guide.ShouldNotContain("Token cached at `~/.botnexus/tokens/copilot.json`");
        guide.ShouldNotContain("On first use, agent prompts user");
        guide.ShouldNotContain("`Auth`: Must be \"oauth\"");
        guide.ShouldNotContain("`OAuthClientId`");
    }
}
