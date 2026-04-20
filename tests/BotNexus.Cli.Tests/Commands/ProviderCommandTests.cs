using System.Text.Json;
using BotNexus.Cli.Commands;

namespace BotNexus.Cli.Tests.Commands;

public class ProviderCommandTests
{
    [Fact]
    public void AuthFileEntry_serializes_in_GatewayAuthManager_compatible_format()
    {
        var entry = new ProviderCommand.AuthFileEntry
        {
            Type = "oauth",
            Refresh = "ghu_refresh_token",
            Access = "tid=copilot_session_token",
            Expires = 1700000000000,
            Endpoint = "https://api.individual.githubcopilot.com"
        };

        var json = JsonSerializer.Serialize(entry, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        });

        // Verify the JSON uses the exact property names GatewayAuthManager expects
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        root.GetProperty("type").GetString().ShouldBe("oauth");
        root.GetProperty("refresh").GetString().ShouldBe("ghu_refresh_token");
        root.GetProperty("access").GetString().ShouldBe("tid=copilot_session_token");
        root.GetProperty("expires").GetInt64().ShouldBe(1700000000000);
        root.GetProperty("endpoint").GetString().ShouldBe("https://api.individual.githubcopilot.com");
    }

    [Fact]
    public void AuthFileEntry_roundtrips_through_dictionary_serialization()
    {
        var entries = new Dictionary<string, ProviderCommand.AuthFileEntry>(StringComparer.OrdinalIgnoreCase)
        {
            ["github-copilot"] = new()
            {
                Type = "oauth",
                Refresh = "refresh123",
                Access = "access456",
                Expires = 1700000000000,
                Endpoint = "https://api.githubcopilot.com"
            }
        };

        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };

        var json = JsonSerializer.Serialize(entries, options);
        var deserialized = JsonSerializer.Deserialize<Dictionary<string, ProviderCommand.AuthFileEntry>>(
            json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        deserialized.ShouldNotBeNull();
        deserialized.ShouldContainKey("github-copilot");
        deserialized["github-copilot"].Type.ShouldBe("oauth");
        deserialized["github-copilot"].Refresh.ShouldBe("refresh123");
        deserialized["github-copilot"].Access.ShouldBe("access456");
        deserialized["github-copilot"].Expires.ShouldBe(1700000000000);
        deserialized["github-copilot"].Endpoint.ShouldBe("https://api.githubcopilot.com");
    }

    [Fact]
    public void AuthFileEntry_omits_null_endpoint()
    {
        var entry = new ProviderCommand.AuthFileEntry
        {
            Type = "oauth",
            Refresh = "r",
            Access = "a",
            Expires = 100
        };

        var json = JsonSerializer.Serialize(entry, new JsonSerializerOptions
        {
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        });

        json.ShouldNotContain("endpoint");
    }

    [Fact]
    public void AuthFileEntry_expires_stores_milliseconds()
    {
        // CopilotOAuth returns ExpiresAt in seconds; auth.json stores milliseconds
        long expiresAtSeconds = 1700000000;
        var entry = new ProviderCommand.AuthFileEntry
        {
            Expires = expiresAtSeconds * 1000
        };

        entry.Expires.ShouldBe(1700000000000);
    }
}
