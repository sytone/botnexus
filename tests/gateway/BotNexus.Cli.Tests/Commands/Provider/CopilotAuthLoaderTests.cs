using System.Text.Json;
using BotNexus.Cli.Commands;
using BotNexus.Cli.Commands.Provider;

namespace BotNexus.Cli.Tests.Commands.Provider;

public sealed class CopilotAuthLoaderTests : IDisposable
{
    private readonly string _home = Path.Combine(Path.GetTempPath(), $"botnexus-copilot-auth-{Guid.NewGuid():N}");

    [Fact]
    public async Task LoadAsync_NamedInstance_ReadsOnlySelectedEntry()
    {
        Directory.CreateDirectory(_home);
        await WriteEntriesAsync(new Dictionary<string, ProviderCommand.AuthFileEntry>
        {
            ["github-copilot"] = Entry("default-refresh", "default-access", "https://api.individual.githubcopilot.com"),
            ["copilot-work"] = Entry("work-refresh", "work-access", "https://api.enterprise.githubcopilot.com")
        });

        var auth = await CopilotAuthLoader.LoadAsync(_home, "copilot-work");

        auth.ShouldNotBeNull();
        auth.GitHubToken.ShouldBe("work-refresh");
        auth.CopilotSessionToken.ShouldBe("work-access");
        auth.ApiEndpoint.ShouldBe("https://api.enterprise.githubcopilot.com");
    }

    [Fact]
    public async Task LoadAsync_CopilotAlias_UsesCanonicalDefaultEntry()
    {
        Directory.CreateDirectory(_home);
        await WriteEntriesAsync(new Dictionary<string, ProviderCommand.AuthFileEntry>
        {
            ["github-copilot"] = Entry("default-refresh", "default-access", "https://api.individual.githubcopilot.com"),
            ["copilot"] = Entry("wrong-refresh", "wrong-access", "https://api.enterprise.githubcopilot.com")
        });

        var auth = await CopilotAuthLoader.LoadAsync(_home, "copilot");

        auth.ShouldNotBeNull();
        auth.GitHubToken.ShouldBe("default-refresh");
        auth.CopilotSessionToken.ShouldBe("default-access");
    }

    private async Task WriteEntriesAsync(Dictionary<string, ProviderCommand.AuthFileEntry> entries)
    {
        var json = JsonSerializer.Serialize(entries, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });
        await File.WriteAllTextAsync(Path.Combine(_home, "auth.json"), json);
    }

    private static ProviderCommand.AuthFileEntry Entry(string refresh, string access, string endpoint) => new()
    {
        Type = "token",
        Refresh = refresh,
        Access = access,
        Expires = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds(),
        Endpoint = endpoint
    };

    public void Dispose()
    {
        if (Directory.Exists(_home))
            Directory.Delete(_home, recursive: true);
    }
}
