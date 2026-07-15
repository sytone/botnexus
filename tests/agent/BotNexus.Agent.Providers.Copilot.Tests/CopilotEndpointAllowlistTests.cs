using BotNexus.Agent.Providers.Copilot;

namespace BotNexus.Agent.Providers.Copilot.Tests;

/// <summary>
/// Guards the https-only + host-allowlist gate applied to the peer-controlled
/// <c>endpoints.api</c> value advertised by the Copilot token-exchange response before it is
/// allowed to flow onto <see cref="Core.Models.LlmModel.BaseUrl"/> and carry the bearer token
/// (#2006, mirrors OpenClaw #105584). A hostile endpoint must not be able to steer the bearer
/// token to an attacker-chosen host.
/// </summary>
public class CopilotEndpointAllowlistTests
{
    // --- Happy path: legitimate Copilot hosts pass ---

    [Theory]
    [InlineData("https://api.individual.githubcopilot.com")]
    [InlineData("https://api.githubcopilot.com")]
    [InlineData("https://api.enterprise.githubcopilot.com")]
    [InlineData("https://api.business.githubcopilot.com/")]
    public void IsAllowed_ValidGithubCopilotHost_ReturnsTrue(string endpoint)
    {
        CopilotEndpointAllowlist.IsAllowedApiEndpoint(endpoint).ShouldBeTrue();
    }

    [Fact]
    public void IsAllowed_ExactApexGithubCopilotDomain_ReturnsTrue()
    {
        CopilotEndpointAllowlist.IsAllowedApiEndpoint("https://githubcopilot.com").ShouldBeTrue();
    }

    // --- Sad path: hostile host is rejected ---

    [Theory]
    [InlineData("https://evil.com")]
    [InlineData("https://attacker.example.com")]
    [InlineData("https://api.githubcopilot.com.evil.com")]
    [InlineData("https://evilgithubcopilot.com")]
    [InlineData("https://githubcopilot.com.attacker.net")]
    public void IsAllowed_HostileHost_ReturnsFalse(string endpoint)
    {
        CopilotEndpointAllowlist.IsAllowedApiEndpoint(endpoint).ShouldBeFalse();
    }

    // --- Sad path: non-https schemes are rejected ---

    [Theory]
    [InlineData("http://api.githubcopilot.com")]
    [InlineData("ftp://api.githubcopilot.com")]
    [InlineData("ws://api.githubcopilot.com")]
    public void IsAllowed_NonHttpsScheme_ReturnsFalse(string endpoint)
    {
        CopilotEndpointAllowlist.IsAllowedApiEndpoint(endpoint).ShouldBeFalse(
            "only https may carry the bearer token");
    }

    // --- Edge: null / empty / malformed ---

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-uri")]
    [InlineData("/relative/path")]
    [InlineData("api.githubcopilot.com")]
    public void IsAllowed_NullEmptyOrMalformed_ReturnsFalse(string? endpoint)
    {
        CopilotEndpointAllowlist.IsAllowedApiEndpoint(endpoint).ShouldBeFalse();
    }

    // --- Explicitly-configured enterprise host allowance ---

    [Fact]
    public void IsAllowed_ConfiguredEnterpriseHost_ReturnsTrue()
    {
        CopilotEndpointAllowlist
            .IsAllowedApiEndpoint("https://copilot.contoso-ghes.example", enterpriseHost: "copilot.contoso-ghes.example")
            .ShouldBeTrue("an explicitly-configured enterprise host is trusted");
    }

    [Fact]
    public void IsAllowed_ConfiguredEnterpriseHostAsUrl_ReturnsTrue()
    {
        // The configured enterprise host may be supplied as a full URL; only the host is compared.
        CopilotEndpointAllowlist
            .IsAllowedApiEndpoint("https://copilot.contoso-ghes.example/api", enterpriseHost: "https://copilot.contoso-ghes.example")
            .ShouldBeTrue();
    }

    [Fact]
    public void IsAllowed_HostileHost_NotRescuedByDifferentEnterpriseHost()
    {
        CopilotEndpointAllowlist
            .IsAllowedApiEndpoint("https://evil.com", enterpriseHost: "copilot.contoso-ghes.example")
            .ShouldBeFalse();
    }

    [Fact]
    public void IsAllowed_ConfiguredEnterpriseHost_StillRequiresHttps()
    {
        CopilotEndpointAllowlist
            .IsAllowedApiEndpoint("http://copilot.contoso-ghes.example", enterpriseHost: "copilot.contoso-ghes.example")
            .ShouldBeFalse("even a configured enterprise host must be reached over https");
    }

    // --- Sanitising helper: returns validated endpoint or null ---

    [Fact]
    public void Sanitise_ValidEndpoint_ReturnsIt()
    {
        CopilotEndpointAllowlist
            .SanitiseApiEndpoint("https://api.githubcopilot.com")
            .ShouldBe("https://api.githubcopilot.com");
    }

    [Fact]
    public void Sanitise_HostileEndpoint_ReturnsNull()
    {
        CopilotEndpointAllowlist.SanitiseApiEndpoint("https://evil.com").ShouldBeNull();
    }

    [Fact]
    public void Sanitise_NullEndpoint_ReturnsNull()
    {
        CopilotEndpointAllowlist.SanitiseApiEndpoint(null).ShouldBeNull();
    }
}
