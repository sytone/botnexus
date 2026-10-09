namespace BotNexus.Architecture.Tests;

/// <summary>
/// Keeps the provider documentation honest about the distinction between a provider instance,
/// its wire contract, canonical CLI authentication, and selected-instance Portal quota scope (#4626).
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
        overview.ShouldContain("canonical CLI login and diagnostics still use `github-copilot`");
        overview.ShouldContain("Copilot instances can be selected in Portal Usage");
        overview.ShouldContain("does not imply that every built-in provisioning or enterprise scenario supports named accounts");
    }

    [Fact]
    public void CopilotGuide_ExplainsAliasAndBoundsNamedInstanceUsage()
    {
        var guide = File.ReadAllText(Repository.Path("docs", "providers", "github-copilot.md"));

        guide.ShouldContain("`copilot` is an alias");
        guide.ShouldContain("not a second subscription");
        guide.ShouldContain("Canonical CLI login and diagnostics still use `github-copilot`");
        guide.ShouldContain("does not promise the complete provisioning/feature matrix");
        guide.ShouldContain("#4192");
        guide.ShouldContain("never an implicit default-account fallback");
        guide.ShouldContain("Historical runs lack provider identity");
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
