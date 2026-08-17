using System.Text.Json;
using BotNexus.Agent.Core.Tools;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using Shouldly;

namespace BotNexus.Extensions.GitHub.Tests;

/// <summary>
/// The structural guarantees of the GitHub tool surface (#2627 AC2, AC3, AC9).
/// </summary>
/// <remarks>
/// These are the tests that make the token/identity properties true by CONSTRUCTION rather than by
/// review. A behavioural test can only show that today's tools do not take a token; enumerating the
/// registered schemas shows that no future tool can add one without reddening a named test.
/// </remarks>
public sealed class GitHubToolSurfaceTests
{
    /// <summary>
    /// Substrings that would indicate a credential or identity leaked into a tool's schema.
    /// </summary>
    /// <remarks>
    /// <c>pat</c> is deliberately NOT here: as a substring it matches the perfectly legitimate
    /// <c>path</c> parameter on <c>github_api</c>, so it is checked as a whole name in
    /// <see cref="IsForbiddenParameterName"/> instead. Keeping it as a fragment would have made this
    /// test fire on a correct schema - a detector that cries wolf gets deleted, which is a worse
    /// outcome than one that is precise.
    /// </remarks>
    private static readonly string[] ForbiddenParameterFragments =
        ["token", "credential", "secret", "password", "auth", "identity", "account", "privatekey"];

    /// <summary>Exact parameter names that are forbidden even though they are not distinctive substrings.</summary>
    private static readonly string[] ForbiddenParameterNames = ["pat", "pats"];

    /// <summary>True when <paramref name="name"/> names a credential or identity argument.</summary>
    private static bool IsForbiddenParameterName(string name)
    {
        var lowered = name.ToLowerInvariant();
        return ForbiddenParameterNames.Contains(lowered, StringComparer.Ordinal)
               || ForbiddenParameterFragments.Any(f => lowered.Contains(f, StringComparison.Ordinal));
    }

    private static IReadOnlyList<IAgentTool> AllTools()
    {
        var api = new RecordingGitHubApiClient();
        var config = GitHubFixtures.Config();
        return
        [
            new GitHubIssueGetTool(api, config),
            new GitHubIssueListTool(api, config),
            new GitHubIssueCommentTool(api, config),
            new GitHubPullRequestGetTool(api, config),
            new GitHubPullRequestListTool(api, config),
            new GitHubApiTool(api, config),
        ];
    }

    // ---- AC2/AC3: no tool takes a token, credential, or identity argument ----------------------

    [Fact]
    public void NoToolSchema_DeclaresATokenCredentialOrIdentityParameter()
    {
        var offenders = new List<string>();

        foreach (var tool in AllTools())
        {
            var schema = tool.Definition.Parameters;
            if (!schema.TryGetProperty("properties", out var properties))
                continue;

            foreach (var property in properties.EnumerateObject())
            {
                if (IsForbiddenParameterName(property.Name))
                    offenders.Add($"{tool.Name}.{property.Name}");
            }
        }

        offenders.ShouldBeEmpty(
            "no GitHub tool may accept a credential or identity argument (#2627 AC2/AC3): " +
            string.Join(", ", offenders));
    }

    [Theory]
    [InlineData("token")]
    [InlineData("ghToken")]
    [InlineData("pat")]
    [InlineData("credential")]
    [InlineData("identity")]
    [InlineData("privateKeyPath")]
    public void ForbiddenParameterDetector_RejectsCredentialShapedNames(string name) =>
        IsForbiddenParameterName(name).ShouldBeTrue();

    [Theory]
    [InlineData("path")]
    [InlineData("repository")]
    [InlineData("number")]
    [InlineData("perPage")]
    public void ForbiddenParameterDetector_AcceptsLegitimateNames(string name) =>
        // Precision half of the detector: 'path' contains 'pat' as a substring, and treating that as
        // a hit made this suite fail on a correct schema.
        IsForbiddenParameterName(name).ShouldBeFalse();

    [Fact]
    public void EveryToolSchema_DeclaresAtLeastOneProperty()
    {
        // Vacuity guard for the assertion above: a tool whose schema had no properties at all would
        // pass the offender scan for the wrong reason.
        foreach (var tool in AllTools())
        {
            tool.Definition.Parameters.TryGetProperty("properties", out var properties)
                .ShouldBeTrue($"{tool.Name} must declare a parameters object");
            properties.EnumerateObject().Any().ShouldBeTrue($"{tool.Name} must declare parameters");
        }
    }

    [Fact]
    public void ToolNames_AreUniqueAndPrefixed()
    {
        var names = AllTools().Select(t => t.Name).ToArray();

        names.Distinct(StringComparer.OrdinalIgnoreCase).Count().ShouldBe(names.Length);
        names.ShouldAllBe(n => n.StartsWith("github_", StringComparison.Ordinal));
    }

    // ---- AC9: the token never reaches a tool result or an error message ------------------------

    [Fact]
    public async Task ToolResults_NeverContainTheInstallationTokenValue()
    {
        const string secret = "ghs_never_should_appear_in_a_result";

        // A real credential provider over a real token, driving the real HTTP client seam: this is
        // the path where a leak could actually occur, not a stubbed stand-in for it.
        var handler = new StubHandler(secret);
        var provider = new CachedGitHubCredentialProvider(
            new FixedTokenSource(new GitHubInstallationToken(secret, DateTimeOffset.UtcNow.AddHours(1))));
        var api = new HttpGitHubApiClient(
            new HttpClient(handler),
            provider,
            new GitHubCredentialOptions { ApiBaseAddress = "https://api.github.test/" });

        var config = GitHubFixtures.Config();
        var results = new List<string>();

        foreach (var invocation in new (GitHubToolBase Tool, Dictionary<string, object?> Args)[]
                 {
                     (new GitHubIssueGetTool(api, config), new() { ["number"] = 1 }),
                     (new GitHubIssueListTool(api, config), new()),
                     (new GitHubIssueCommentTool(api, config), new() { ["number"] = 1, ["body"] = "hi" }),
                     (new GitHubPullRequestGetTool(api, config), new() { ["number"] = 1 }),
                     (new GitHubPullRequestListTool(api, config), new()),
                     (new GitHubApiTool(api, config), new() { ["path"] = "user" }),
                 })
        {
            results.Add(await GitHubFixtures.InvokeAsync(invocation.Tool, invocation.Args));
        }

        results.Count.ShouldBe(6, "vacuity guard: every tool must have produced a result to scan");
        handler.SawAuthorizationHeader.ShouldBeTrue(
            "vacuity guard: the credential must actually have been attached, or this test proves nothing");

        foreach (var result in results)
        {
            result.ShouldNotContain(secret);
        }
    }

    [Fact]
    public async Task ToolErrorResults_NeverContainTheInstallationTokenValue()
    {
        const string secret = "ghs_never_should_appear_in_an_error";

        var handler = new StubHandler(secret, failWithStatus: System.Net.HttpStatusCode.Unauthorized);
        var api = new HttpGitHubApiClient(
            new HttpClient(handler),
            new CachedGitHubCredentialProvider(
                new FixedTokenSource(new GitHubInstallationToken(secret, DateTimeOffset.UtcNow.AddHours(1)))),
            new GitHubCredentialOptions { ApiBaseAddress = "https://api.github.test/" });

        var text = await GitHubFixtures.InvokeAsync(
            new GitHubIssueGetTool(api, GitHubFixtures.Config()), new() { ["number"] = 1 });

        text.ShouldNotContain(secret);
        JsonDocument.Parse(text).RootElement.GetProperty("status").GetInt32().ShouldBe(401);
    }

    [Fact]
    public void NoToolType_ExposesAPublicMemberReturningTheCredentialProviderOrToken()
    {
        // Structural half of AC9: a public accessor would defeat every runtime redaction above.
        var offenders = AllTools()
            .SelectMany(t => t.GetType().GetProperties().Select(p => (Tool: t.Name, Member: p.Name, Type: p.PropertyType)))
            .Where(m => m.Type == typeof(GitHubInstallationToken)
                        || m.Type == typeof(IGitHubCredentialProvider)
                        || m.Type == typeof(CachedGitHubCredentialProvider))
            .Select(m => m.Tool + "." + m.Member)
            .ToArray();

        offenders.ShouldBeEmpty(
            "a GitHub tool must not expose the credential on its public surface: " + string.Join(", ", offenders));
    }

    // ---- AC3: identity comes from configuration, and the contributor honours it ----------------

    [Fact]
    public async Task Contributor_WithNoGitHubConfiguration_ContributesNoTools()
    {
        var contributor = new GitHubToolsContributor(_ => new RecordingGitHubApiClient());

        var contribution = await contributor.ContributeAsync(ContextFor(new Dictionary<string, JsonElement>()));

        // No tools rather than tools that fail at call time: a visible-but-unusable tool is a turn
        // tax paid on every prompt.
        contribution.Tools.ShouldBeEmpty();
    }

    [Fact]
    public async Task Contributor_WithGitHubConfiguration_ContributesTheFullToolSet()
    {
        var contributor = new GitHubToolsContributor(_ => new RecordingGitHubApiClient());

        var contribution = await contributor.ContributeAsync(ContextFor(ConfigElement(
            """{"defaultRepository":"Sytone/botnexus","identity":"agent-farnsworth[bot]"}""")));

        contribution.Tools.Select(t => t.Name).ShouldBe(
            [
                "github_issue_get", "github_issue_list", "github_issue_comment",
                "github_pr_get", "github_pr_list", "github_api",
            ],
            ignoreOrder: true);
    }

    [Fact]
    public void Contributor_ResolvesTheActingIdentityFromConfiguration()
    {
        var config = GitHubToolsContributor.ResolveConfig(DescriptorFor(ConfigElement(
            """{"defaultRepository":"Sytone/botnexus","identity":"agent-farnsworth[bot]"}""")));

        config.ShouldNotBeNull();
        config!.Identity.ShouldBe("agent-farnsworth[bot]");
        config.DefaultRepository.ShouldBe("Sytone/botnexus");
    }

    [Fact]
    public void Contributor_WithMalformedConfiguration_ContributesNothingRatherThanWrongBounds()
    {
        var config = GitHubToolsContributor.ResolveConfig(DescriptorFor(ConfigElement("\"not-an-object\"")));

        config.ShouldBeNull();
    }

    [Theory]
    [InlineData("""{"maxPageSize":0}""")]
    [InlineData("""{"defaultPageSize":0}""")]
    [InlineData("""{"defaultPageSize":500,"maxPageSize":10}""")]
    public void Contributor_NormalisesOutOfRangePageBounds(string json)
    {
        // A configured 0 would otherwise surface as an exception deep inside a tool call, where it
        // reads as a GitHub failure rather than a configuration one.
        var config = GitHubToolsContributor.ResolveConfig(DescriptorFor(ConfigElement(json)));

        config.ShouldNotBeNull();
        config!.MaxPageSize.ShouldBeGreaterThan(0);
        config.DefaultPageSize.ShouldBeGreaterThan(0);
        config.DefaultPageSize.ShouldBeLessThanOrEqualTo(config.MaxPageSize);
    }

    private static Dictionary<string, JsonElement> ConfigElement(string json) =>
        new(StringComparer.Ordinal)
        {
            [GitHubToolsConfig.ExtensionId] = JsonDocument.Parse(json).RootElement.Clone(),
        };

    private static AgentDescriptor DescriptorFor(Dictionary<string, JsonElement> extensionConfig) =>
        new()
        {
            AgentId = AgentId.From("farnsworth"),
            DisplayName = "Farnsworth",
            ModelId = "claude-opus-5",
            ApiProvider = "github-copilot",
            ExtensionConfig = extensionConfig,
        };

    private static AgentToolContributionContext ContextFor(Dictionary<string, JsonElement> extensionConfig) =>
        new(
            DescriptorFor(extensionConfig),
            new BotNexus.Gateway.Abstractions.Models.AgentExecutionContext { SessionId = SessionId.From("session-2627") },
            WorkspacePath: Path.GetTempPath(),
            PathValidator: null!,
            CopilotMcpEndpoint: null,
            GetProviderApiKeyAsync: (_, _) => Task.FromResult<string?>(null));

    /// <summary>A token source that always returns the same token, for leak-scan tests.</summary>
    private sealed class FixedTokenSource : IGitHubInstallationTokenSource
    {
        private readonly GitHubInstallationToken _token;

        public FixedTokenSource(GitHubInstallationToken token) => _token = token;

        public Task<GitHubInstallationToken> MintAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(_token);
    }

    /// <summary>
    /// Answers every request with a benign payload and records whether the credential was attached.
    /// The recorded flag is the vacuity guard: without it, a leak scan over results produced by an
    /// unauthenticated request would pass trivially.
    /// </summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly string _expectedToken;
        private readonly System.Net.HttpStatusCode _status;

        public StubHandler(string expectedToken, System.Net.HttpStatusCode? failWithStatus = null)
        {
            _expectedToken = expectedToken;
            _status = failWithStatus ?? System.Net.HttpStatusCode.OK;
        }

        public bool SawAuthorizationHeader { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.Headers.Authorization?.Parameter == _expectedToken)
                SawAuthorizationHeader = true;

            var isList = request.RequestUri!.AbsolutePath.EndsWith("issues", StringComparison.Ordinal)
                         || request.RequestUri.AbsolutePath.EndsWith("pulls", StringComparison.Ordinal);

            var payload = _status == System.Net.HttpStatusCode.OK
                ? (isList ? "[]" : GitHubFixtures.Issue)
                : """{"message":"Bad credentials"}""";

            return Task.FromResult(new HttpResponseMessage(_status)
            {
                Content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json"),
            });
        }
    }
}
