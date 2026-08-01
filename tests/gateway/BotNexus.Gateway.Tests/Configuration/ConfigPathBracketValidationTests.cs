using BotNexus.Gateway.Configuration;

namespace BotNexus.Gateway.Tests.Configuration;

/// <summary>
/// Pins the bracket-balance rejection added for #2605 and, just as importantly, pins the
/// well-formed path shapes that must keep parsing exactly as they did before it. The parser sits
/// under <c>config get</c>/<c>config set</c>, so a validator that rejected a path working today
/// would be a regression rather than hardening.
/// </summary>
public sealed class ConfigPathBracketValidationTests
{
    private readonly ConfigPathResolver _resolver = new();

    // ---- malformed shapes: rejected, with the position named ----

    [Theory]
    [InlineData("a]b", 2)]
    [InlineData("agents.my]agent.model", 10)]
    [InlineData("a[0]]", 5)]
    public void TryGetValue_UnmatchedCloser_IsRejectedWithPosition(string path, int position)
    {
        var ok = _resolver.TryGetValue(new PlatformConfig(), path, out _, out var error);

        ok.ShouldBeFalse();
        error.ShouldContain("unmatched ']'");
        error.ShouldContain($"position {position}");
        error.ShouldContain(path);
    }

    [Theory]
    [InlineData("a[0", 2)]
    [InlineData("a.b[0.c", 4)]
    [InlineData("a[b", 2)]
    [InlineData("a[[0]", 2)]
    public void TryGetValue_UnclosedOpener_IsRejectedWithPosition(string path, int position)
    {
        var ok = _resolver.TryGetValue(new PlatformConfig(), path, out _, out var error);

        ok.ShouldBeFalse();
        error.ShouldContain("unclosed '['");
        error.ShouldContain($"position {position}");
        error.ShouldContain(path);
    }

    [Theory]
    [InlineData("a]b")]
    [InlineData("a.b[0.c")]
    [InlineData("a[0")]
    public void TrySetValue_MalformedPath_ReturnsFalseAndWritesNothing(string path)
    {
        var config = new PlatformConfig
        {
            Gateway = new GatewaySettingsConfig { ListenUrl = "http://localhost:5005" }
        };
        var providerCountBefore = config.Providers?.Count ?? 0;
        var agentCountBefore = config.Agents?.Count ?? 0;

        var ok = _resolver.TrySetValue(config, path, "value", out var error);

        ok.ShouldBeFalse();
        error.ShouldNotBeEmpty();
        // No key was created anywhere on the graph, and nothing existing was mutated.
        (config.Providers?.Count ?? 0).ShouldBe(providerCountBefore);
        (config.Agents?.Count ?? 0).ShouldBe(agentCountBefore);
        config.Gateway!.ListenUrl.ShouldBe("http://localhost:5005");
    }

    [Fact]
    public void TrySetValue_UnmatchedCloser_DoesNotCreateMisnamedDictionaryKey()
    {
        // Before #2605 the stray ']' was absorbed into the segment and a dictionary entry
        // literally named "my]agent" was created and written - reported to the caller as success.
        var config = new PlatformConfig();

        var ok = _resolver.TrySetValue(config, "agents.my]agent.model", "gpt-4", out var error);

        ok.ShouldBeFalse();
        error.ShouldContain("unmatched ']'");
        (config.Agents?.ContainsKey("my]agent") ?? false).ShouldBeFalse();
        (config.Agents?.Count ?? 0).ShouldBe(0);
    }

    // ---- valid inventory: parity, asserted not inspected ----

    [Theory]
    [InlineData("gateway.listenUrl")]
    [InlineData("gateway.defaultAgentId")]
    [InlineData("agents.assistant.model")]
    [InlineData("agents.assistant.enabled")]
    [InlineData("agents.coder.enabled")]
    [InlineData("gateway.enableProviderRequestLogging")]
    [InlineData("world.id")]
    [InlineData("world.displayName")]
    [InlineData("PROVIDERS.copilot.apiKey")]
    [InlineData("gateway.cors.allowedOrigins[0]")]
    [InlineData("gateway.cors.allowedOrigins[1]")]
    [InlineData("gateway.cors.allowedOrigins[5]")]
    [InlineData("gateway.cors.allowedOrigins.0")]
    [InlineData("gateway.cors.allowedOrigins")]
    [InlineData("a[0][1]")]
    [InlineData("a.b[0].c")]
    [InlineData("gateway.apiKeys.*.apiKey")]
    [InlineData("gateway.locations.*.connectionString")]
    [InlineData("gateway.crossWorld.peers.*.apiKey")]
    [InlineData("gateway.satellites.*.apiKey")]
    [InlineData("gateway.sessionStore.connectionString")]
    [InlineData("gateway.rateLimit.requestsPerMinute")]
    [InlineData("gateway.rateLimit.enabled")]
    [InlineData("gateway.auxiliary.titling")]
    [InlineData("agents.assistant.provider")]
    [InlineData("agents.assistant.memory.promptInjection")]
    [InlineData("platformVersion")]
    [InlineData("a.b.c")]
    public void ValidPaths_AreNotRejectedByBracketValidation(string path)
    {
        _resolver.TryGetValue(new PlatformConfig(), path, out _, out var error);

        // The path may legitimately fail to resolve against an empty config; what must never
        // happen is a *syntax* rejection of a shape that is in real operator use.
        error.ShouldNotContain("unmatched ']'");
        error.ShouldNotContain("unclosed '['");
    }

    [Fact]
    public void ValidNestedPath_StillResolvesUnchanged()
    {
        var config = new PlatformConfig
        {
            Gateway = new GatewaySettingsConfig { ListenUrl = "http://localhost:5005" }
        };

        var ok = _resolver.TryGetValue(config, "gateway.listenUrl", out var value, out var error);

        ok.ShouldBeTrue(error);
        value.ShouldBe("http://localhost:5005");
    }

    [Fact]
    public void ValidIndexedPath_StillSetsThroughList()
    {
        var config = new PlatformConfig();

        var ok = _resolver.TrySetValue(config, "gateway.cors.allowedOrigins[0]", "http://a", out var error);

        ok.ShouldBeTrue(error);
        _resolver.TryGetValue(config, "gateway.cors.allowedOrigins[0]", out var value, out _).ShouldBeTrue();
        value.ShouldBe("http://a");
    }

    [Fact]
    public void EmptySegment_StillReportsTheExistingEmptySegmentError()
    {
        // Bracket validation must run without displacing the pre-existing segment diagnostics.
        // "." splits to zero segments today, which TryParsePath accepts as an empty token list;
        // whatever that behaviour is, bracket validation must not have changed it.
        var ok = _resolver.TryGetValue(new PlatformConfig(), ".", out _, out var error);

        ok.ShouldBeTrue(error);
        error.ShouldNotContain("unmatched ']'");
        error.ShouldNotContain("unclosed '['");
    }
}
