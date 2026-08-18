using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using Shouldly;

namespace BotNexus.Extensions.GitHub.Tests;

/// <summary>
/// Per-agent acting-identity resolution (#2733).
/// </summary>
/// <remarks>
/// <para><b>What these tests defend.</b> Before #2733 the acting GitHub identity was ambient process
/// state, so "which account acted" was not a value the program held. The rejection tests assert on
/// the <i>message content</i>, not merely that something threw: a fail-closed error that does not
/// name the configuration key sends the reader back to the EMU 403 that names the identity, which is
/// the misdirection that made <c>gh auth switch</c> look like the remedy 287 times.</para>
/// <para><b>The concurrency test is the load-bearing one (AC4/AC5).</b> It runs two differently
/// configured agents through the resolver simultaneously and asserts every observed identity matches
/// the requesting agent. Reverting resolution to a single shared identity — for example returning one
/// cached provider regardless of agent id — reddens
/// <see cref="TwoAgents_ResolvingConcurrently_NeverObserveEachOthersIdentity"/> by name, because the
/// per-request recorded identity stops correlating with the requesting agent.</para>
/// </remarks>
public sealed class GitHubIdentityResolutionTests
{
    private const string AlphaAgent = "agent-alpha";
    private const string BetaAgent = "agent-beta";

    // ---------- AC1: identity comes from configuration, keyed by agent id ----------

    [Fact]
    public void ResolveIdentity_ReturnsTheProfileConfiguredForThatAgent()
    {
        var resolver = ResolverFor(TwoIdentityOptions());

        var alpha = resolver.ResolveIdentity(AgentId.From(AlphaAgent));
        var beta = resolver.ResolveIdentity(AgentId.From(BetaAgent));

        alpha.Name.ShouldBe("alpha-app");
        alpha.AppId.ShouldBe("1001");
        alpha.InstallationId.ShouldBe("2001");

        beta.Name.ShouldBe("beta-app");
        beta.AppId.ShouldBe("1002");
        beta.InstallationId.ShouldBe("2002");
    }

    [Fact]
    public void ResolveCredentialProvider_GivesDifferentAgentsDifferentProviders()
    {
        var resolver = ResolverFor(TwoIdentityOptions());

        resolver.ResolveCredentialProvider(AgentId.From(AlphaAgent))
            .ShouldNotBeSameAs(resolver.ResolveCredentialProvider(AgentId.From(BetaAgent)));
    }

    [Fact]
    public void ResolveCredentialProvider_ReusesOneProviderPerIdentity_SoOneTokenCacheServesTheProfile()
    {
        var resolver = ResolverFor(TwoIdentityOptions());

        resolver.ResolveCredentialProvider(AgentId.From(AlphaAgent))
            .ShouldBeSameAs(resolver.ResolveCredentialProvider(AgentId.From(AlphaAgent)));
    }

    [Fact]
    public void ResolveCredentialProvider_TwoAgentsOnTheSameProfile_ShareOneProvider()
    {
        var options = TwoIdentityOptions();
        options.AgentIdentities["agent-gamma"] = "alpha-app";
        var resolver = ResolverFor(options);

        resolver.ResolveCredentialProvider(AgentId.From("agent-gamma"))
            .ShouldBeSameAs(resolver.ResolveCredentialProvider(AgentId.From(AlphaAgent)));
    }

    // ---------- AC3: fail closed, naming the configuration key ----------

    [Fact]
    public void ResolveIdentity_ForAnUnmappedAgent_NamesTheAgentIdentityConfigurationKey()
    {
        var resolver = ResolverFor(TwoIdentityOptions());

        var ex = Should.Throw<GitHubCredentialException>(() => resolver.ResolveIdentity(AgentId.From("agent-unknown")));

        // The KEY, not just "unauthorised": the reader must be able to go straight to the setting.
        ex.Message.ShouldContain("GitHub:agentIdentities:agent-unknown");
        ex.Message.ShouldContain("GitHub:identities");
    }

    [Fact]
    public void ResolveIdentity_WhenTheNamedProfileIsAbsent_NamesTheMissingProfileKey()
    {
        var options = TwoIdentityOptions();
        options.AgentIdentities["agent-dangling"] = "ghost-app";
        var resolver = ResolverFor(options);

        var ex = Should.Throw<GitHubCredentialException>(() => resolver.ResolveIdentity(AgentId.From("agent-dangling")));

        ex.Message.ShouldContain("GitHub:identities:ghost-app");
    }

    [Fact]
    public void ResolveIdentity_WhenTheProfileIsIncomplete_NamesEveryMissingLeafKey()
    {
        var options = TwoIdentityOptions();
        options.Identities["partial-app"] = new GitHubIdentityOptions { AppId = "1003" };
        options.AgentIdentities["agent-partial"] = "partial-app";
        var resolver = ResolverFor(options);

        var ex = Should.Throw<GitHubCredentialException>(() => resolver.ResolveIdentity(AgentId.From("agent-partial")));

        ex.Message.ShouldContain("GitHub:identities:partial-app:installationId");
        ex.Message.ShouldContain("GitHub:identities:partial-app:privateKeyPath");
        // The one that IS configured must not be reported as missing - a message that names every
        // key regardless is no more useful than one that names none.
        ex.Message.ShouldNotContain("partial-app:appId");
    }

    [Fact]
    public void ResolveIdentity_ErrorMessages_NeverEchoConfiguredValues()
    {
        var options = TwoIdentityOptions();
        options.Identities["secretish-app"] = new GitHubIdentityOptions
        {
            AppId = "9999",
            PrivateKeyPath = "/etc/very-secret-location/app.pem",
        };
        options.AgentIdentities["agent-secretish"] = "secretish-app";
        var resolver = ResolverFor(options);

        var ex = Should.Throw<GitHubCredentialException>(() => resolver.ResolveIdentity(AgentId.From("agent-secretish")));

        ex.Message.ShouldNotContain("very-secret-location");
        ex.Message.ShouldNotContain("9999");
    }

    [Fact]
    public async Task AgentScopedProvider_ForAnUnmappedAgent_FailsClosedOnAuthenticate()
    {
        var resolver = ResolverFor(TwoIdentityOptions());
        var provider = new AgentScopedGitHubCredentialProvider(resolver, AgentId.From("agent-unknown"));
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/rate_limit");

        var ex = await Should.ThrowAsync<GitHubCredentialException>(() => provider.AuthenticateAsync(request));

        ex.Message.ShouldContain("GitHub:agentIdentities:agent-unknown");
        // Fail CLOSED: no header may be attached on the failure path.
        request.Headers.Authorization.ShouldBeNull();
    }

    // ---------- AC4/AC5: concurrent independence ----------

    [Fact]
    public async Task TwoAgents_ResolvingConcurrently_NeverObserveEachOthersIdentity()
    {
        var options = TwoIdentityOptions();
        var observations = new ConcurrentBag<(string Agent, string Identity)>();

        // Each identity's provider stamps ITS OWN identity name into the request. If resolution
        // collapsed to one shared ambient identity, agent-beta's requests would carry alpha-app (or
        // vice versa) and the correlation assertion below fails.
        var resolver = new ConfiguredGitHubIdentityResolver(
            options,
            identity => new StampingCredentialProvider(identity.Name));

        var alpha = new AgentScopedGitHubCredentialProvider(resolver, AgentId.From(AlphaAgent));
        var beta = new AgentScopedGitHubCredentialProvider(resolver, AgentId.From(BetaAgent));

        const int IterationsPerAgent = 200;
        using var barrier = new Barrier(2);

        async Task DriveAsync(AgentScopedGitHubCredentialProvider provider, string agentId)
        {
            barrier.SignalAndWait();
            for (var i = 0; i < IterationsPerAgent; i++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/rate_limit");
                await provider.AuthenticateAsync(request);
                observations.Add((agentId, request.Headers.Authorization!.Parameter!));
            }
        }

        await Task.WhenAll(
            Task.Run(() => DriveAsync(alpha, AlphaAgent)),
            Task.Run(() => DriveAsync(beta, BetaAgent)));

        observations.Count.ShouldBe(IterationsPerAgent * 2);

        var expected = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [AlphaAgent] = "alpha-app",
            [BetaAgent] = "beta-app",
        };

        foreach (var (agent, identity) in observations)
        {
            identity.ShouldBe(
                expected[agent],
                $"Agent '{agent}' authenticated as identity '{identity}' instead of '{expected[agent]}'. "
                + "Acting identity must be resolved per agent from configuration; a shared ambient "
                + "identity cross-contaminates concurrent agents (#2733 AC4/AC5).");
        }

        // Vacuity guard: both identities must actually have been exercised, or a resolver that
        // always answered with one profile could satisfy the loop by never producing the other.
        observations.Select(o => o.Identity).Distinct().Count().ShouldBe(2);
    }

    // ---------- AC1/AC2: the tool surface carries the agent's identity, not an ambient one ----------

    [Fact]
    public async Task ToolsContributor_BuildsTheApiClientWithTheContributingAgentsId()
    {
        var observed = new List<string>();
        var contributor = new GitHubToolsContributor((GitHubToolsConfig _, AgentId agentId) =>
        {
            observed.Add(agentId.Value);
            return new NoopApiClient();
        });

        await contributor.ContributeAsync(ContextFor(BetaAgent));

        observed.ShouldBe([BetaAgent]);
    }

    // ---------- helpers ----------

    private static GitHubCredentialOptions TwoIdentityOptions() => new()
    {
        Identities = new Dictionary<string, GitHubIdentityOptions>(StringComparer.OrdinalIgnoreCase)
        {
            ["alpha-app"] = new() { AppId = "1001", InstallationId = "2001", PrivateKeyPath = "/keys/alpha.pem" },
            ["beta-app"] = new() { AppId = "1002", InstallationId = "2002", PrivateKeyPath = "/keys/beta.pem" },
        },
        AgentIdentities = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [AlphaAgent] = "alpha-app",
            [BetaAgent] = "beta-app",
        },
    };

    private static ConfiguredGitHubIdentityResolver ResolverFor(GitHubCredentialOptions options) =>
        new(options, identity => new StampingCredentialProvider(identity.Name));

    /// <summary>Writes the identity name into the Authorization header so it can be observed.</summary>
    private sealed class StampingCredentialProvider : IGitHubCredentialProvider
    {
        private readonly string _identityName;

        public StampingCredentialProvider(string identityName) => _identityName = identityName;

        public Task AuthenticateAsync(HttpRequestMessage request, CancellationToken cancellationToken = default)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _identityName);
            return Task.CompletedTask;
        }
    }

    private sealed class NoopApiClient : IGitHubApiClient
    {
        public Task<GitHubApiResponse> SendAsync(
            HttpMethod method,
            string path,
            object? body = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new GitHubApiResponse((int)HttpStatusCode.OK, true, null));
    }

    private static AgentToolContributionContext ContextFor(string agentId) =>
        new(
            new AgentDescriptor
            {
                AgentId = AgentId.From(agentId),
                DisplayName = agentId,
                ModelId = "claude-opus-5",
                ApiProvider = "github-copilot",
                ExtensionConfig = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                {
                    [GitHubToolsConfig.ExtensionId] = JsonDocument.Parse("{}").RootElement.Clone(),
                },
            },
            new AgentExecutionContext { SessionId = SessionId.From("session-2733") },
            WorkspacePath: Path.GetTempPath(),
            PathValidator: null!,
            CopilotMcpEndpoint: null,
            GetProviderApiKeyAsync: (_, _) => Task.FromResult<string?>(null));
}
