using BotNexus.Cron.Prompts;

namespace BotNexus.Cron.Tests;

public sealed class PromptTemplateRendererTests
{
    [Fact]
    public void GetRequiredParameters_DeduplicatesCaseInsensitiveNames()
    {
        var required = PromptTemplateRenderer.GetRequiredParameters("Hello {{name}} from {{ Name }} on {{day}}");

        required.Count.ShouldBe(2);
        required.ShouldContain("name");
        required.ShouldContain("day");
    }

    [Fact]
    public void TryRender_UsesDefaultsAndOverridesWithProvidedParameters()
    {
        var ok = PromptTemplateRenderer.TryRender(
            "Status for {{project}} by {{owner}}",
            new Dictionary<string, string?> { ["owner"] = "Hermes" },
            new Dictionary<string, string?> { ["project"] = "BotNexus", ["owner"] = "Default" },
            out var rendered,
            out var error);

        ok.ShouldBeTrue();
        error.ShouldBeNull();
        rendered.ShouldBe("Status for BotNexus by Hermes");
    }
}
