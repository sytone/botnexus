using BotNexus.Agent.Core.Tools;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using System.Text.Json;

namespace BotNexus.Extensions.BrowserTools.Tests;

/// <summary>
/// Exercises the real transport boundary, not a fake runner's network-policy claims (#4030).
/// No executable exists at the supplied path: attempting to start it produces a different error.
/// </summary>
public sealed class BrowserTransportFailClosedTests
{
    private static readonly string MissingBinary = Path.Combine(
        Path.GetTempPath(), "botnexus-4030-no-executable", "agent-browser-does-not-exist");

    [Theory]
    [InlineData("navigate", "https://example.com/")]
    [InlineData("navigate", "http://10.0.0.1/")]
    [InlineData("navigate", "http://169.254.169.254/")]
    [InlineData("navigate", "http://127.0.0.1/")]
    [InlineData("navigate", "http://[::1]/")]
    [InlineData("navigate", "http://[fc00::1]/")]
    [InlineData("url", "")]
    [InlineData("snapshot", "")]
    [InlineData("click", "#redirect")]
    [InlineData("type", "#submit")]
    [InlineData("screenshot", "capture.png")]
    [InlineData("close", "")]
    public async Task RunAsync_WithoutDestinationEnforcement_RefusesBeforeProcessStart(
        string command, string argument)
    {
        var runner = new AgentBrowserProcessRunner();
        var exception = await Should.ThrowAsync<AgentBrowserUnavailableException>(() =>
            runner.RunAsync(MissingBinary, ["--session", "4030", command, argument],
                new Dictionary<string, string>(), TimeSpan.FromSeconds(1)));

        exception.Message.ShouldContain("connection-bound destination enforcement");
        exception.Message.ShouldNotContain(MissingBinary);
        exception.Message.ShouldNotContain("could not be started");
    }

    [Theory]
    [InlineData("HTTP_PROXY")]
    [InlineData("HTTPS_PROXY")]
    [InlineData("ALL_PROXY")]
    [InlineData("NO_PROXY")]
    [InlineData("http_proxy")]
    [InlineData("https_proxy")]
    [InlineData("all_proxy")]
    [InlineData("no_proxy")]
    public async Task RunAsync_ProxyOrBypassEnvironment_CannotEnableTransport(string variable)
    {
        var runner = new AgentBrowserProcessRunner();
        var exception = await Should.ThrowAsync<AgentBrowserUnavailableException>(() =>
            runner.RunAsync(MissingBinary, ["navigate", "https://example.com/"],
                new Dictionary<string, string> { [variable] = "*" }, TimeSpan.FromSeconds(1)));

        exception.Message.ShouldContain("connection-bound destination enforcement");
        exception.Message.ShouldNotContain("could not be started");
    }

    [Theory]
    [InlineData("browser_navigate")]
    [InlineData("browser_snapshot")]
    [InlineData("browser_click")]
    [InlineData("browser_type")]
    [InlineData("browser_screenshot")]
    public async Task ContributedTool_WithRealRunner_ReturnsTransportDenial(string toolName)
    {
        var descriptor = new AgentDescriptor
        {
            AgentId = AgentId.From("transport-test"),
            DisplayName = "Transport test",
            ModelId = "test-model",
            ApiProvider = "test-provider",
            ExtensionConfig = new Dictionary<string, JsonElement>
            {
                [BrowserToolsContributor.ExtensionId] = JsonSerializer.SerializeToElement(new { }),
            },
        };
        var context = new AgentToolContributionContext(descriptor,
            new AgentExecutionContext { SessionId = SessionId.From("transport-session") },
            Path.GetTempPath(), null!, null, (_, _) => Task.FromResult<string?>(null));
        // Default runner is the production runner; only binary discovery and filesystem are isolated.
        var contributor = new BrowserToolsContributor(fileSystem: new FakeBrowserFileSystem(),
            resolver: _ => Task.FromResult(new AgentBrowserResolution(
                AgentBrowserSource.ConfiguredPath, MissingBinary, null)));
        var contribution = await contributor.ContributeAsync(context);
        var tool = contribution.Tools.Single(t => t.Name == toolName);
        var arguments = new Dictionary<string, object?>
        {
            ["url"] = "https://example.com/",
            ["selector"] = "#submit",
            ["text"] = "ordinary text",
        };
        var result = await tool.ExecuteAsync("4030-call", await tool.PrepareArgumentsAsync(arguments));
        var text = string.Join("\n", result.Content.Select(content => content.Value));

        text.ShouldContain("connection-bound destination enforcement");
        text.ShouldNotContain(MissingBinary);
        foreach (var resource in contribution.ResourcesToDispose ?? [])
        {
            if (resource is IAsyncDisposable disposable)
            {
                await disposable.DisposeAsync();
            }
        }
    }

    [Fact]
    public void Validate_PublicTarget_RemainsALexicalPositiveControl()
    {
        // Public lexical admission does not certify the external browser's actual destination.
        BrowserToolsUrlGuard.Validate("https://example.com/").IsAllowed.ShouldBeTrue();
    }
}
