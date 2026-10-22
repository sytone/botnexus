using System.IO.Abstractions.TestingHelpers;
using System.Text.Json;
using BotNexus.Agent.Providers.Core;
using BotNexus.Agent.Providers.Copilot.Headers;
using BotNexus.Agent.Providers.Copilot.Discovery;
using BotNexus.Gateway.Api.Controllers;
using BotNexus.Gateway.Abstractions.Providers;
using BotNexus.Gateway.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace BotNexus.Gateway.Api.Tests;

public sealed class CopilotCredentialProvenanceTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("literal-key")]
    [InlineData("auth:")]
    [InlineData("AUTH: Account-B ")]
    public async Task Account_ExactUsableEntryWinsOverEveryConfigDeclaration(string? declaration)
    {
        var f = Fixture(declaration, new() { ["work"] = Entry("oauth-a", "access-a"), ["account-b"] = Entry("oauth-b", "access-b") });
        (await f.Auth.GetApiKeyAsync("WORK")).ShouldBe("access-a");
        var account = f.Auth.ResolveCopilotAccountCredential("WORK") ?? throw new InvalidOperationException("Exact account unavailable");
        account.Instance.ShouldBe("work");
        account.OAuthToken.ShouldBe("oauth-a");
    }

    [Theory]
    [InlineData("https://api.githubcopilot.com", true)]
    [InlineData("https://api.individual.githubcopilot.com", true)]
    [InlineData("https://api.business.githubcopilot.com", true)]
    [InlineData("https://api.enterprise.githubcopilot.com", true)]
    [InlineData("https://copilot.acme.ghe.com", false)]
    [InlineData("https://api.githubcopilot.com.attacker.test", false)]
    [InlineData("https://evilgithubcopilot.com", false)]
    [InlineData("https://arbitrary.githubcopilot.com", false)]
    [InlineData("http://api.githubcopilot.com", false)]
    [InlineData("https://user@api.githubcopilot.com", false)]
    [InlineData("https://api.githubcopilot.com:444", false)]
    public async Task AccountAndHeaders_TrustOnlyPublicServiceEndpoints(string endpoint, bool allowed)
    {
        var f = Fixture("auth:account-b", new() { ["account-b"] = Entry("oauth-b", "access-b", endpoint) });
        (f.Auth.ResolveCopilotAccountCredential("work") is not null).ShouldBe(allowed);
        var options = await f.Auth.CreateExecutionOptionsAsync("work");
        var attribution = (options.Metadata ?? throw new InvalidOperationException())[CopilotHeaderScope.MetadataKey];
        if (allowed) attribution.ShouldBeOfType<CopilotHeaderScope>();
        else attribution.ShouldBe(CopilotHeaderAttribution.Unavailable);
    }

    [Theory]
    [InlineData("")]
    [InlineData("literal-key")]
    [InlineData("auth:")]
    [InlineData("auth:missing")]
    public async Task Account_NoUsableExactEntryNeverAttributesRawOrUnresolvedDeclaration(string declaration)
    {
        var f = Fixture(declaration, new() { ["work"] = new() { Type = "api", Access = "", Refresh = "not-oauth" }, ["account-b"] = Entry("oauth-b", "access-b") });
        f.Auth.ResolveCopilotAccountCredential("work").ShouldBeNull();
        var options = await f.Auth.CreateExecutionOptionsAsync("work");
        options.ApiKey.ShouldBe(declaration.StartsWith("auth:", StringComparison.Ordinal) ? "" : declaration);
        (options.Metadata ?? throw new InvalidOperationException())[CopilotHeaderScope.MetadataKey].ShouldBe(CopilotHeaderAttribution.Unavailable);
    }

    [Fact]
    public async Task Account_CaseInsensitiveReferenceFallsThroughUnusableExactEntry()
    {
        var f = Fixture("AUTH: ACCOUNT-B ", new() { ["work"] = new() { Type = "api", Access = "" }, ["account-b"] = Entry("oauth-b", "access-b") });
        (await f.Auth.GetApiKeyAsync("WoRk")).ShouldBe("access-b");
        (f.Auth.ResolveCopilotAccountCredential("WoRk") ?? throw new InvalidOperationException()).OAuthToken.ShouldBe("oauth-b");
        var options = await f.Auth.CreateExecutionOptionsAsync("WoRk");
        (options.Metadata ?? throw new InvalidOperationException())[CopilotHeaderScope.MetadataKey].ShouldBeOfType<CopilotHeaderScope>();
    }

    [Fact]
    public async Task Headers_RotationDuringRefreshWithEqualAccessNeverAttributesNewAccount()
    {
        var initial = Entry("oauth-old", "shared-access");
        initial.Expires = 1;
        var f = Fixture("auth:account-b", new() { ["account-b"] = initial });
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var auth = new GatewayAuthManager(f.Monitor, NullLogger<GatewayAuthManager>.Instance, f.FileSystem, NullProviderHealthObserver.Instance, async (_, _) =>
        {
            started.SetResult();
            await release.Task;
            return Entry("oauth-old", "shared-access");
        });
        var pending = auth.CreateExecutionOptionsAsync("work");
        await started.Task;
        Write(f.FileSystem, new() { ["account-b"] = Entry("oauth-new-longer", "shared-access") });
        auth.InvalidateCache();
        (auth.ResolveCopilotAccountCredential("work") ?? throw new InvalidOperationException()).OAuthToken.ShouldBe("oauth-new-longer");
        release.SetResult();
        var options = await pending;
        options.ApiKey.ShouldBe("shared-access");
        (options.Metadata ?? throw new InvalidOperationException())[CopilotHeaderScope.MetadataKey].ShouldBe(CopilotHeaderAttribution.Unavailable);
        (auth.ResolveCopilotAccountCredential("work") ?? throw new InvalidOperationException()).OAuthToken.ShouldBe("oauth-new-longer");
    }

    [Theory]
    [InlineData("copilot", "github-copilot")]
    [InlineData("github-copilot", "github-copilot")]
    [InlineData("github-copilot", "copilot")]
    [InlineData("copilot", "copilot")]
    public async Task Account_CanonicalAliasMatchesCredentialSupplier(string requested, string stored)
    {
        var f = Fixture(null, new() { [stored] = Entry("oauth-canonical", "access-canonical") });
        (await f.Auth.GetApiKeyAsync(requested)).ShouldBe("access-canonical");
        (f.Auth.ResolveCopilotAccountCredential(requested) ?? throw new InvalidOperationException()).OAuthToken.ShouldBe("oauth-canonical");
        var options = await f.Auth.CreateExecutionOptionsAsync(requested);
        (options.Metadata ?? throw new InvalidOperationException())[CopilotHeaderScope.MetadataKey].ShouldBeOfType<CopilotHeaderScope>().Instance.ShouldBe("github-copilot");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("auth:missing")]
    [InlineData("literal-key")]
    public async Task AccountAndHeaders_AmbientOrRawCanonicalCredentialHasNoOAuthSupplier(string? declaration)
    {
        var f = Fixture(null, new());
        (f.Monitor.CurrentValue.Providers ?? throw new InvalidOperationException())["github-copilot"] = new() { Type = "github-copilot", ApiKey = declaration };
        f.Auth.ResolveCopilotAccountCredential("github-copilot").ShouldBeNull();
        var options = await f.Auth.CreateExecutionOptionsAsync("github-copilot");
        var expected = declaration is null ? EnvironmentApiKeys.GetApiKey("github-copilot")
            : declaration.StartsWith("auth:", StringComparison.OrdinalIgnoreCase) ? "" : declaration;
        options.ApiKey.ShouldBe(expected);
        (options.Metadata ?? throw new InvalidOperationException())[CopilotHeaderScope.MetadataKey].ShouldBe(CopilotHeaderAttribution.Unavailable);
    }

    [Fact]
    public async Task Endpoint_UnusableExactEntryUsesSameReferencedSupplierAsInference()
    {
        var f = Fixture("auth:account-b", new()
        {
            ["work"] = new() { Type = "api", Access = "", Endpoint = "https://unrelated.test" },
            ["account-b"] = Entry("oauth-b", "access-b", "https://api.business.githubcopilot.com")
        });
        (await f.Auth.GetApiKeyAsync("work")).ShouldBe("access-b");
        f.Auth.GetApiEndpoint("work").ShouldBe("https://api.business.githubcopilot.com");
    }

    [Theory]
    [InlineData(true, "https://copilot.acme.ghe.com", "github-copilot")]
    [InlineData(false, "https://api.githubcopilot.com", "github-copilot")]
    [InlineData(true, "https://api.githubcopilot.com", "openai")]
    public async Task AccountAndHeaders_RejectUnsupportedConfiguredProvider(bool enabled, string baseUrl, string type)
    {
        var f = Fixture("auth:account-b", new() { ["account-b"] = Entry("oauth-b", "access-b") });
        var config = (f.Monitor.CurrentValue.Providers ?? throw new InvalidOperationException())["work"];
        config.Enabled = enabled;
        config.BaseUrl = baseUrl;
        config.Type = type;
        f.Auth.ResolveCopilotAccountCredential("work").ShouldBeNull();
        var options = await f.Auth.CreateExecutionOptionsAsync("work");
        (options.Metadata ?? throw new InvalidOperationException())[CopilotHeaderScope.MetadataKey].ShouldBe(CopilotHeaderAttribution.Unavailable);
    }

    [Fact]
    public async Task AccountAndHeaders_ExactRawTokenNeverBorrowsReferencedOAuthIdentity()
    {
        var f = Fixture("auth:account-b", new()
        {
            ["work"] = new() { Type = "token", Access = "access-b" },
            ["account-b"] = Entry("oauth-b", "access-b")
        });
        f.Auth.ResolveCopilotAccountCredential("work").ShouldBeNull();
        var options = await f.Auth.CreateExecutionOptionsAsync("work");
        options.ApiKey.ShouldBe("access-b");
        (options.Metadata ?? throw new InvalidOperationException())[CopilotHeaderScope.MetadataKey].ShouldBe(CopilotHeaderAttribution.Unavailable);
    }

    [Fact]
    public async Task Headers_SuccessfulRefreshRetainsOAuthGenerationAndSupplier()
    {
        var initial = Entry("oauth-b", "stale");
        initial.Expires = 1;
        var f = Fixture("auth:account-b", new() { ["account-b"] = initial });
        var calls = 0;
        var auth = new GatewayAuthManager(f.Monitor, NullLogger<GatewayAuthManager>.Instance, f.FileSystem, NullProviderHealthObserver.Instance, (_, _) =>
        {
            calls++;
            return Task.FromResult(Entry("oauth-b", "fresh"));
        });
        var before = auth.ResolveCopilotAccountCredential("work") ?? throw new InvalidOperationException();
        var options = await auth.CreateExecutionOptionsAsync("work");
        options.ApiKey.ShouldBe("fresh");
        calls.ShouldBe(1);
        var scope = (options.Metadata ?? throw new InvalidOperationException())[CopilotHeaderScope.MetadataKey].ShouldBeOfType<CopilotHeaderScope>();
        scope.Generation.ShouldBe(before.Generation);
        (auth.ResolveCopilotAccountCredential("work") ?? throw new InvalidOperationException()).Generation.ShouldBe(scope.Generation);
    }

    [Fact]
    public async Task Headers_ReferencedRefreshFailureRemainsDeclaredAndIsNotRetried()
    {
        var initial = Entry("oauth-b", "stale");
        initial.Expires = 1;
        var f = Fixture("auth:account-b", new() { ["account-b"] = initial });
        var calls = 0;
        var auth = new GatewayAuthManager(f.Monitor, NullLogger<GatewayAuthManager>.Instance, f.FileSystem, NullProviderHealthObserver.Instance, (_, _) =>
        {
            calls++;
            throw new HttpRequestException("upstream unavailable");
        });
        var options = await auth.CreateExecutionOptionsAsync("work");
        options.ApiKey.ShouldBe("");
        calls.ShouldBe(1);
        (options.Metadata ?? throw new InvalidOperationException())[CopilotHeaderScope.MetadataKey].ShouldBe(CopilotHeaderAttribution.Unavailable);
        (await auth.ResolveCredentialAsync("work")).Status.ShouldBe(ProviderCredentialStatus.RefreshFailed);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task GetApiKey_BlankSuccessfulExactRefreshFallsThroughToConfiguredKey(string access)
    {
        var initial = Entry("oauth-openai", "stale");
        initial.Expires = 1;
        var f = Fixture(null, new() { ["openai"] = initial });
        (f.Monitor.CurrentValue.Providers ?? throw new InvalidOperationException())["openai"] = new() { Type = "openai", ApiKey = "configured-key" };
        var observer = new RecordingHealthObserver();
        var calls = 0;
        var auth = new GatewayAuthManager(f.Monitor, NullLogger<GatewayAuthManager>.Instance, f.FileSystem, observer, (_, _) =>
        {
            calls++;
            return Task.FromResult(Entry("oauth-openai", access));
        });
        (await auth.GetApiKeyAsync("openai")).ShouldBe("configured-key");
        calls.ShouldBe(1);
        var notification = observer.Outcomes.ShouldHaveSingleItem();
        notification.Provider.ShouldBe("openai");
        notification.Outcome.Status.ShouldBe(ProviderCredentialStatus.Resolved);
        notification.Outcome.ApiKey.ShouldBe(access);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ResolveCredential_BlankSuccessfulRefreshPreservesHistoricalOutcome(string access)
    {
        var initial = Entry("oauth-openai", "stale");
        initial.Expires = 1;
        var f = Fixture(null, new() { ["openai"] = initial });
        (f.Monitor.CurrentValue.Providers ?? throw new InvalidOperationException())["openai"] = new() { Type = "openai", ApiKey = "configured-key" };
        var auth = new GatewayAuthManager(f.Monitor, NullLogger<GatewayAuthManager>.Instance, f.FileSystem, NullProviderHealthObserver.Instance,
            (_, _) => Task.FromResult(Entry("oauth-openai", access)));
        var outcome = await auth.ResolveCredentialAsync("openai");
        outcome.Status.ShouldBe(ProviderCredentialStatus.Resolved);
        outcome.ApiKey.ShouldBe(access);
    }

    [Theory]
    [InlineData("github-copilot", "CoPiLoT", false)]
    [InlineData("copilot", "GITHUB-COPILOT", false)]
    [InlineData("github-copilot", "CoPiLoT", true)]
    [InlineData("copilot", "GITHUB-COPILOT", true)]
    public async Task Endpoint_CanonicalAliasConfigUsesSameSupplierWithoutGheAttribution(string requested, string configured, bool referenced)
    {
        const string endpoint = "https://copilot.acme.ghe.com";
        // A non-OAuth referenced token needs no refresh and exercises config endpoint fallback.
        var f = Fixture(null, referenced ? new() { ["account-b"] = new() { Type = "token", Access = "selected-key" } } : new());
        var providers = f.Monitor.CurrentValue.Providers ?? throw new InvalidOperationException();
        providers.Clear();
        providers[configured] = new() { Type = "github-copilot", ApiKey = referenced ? "auth:account-b" : "selected-key", BaseUrl = endpoint };
        (await f.Auth.GetApiKeyAsync(requested)).ShouldBe("selected-key");
        f.Auth.GetApiEndpoint(requested).ShouldBe(endpoint);
        f.Auth.ResolveCopilotAccountCredential(requested).ShouldBeNull();
        var options = await f.Auth.CreateExecutionOptionsAsync(requested);
        options.ApiKey.ShouldBe("selected-key");
        (options.Metadata ?? throw new InvalidOperationException())[CopilotHeaderScope.MetadataKey].ShouldBe(CopilotHeaderAttribution.Unavailable);
    }

    [Fact]
    public async Task Endpoint_ExactConfigStillWinsOverCanonicalAliasConfig()
    {
        var f = Fixture(null, new());
        var providers = f.Monitor.CurrentValue.Providers ?? throw new InvalidOperationException();
        providers["copilot"] = new() { ApiKey = "alias-key", BaseUrl = "https://alias.test" };
        providers["github-copilot"] = new() { ApiKey = "exact-key", BaseUrl = "https://exact.test" };
        (await f.Auth.GetApiKeyAsync("GITHUB-COPILOT")).ShouldBe("exact-key");
        f.Auth.GetApiEndpoint("GITHUB-COPILOT").ShouldBe("https://exact.test");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Headers_FallbackRefreshReportsActualSupplierKeysAndPreservesProvenance(bool blankSuccess)
    {
        var exact = Entry("oauth-exact", "stale-exact");
        var referenced = Entry("oauth-b", "stale-b");
        exact.Expires = referenced.Expires = 1;
        var f = Fixture("auth:account-b", new() { ["work"] = exact, ["account-b"] = referenced });
        var observer = new RecordingHealthObserver();
        var calls = 0;
        var auth = new GatewayAuthManager(f.Monitor, NullLogger<GatewayAuthManager>.Instance, f.FileSystem, observer, (entry, _) =>
        {
            calls++;
            if (entry.Refresh == "oauth-exact")
            {
                if (blankSuccess) return Task.FromResult(Entry("oauth-exact", " "));
                throw new HttpRequestException("upstream unavailable");
            }
            entry.Refresh.ShouldBe("oauth-b");
            return Task.FromResult(Entry("oauth-b", "fresh-b"));
        });
        var options = await auth.CreateExecutionOptionsAsync("work");
        options.ApiKey.ShouldBe("fresh-b");
        calls.ShouldBe(2);
        observer.Outcomes.Select(x => x.Provider).ShouldBe(new[] { "work", "account-b" });
        observer.Outcomes[0].Outcome.Status.ShouldBe(blankSuccess ? ProviderCredentialStatus.Resolved : ProviderCredentialStatus.RefreshFailed);
        observer.Outcomes[1].Outcome.Status.ShouldBe(ProviderCredentialStatus.Resolved);
        observer.Outcomes[1].Outcome.ApiKey.ShouldBe("fresh-b");
        var attribution = (options.Metadata ?? throw new InvalidOperationException())[CopilotHeaderScope.MetadataKey];
        if (blankSuccess)
        {
            var scope = attribution.ShouldBeOfType<CopilotHeaderScope>();
            scope.Instance.ShouldBe("work");
            scope.Generation.ShouldBe((auth.ResolveCopilotAccountCredential("work") ?? throw new InvalidOperationException()).Generation);
        }
        else
        {
            // Inference still falls back, but neither headers nor account discovery may use failed A.
            attribution.ShouldBe(CopilotHeaderAttribution.Unavailable);
            auth.ResolveCopilotAccountCredential("work").ShouldBeNull();
            await AssertNoQuotaFetchAsync(auth, "work");
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Account_FailedExactSnapshotStaysUnavailableUntilSuccessfulRefreshOrRotation(bool rotate)
    {
        var initial = Entry("oauth-a", "stale-a");
        initial.Expires = 1;
        var f = Fixture("auth:account-b", new() { ["work"] = initial, ["account-b"] = Entry("oauth-b", "access-b") });
        var fail = true;
        var auth = new GatewayAuthManager(f.Monitor, NullLogger<GatewayAuthManager>.Instance, f.FileSystem,
            NullProviderHealthObserver.Instance, (_, _) => fail
                ? Task.FromException<GatewayAuthManager.AuthEntry>(new HttpRequestException("synthetic refresh failure"))
                : Task.FromResult(Entry("oauth-a", "fresh-a")));
        var original = auth.ResolveCopilotAccountCredential("work") ?? throw new InvalidOperationException();
        using var cachedHandler = new QuotaRequestCounter();
        using var cachedClient = new HttpClient(cachedHandler);
        var cachedQuota = new CopilotQuotaService(auth, new CopilotDiscoveryClient(cachedClient), NullLogger<CopilotQuotaService>.Instance);
        var cachedA = await cachedQuota.RefreshAsync("work");
        cachedA.AttemptState.ShouldBe("success");
        cachedA.Snapshots.ShouldHaveSingleItem().Remaining.ShouldBe(7m);
        cachedHandler.Requests.ShouldBe(1);
        (await auth.GetApiKeyAsync("work")).ShouldBe("access-b");
        auth.ResolveCopilotAccountCredential("work").ShouldBeNull();
        (await auth.GetCopilotOAuthTokenAsync("work")).ShouldBeNull();
        foreach (var state in new[] { await cachedQuota.ReadAsync("work"), await cachedQuota.RefreshAsync("work", force: true) })
        {
            state.AttemptState.ShouldBe("unavailable");
            state.Instance.ShouldBeNull();
            state.Snapshots.ShouldBeEmpty();
        }
        cachedHandler.Requests.ShouldBe(1);
        await AssertNoQuotaFetchAsync(auth, "work");
        var fallback = await auth.CreateExecutionOptionsAsync("work");
        fallback.ApiKey.ShouldBe("access-b");
        (fallback.Metadata ?? throw new InvalidOperationException())[CopilotHeaderScope.MetadataKey].ShouldBe(CopilotHeaderAttribution.Unavailable);
        auth.ResolveCopilotAccountCredential("work").ShouldBeNull();
        fail = false;
        if (rotate)
        {
            Write(f.FileSystem, new() { ["work"] = Entry("oauth-rotated-longer", "rotated-access"), ["account-b"] = Entry("oauth-b", "access-b") });
            auth.InvalidateCache();
        }
        var recovered = await auth.CreateExecutionOptionsAsync("work");
        recovered.ApiKey.ShouldBe(rotate ? "rotated-access" : "fresh-a");
        var account = auth.ResolveCopilotAccountCredential("work") ?? throw new InvalidOperationException();
        account.OAuthToken.ShouldBe(rotate ? "oauth-rotated-longer" : "oauth-a");
        if (rotate) account.Generation.ShouldNotBe(original.Generation);
        else account.Generation.ShouldBe(original.Generation);
        var scope = (recovered.Metadata ?? throw new InvalidOperationException())[CopilotHeaderScope.MetadataKey].ShouldBeOfType<CopilotHeaderScope>();
        scope.Generation.ShouldBe(account.Generation);
        var json = f.FileSystem.File.ReadAllText(Path.Combine(PlatformConfigLoader.GetDefaultConfigDirectory(f.FileSystem), "auth.json"));
        json.ShouldNotContain("AttributionUnavailable", Case.Insensitive);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task AccountAndHeaders_DistinctCanonicalSuppliersFailClosedWithoutChangingInference(bool references, bool sameRefresh)
    {
        var f = Fixture(null, references
            ? new() { ["account-a"] = Entry("oauth-a", "access-a"), ["account-b"] = Entry(sameRefresh ? "oauth-a" : "oauth-b", "access-b") }
            : new() { ["copilot"] = Entry("oauth-a", "access-a"), ["github-copilot"] = Entry(sameRefresh ? "oauth-a" : "oauth-b", "access-b") });
        var providers = f.Monitor.CurrentValue.Providers ?? throw new InvalidOperationException();
        providers.Clear();
        if (references)
        {
            providers["copilot"] = new() { Type = "github-copilot", ApiKey = "auth:account-a" };
            providers["github-copilot"] = new() { Type = "github-copilot", ApiKey = "auth:account-b" };
        }
        foreach (var (requested, access) in new[] { ("copilot", "access-a"), ("github-copilot", "access-b") })
        {
            (await f.Auth.GetApiKeyAsync(requested)).ShouldBe(access);
            f.Auth.ResolveCopilotAccountCredential(requested).ShouldBeNull();
            (await f.Auth.GetCopilotOAuthTokenAsync(requested)).ShouldBeNull();
            var options = await f.Auth.CreateExecutionOptionsAsync(requested);
            options.ApiKey.ShouldBe(access);
            (options.Metadata ?? throw new InvalidOperationException())[CopilotHeaderScope.MetadataKey].ShouldBe(CopilotHeaderAttribution.Unavailable);
            await AssertNoQuotaFetchAsync(f.Auth, requested);
        }
    }

    [Fact]
    public async Task AccountAndHeaders_CanonicalAliasesReferencingSameSnapshotShareGeneration()
    {
        var f = Fixture(null, new() { ["account-a"] = Entry("oauth-a", "access-a") });
        var providers = f.Monitor.CurrentValue.Providers ?? throw new InvalidOperationException();
        providers.Clear();
        providers["copilot"] = new() { Type = "copilot", ApiKey = "AUTH: ACCOUNT-A " };
        providers["github-copilot"] = new() { Type = "github-copilot", ApiKey = "auth:account-a" };
        var first = f.Auth.ResolveCopilotAccountCredential("copilot") ?? throw new InvalidOperationException();
        var second = f.Auth.ResolveCopilotAccountCredential("github-copilot") ?? throw new InvalidOperationException();
        first.Instance.ShouldBe(second.Instance);
        first.Generation.ShouldBe(second.Generation);
        foreach (var instance in new[] { "copilot", "github-copilot" })
        {
            var options = await f.Auth.CreateExecutionOptionsAsync(instance);
            options.ApiKey.ShouldBe("access-a");
            var scope = (options.Metadata ?? throw new InvalidOperationException())[CopilotHeaderScope.MetadataKey].ShouldBeOfType<CopilotHeaderScope>();
            scope.Instance.ShouldBe(first.Instance);
            scope.Generation.ShouldBe(first.Generation);
        }
    }

    private static async Task AssertNoQuotaFetchAsync(GatewayAuthManager auth, string instance)
    {
        using var handler = new QuotaRequestCounter();
        using var client = new HttpClient(handler);
        var quota = new CopilotQuotaService(auth, new CopilotDiscoveryClient(client), NullLogger<CopilotQuotaService>.Instance);
        foreach (var state in new[] { await quota.ReadAsync(instance), await quota.RefreshAsync(instance, force: true) })
        {
            state.AttemptState.ShouldBe("unavailable");
            state.Instance.ShouldBeNull();
            state.Snapshots.ShouldBeEmpty();
        }
        handler.Requests.ShouldBe(0);
    }

    private sealed class QuotaRequestCounter : HttpMessageHandler
    {
        public int Requests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("{\"quota_snapshots\":{\"chat\":{\"quota_remaining\":7}}}")
            });
        }
    }

    private sealed class RecordingHealthObserver : IProviderHealthObserver
    {
        public List<(string Provider, ProviderCredentialOutcome Outcome)> Outcomes { get; } = new();
        public Task RecordAsync(string providerId, ProviderCredentialOutcome outcome, CancellationToken cancellationToken = default)
        {
            Outcomes.Add((providerId, outcome));
            return Task.CompletedTask;
        }
    }

    private static (GatewayAuthManager Auth, MockFileSystem FileSystem, IOptionsMonitor<PlatformConfig> Monitor) Fixture(string? declaration, Dictionary<string, GatewayAuthManager.AuthEntry> entries)
    {
        var fs = new MockFileSystem();
        Write(fs, entries);
        var monitor = Substitute.For<IOptionsMonitor<PlatformConfig>>();
        monitor.CurrentValue.Returns(new PlatformConfig { Providers = new() { ["work"] = new() { Type = "github-copilot", ApiKey = declaration } } });
        return (new(monitor, NullLogger<GatewayAuthManager>.Instance, fs), fs, monitor);
    }

    private static void Write(MockFileSystem fs, Dictionary<string, GatewayAuthManager.AuthEntry> entries)
    {
        var path = Path.Combine(PlatformConfigLoader.GetDefaultConfigDirectory(fs), "auth.json");
        fs.Directory.CreateDirectory(Path.GetDirectoryName(path) ?? throw new InvalidOperationException());
        fs.File.WriteAllText(path, JsonSerializer.Serialize(entries));
    }

    private static GatewayAuthManager.AuthEntry Entry(string refresh, string access, string endpoint = "https://api.githubcopilot.com") => new() { Refresh = refresh, Access = access, Expires = 4102444800000, Endpoint = endpoint };
}
