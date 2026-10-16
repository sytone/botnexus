using System.IO.Abstractions.TestingHelpers;
using System.Text.Json;
using BotNexus.Agent.Providers.Core;
using BotNexus.Agent.Providers.Copilot.Headers;
using BotNexus.Gateway.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace BotNexus.Gateway.Api.Tests;

public sealed class CopilotHeaderAuthTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ExecutionScope_OnlyWhenSelectedOAuthSuppliedActualApiKey(bool exactPresent, bool equalAccess)
    {
        var fileSystem = new MockFileSystem();
        var authPath = Path.Combine(PlatformConfigLoader.GetDefaultConfigDirectory(fileSystem), "auth.json");
        fileSystem.Directory.CreateDirectory(Path.GetDirectoryName(authPath) ?? throw new InvalidOperationException());
        var entries = new Dictionary<string, AuthEntry>
        {
            ["account-b"] = Entry("oauth-b", "access-b")
        };
        if (exactPresent) entries["work"] = Entry("oauth-a", equalAccess ? "access-b" : "access-a");
        fileSystem.File.WriteAllText(authPath, JsonSerializer.Serialize(entries, JsonOptions));
        var monitor = Substitute.For<IOptionsMonitor<PlatformConfig>>();
        monitor.CurrentValue.Returns(new PlatformConfig
        {
            Providers = new() { ["work"] = new ProviderConfig { Type = "github-copilot", ApiKey = "auth:account-b" } }
        });
        var auth = new GatewayAuthManager(monitor, NullLogger<GatewayAuthManager>.Instance, fileSystem);
        var options = await auth.CreateExecutionOptionsAsync("work");
        options.ApiKey.ShouldBe(exactPresent && !equalAccess ? "access-a" : "access-b");
        var metadata = options.Metadata ?? throw new InvalidOperationException("Missing metadata");
        if (exactPresent) metadata[CopilotHeaderScope.MetadataKey].ShouldBe(CopilotHeaderAttribution.Unavailable);
        else
        {
            var scope = metadata[CopilotHeaderScope.MetadataKey].ShouldBeOfType<CopilotHeaderScope>();
            scope.Instance.ShouldBe("work");
            scope.Generation.ShouldBe(auth.ResolveCopilotAccountCredential("work")?.Generation);
            JsonSerializer.Serialize(scope).ShouldNotContain("oauth-b");
            JsonSerializer.Serialize(scope).ShouldNotContain("access-b");
        }
    }

    [Fact]
    public async Task ExecutionScope_RotationAndUnknownInstance_NeverReuseCallerScope()
    {
        var fileSystem = new MockFileSystem();
        var authPath = Path.Combine(PlatformConfigLoader.GetDefaultConfigDirectory(fileSystem), "auth.json");
        fileSystem.Directory.CreateDirectory(Path.GetDirectoryName(authPath) ?? throw new InvalidOperationException());
        fileSystem.File.WriteAllText(authPath, JsonSerializer.Serialize(new Dictionary<string, AuthEntry> { ["copilot"] = Entry("old-oauth", "old-access") }, JsonOptions));
        var monitor = Substitute.For<IOptionsMonitor<PlatformConfig>>();
        monitor.CurrentValue.Returns(new PlatformConfig());
        var auth = new GatewayAuthManager(monitor, NullLogger<GatewayAuthManager>.Instance, fileSystem);
        var old = await auth.CreateExecutionOptionsAsync("copilot");
        var oldMetadata = old.Metadata ?? throw new InvalidOperationException();
        var oldScope = oldMetadata[CopilotHeaderScope.MetadataKey].ShouldBeOfType<CopilotHeaderScope>();
        fileSystem.File.WriteAllText(authPath, JsonSerializer.Serialize(new Dictionary<string, AuthEntry> { ["copilot"] = Entry("longer-new-oauth", "new-access") }, JsonOptions));
        var current = await auth.CreateExecutionOptionsAsync("copilot", old);
        var currentScope = (current.Metadata ?? throw new InvalidOperationException())[CopilotHeaderScope.MetadataKey].ShouldBeOfType<CopilotHeaderScope>();
        currentScope.Generation.ShouldNotBe(oldScope.Generation);
        oldMetadata[CopilotHeaderScope.MetadataKey].ShouldBe(oldScope);
        var unknown = await auth.CreateExecutionOptionsAsync("unknown", new ProviderExecutionOptions { ApiKey = "synthetic", Metadata = new() { [CopilotHeaderScope.MetadataKey] = oldScope } });
        (unknown.Metadata ?? throw new InvalidOperationException())[CopilotHeaderScope.MetadataKey].ShouldBe(CopilotHeaderAttribution.Unavailable);
    }
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private sealed class AuthEntry
    {
        public string Type { get; init; } = "oauth";
        public string Refresh { get; init; } = "";
        public string Access { get; init; } = "";
        public long Expires { get; init; }
        public string Endpoint { get; init; } = "";
    }
    private static AuthEntry Entry(string refresh, string access) => new() { Type = "oauth", Refresh = refresh, Access = access, Expires = 4102444800000, Endpoint = "https://api.githubcopilot.com" };
}
