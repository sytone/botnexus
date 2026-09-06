using System.Net;
using System.Text.Json;

namespace BotNexus.Extensions.GitHub.Tests;

/// <summary>Behavioral contracts for the existing comment writer, not the four unimplemented write tools.</summary>
public sealed class GitHubWriteToolContractTests
{
    [Theory]
    [InlineData(null, "Sytone/botnexus", 2735)]
    [InlineData("another/private-repository", "another/private-repository", 42)]
    public async Task CommentWrite_UsesPostRestCommentsEndpoint_NotGraphQL(
        string? requestedRepository, string expectedRepository, int number)
    {
        var api = new RecordingGitHubApiClient().Returns(GitHubFixtures.Comment, 201);
        var config = GitHubFixtures.Config();
        const string body = "A comment, not a GraphQL mutation.";

        var result = await GitHubFixtures.InvokeJsonAsync(
            new GitHubIssueCommentTool(api, config),
            new() { ["repository"] = requestedRepository, ["number"] = number, ["body"] = body });

        var call = api.Calls.ShouldHaveSingleItem();
        call.Method.ShouldBe(HttpMethod.Post);
        call.Path.ShouldBe($"repos/{expectedRepository}/issues/{number}/comments");
        call.Path.ShouldNotContain("graphql");
        var payload = JsonSerializer.SerializeToElement(call.Body);
        payload.EnumerateObject().Select(p => p.Name).ShouldBe(["body"]);
        payload.GetProperty("body").GetString().ShouldBe(body);
        result.GetProperty("ok").GetBoolean().ShouldBeTrue();
        result.GetProperty("repository").GetString().ShouldBe(expectedRepository);
        result.GetProperty("identity").GetString().ShouldBe(config.Identity);
    }

    [Theory]
    [InlineData(403, null, "Sytone/botnexus", "configured app label")]
    [InlineData(404, null, "Sytone/botnexus", "configured app label")]
    [InlineData(403, "another/private-repository", "another/private-repository", "other configured label")]
    [InlineData(404, "another/private-repository", "another/private-repository", "other configured label")]
    public async Task CommentWrite_AccessDenied_NamesRepositoryAndConfiguredIdentity_WithoutCredentials(
        int status, string? requestedRepository, string expectedRepository, string configuredIdentity)
    {
        const string secret = "ghs_contract_test_synthetic_secret";
        const string commentBody = "Private request body must not be echoed in a failure.";
        var source = new CountingTokenSource(_ =>
            new GitHubInstallationToken(secret, DateTimeOffset.UtcNow.AddHours(1)));
        using var handler = new AccessDeniedHandler((HttpStatusCode)status, secret);
        using var http = new HttpClient(handler);
        var api = new HttpGitHubApiClient(http, new CachedGitHubCredentialProvider(source),
            new GitHubCredentialOptions { ApiBaseAddress = "https://api.github.test/" });
        var config = GitHubFixtures.Config();
        config.Identity = configuredIdentity;

        var text = await GitHubFixtures.InvokeAsync(new GitHubIssueCommentTool(api, config), new()
        {
            ["repository"] = requestedRepository,
            ["number"] = 2735,
            ["body"] = commentBody,
            ["identity"] = "caller cannot select identity",
            ["token"] = "caller cannot select credential",
        });

        handler.RequestCount.ShouldBe(1, "access denial must not trigger a fallback write");
        handler.SawConfiguredCredential.ShouldBeTrue("the real HTTP request must carry the synthetic credential");
        source.MintCount.ShouldBe(1);
        text.ShouldNotContain(secret);
        text.ShouldNotContain(commentBody);
        text.ShouldNotContain("caller cannot select");
        text.ShouldNotContain("Authorization");
        var result = JsonDocument.Parse(text).RootElement;
        result.GetProperty("tool").GetString().ShouldBe("github_issue_comment");
        result.GetProperty("ok").GetBoolean().ShouldBeFalse();
        result.GetProperty("status").GetInt32().ShouldBe(status);
        result.GetProperty("repository").GetString().ShouldBe(expectedRepository);
        result.GetProperty("error").GetString().ShouldBe("Repository access denied.");
        result.TryGetProperty("identity", out var identity).ShouldBeTrue("access failures must name the configured identity label");
        identity.GetString().ShouldBe(configuredIdentity);
        result.TryGetProperty("comment", out _).ShouldBeFalse();
    }

    // A transport-only seam: production credential attachment, response parsing, and tool projection
    // all run unchanged. The raw error body contains a sentinel that must not enter the tool result.
    private sealed class AccessDeniedHandler(HttpStatusCode status, string secret) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }
        public bool SawConfiguredCredential { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            SawConfiguredCredential = request.Headers.Authorization?.Scheme == "Bearer"
                && request.Headers.Authorization.Parameter == secret;
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(JsonSerializer.Serialize(new
                {
                    message = "Repository access denied.",
                    diagnostic = secret,
                })),
            });
        }
    }
}
