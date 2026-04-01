using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using BotNexus.Agent.Tools;
using BotNexus.Core.Models;
using BotNexus.Tools.GitHub;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Moq.Protected;

namespace BotNexus.Tests.Unit.Tests;

/// <summary>Tests for the <see cref="GitHubTool"/> extension library.</summary>
public class GitHubToolTests
{
    // ── Definition ────────────────────────────────────────────────────────────

    [Fact]
    public void GitHubTool_HasCorrectDefinitionName()
    {
        var tool = new GitHubTool(new GitHubToolsConfig());
        tool.Definition.Name.Should().Be("github");
    }

    [Fact]
    public void GitHubTool_DefinitionContainsExpectedParameters()
    {
        var tool = new GitHubTool(new GitHubToolsConfig());
        tool.Definition.Parameters.Should().ContainKeys("action", "owner", "repo", "number", "query", "state", "per_page");
    }

    [Fact]
    public void GitHubTool_IsRegisteredAsITool_WhenUsingAddGitHubTools()
    {
        var services = new ServiceCollection();
        services.AddGitHubTools(cfg => { cfg.Token = "test"; });
        var provider = services.BuildServiceProvider();

        var tools = provider.GetServices<BotNexus.Core.Abstractions.ITool>().ToList();
        tools.Should().HaveCount(1);
        tools[0].Should().BeOfType<GitHubTool>();
    }

    // ── Argument validation ───────────────────────────────────────────────────

    [Fact]
    public async Task GitHubTool_ReturnsError_WhenActionMissing()
    {
        var tool = new GitHubTool(new GitHubToolsConfig());
        var result = await tool.ExecuteAsync(new Dictionary<string, object?>());
        result.Should().StartWith("Error:");
    }

    [Fact]
    public async Task GitHubTool_ReturnsError_WhenOwnerMissing()
    {
        var tool = new GitHubTool(new GitHubToolsConfig());
        var result = await tool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["action"] = "get_repo",
            ["repo"] = "myrepo"
        });
        result.Should().StartWith("Error:");
    }

    [Fact]
    public async Task GitHubTool_ReturnsError_WhenRepoMissing()
    {
        var tool = new GitHubTool(new GitHubToolsConfig { DefaultOwner = "me" });
        var result = await tool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["action"] = "get_repo"
            // repo not supplied
        });
        result.Should().StartWith("Error:");
    }

    // ── HTTP mock tests ───────────────────────────────────────────────────────

    [Fact]
    public async Task GitHubTool_GetRepo_ParsesApiResponse()
    {
        var json = JsonSerializer.Serialize(new
        {
            full_name = "octocat/hello-world",
            description = "Hello World",
            language = "C#",
            stargazers_count = 42,
            forks_count = 3,
            open_issues_count = 1,
            default_branch = "main",
            html_url = "https://github.com/octocat/hello-world",
            visibility = "public",
            topics = new[] { "dotnet" }
        });

        var tool = BuildToolWithResponse(json);
        var result = await tool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["action"] = "get_repo",
            ["owner"] = "octocat",
            ["repo"] = "hello-world"
        });

        result.Should().Contain("octocat/hello-world");
        result.Should().Contain("Hello World");
    }

    [Fact]
    public async Task GitHubTool_ListIssues_ParsesApiResponse()
    {
        var json = JsonSerializer.Serialize(new[]
        {
            new { number = 1, title = "Bug fix", state = "open", user = new { login = "dev" },
                  created_at = "2024-01-01", html_url = "https://github.com/x/y/issues/1" }
        });

        var tool = BuildToolWithResponse(json);
        var result = await tool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["action"] = "list_issues",
            ["owner"] = "x",
            ["repo"] = "y"
        });

        result.Should().Contain("Bug fix");
    }

    [Fact]
    public async Task GitHubTool_ReturnsError_OnHttpFailure()
    {
        var tool = BuildToolWithResponse("{}", statusCode: HttpStatusCode.Unauthorized);
        var result = await tool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["action"] = "get_repo",
            ["owner"] = "x",
            ["repo"] = "y"
        });

        result.Should().StartWith("Error");
    }

    // ── RegisterFromServices ──────────────────────────────────────────────────

    [Fact]
    public void RegisterFromServices_AddsGitHubToolToRegistry()
    {
        var services = new ServiceCollection();
        services.AddGitHubTools();
        var provider = services.BuildServiceProvider();

        var registry = new ToolRegistry();
        registry.RegisterFromServices(provider);

        registry.Contains("github").Should().BeTrue();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static GitHubTool BuildToolWithResponse(string responseJson, HttpStatusCode statusCode = HttpStatusCode.OK)
    {
        var handlerMock = new Mock<HttpMessageHandler>();
        handlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = statusCode,
                Content = new StringContent(responseJson, System.Text.Encoding.UTF8, "application/json")
            });

        var httpClient = new HttpClient(handlerMock.Object)
        {
            BaseAddress = new Uri("https://api.github.com/")
        };

        return new GitHubTool(
            new GitHubToolsConfig { Token = "test" },
            httpClient: httpClient);
    }
}
