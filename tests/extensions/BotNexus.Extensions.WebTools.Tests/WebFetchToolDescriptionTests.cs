namespace BotNexus.Extensions.WebTools.Tests;

/// <summary>
/// Issue #2691 AC1: 99.2% of web_fetch failures were the loopback SSRF block discovered at call
/// time. The refusal already names the remedy (#2418), but the tool description did not mention
/// the restriction at all, so an agent reading the schema could not know before calling. These
/// tests pin that the restriction and the remedy are visible in the description itself, and that
/// the remedy named there is derived from - not a second copy of - the refusal guidance.
/// </summary>
[Trait("Category", "Security")]
public class WebFetchToolDescriptionTests
{
    [Fact]
    public void Definition_Description_StatesLoopbackAndPrivateRangeHostsAreRefused()
    {
        using var tool = CreateTool();

        var description = tool.Definition.Description.ToLowerInvariant();

        description.ShouldContain("loopback");
        description.ShouldContain("localhost");
        description.ShouldContain("private-range");
        description.ShouldContain("refused");
    }

    /// <summary>
    /// The description must name the same working alternative the refusal names. Asserted by
    /// derivation from the single shared constant rather than by restating the sentence, so the
    /// two surfaces cannot drift apart without this test failing.
    /// </summary>
    [Fact]
    public void Definition_Description_NamesTheSameLocalAlternativeAsTheRefusalGuidance()
    {
        using var tool = CreateTool();

        WebFetchTool.LocalEndpointRemedy.ShouldNotBeNullOrWhiteSpace();
        WebFetchTool.LoopbackGuidance.ShouldContain(WebFetchTool.LocalEndpointRemedy);
        tool.Definition.Description.ShouldContain(WebFetchTool.LocalEndpointRemedy);
    }

    /// <summary>
    /// The pre-existing description text must survive: this is a description addition, not a
    /// replacement of what the tool already advertised.
    /// </summary>
    [Fact]
    public void Definition_Description_RetainsOriginalCapabilityText()
    {
        using var tool = CreateTool();

        tool.Definition.Description.ShouldContain(
            "Fetch a URL and return content as readable text or raw HTML. Supports pagination.");
    }

    private static WebFetchTool CreateTool()
    {
        var httpClient = new HttpClient(new Helpers.MockHttpMessageHandler());
        var config = new WebFetchConfig { MaxLengthChars = 20_000, TimeoutSeconds = 5 };
        return new WebFetchTool(config, httpClient);
    }
}
