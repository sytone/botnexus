using System.Net;
using System.Reflection;
using BotNexus.Gateway.Abstractions.Providers;
using BotNexus.Gateway.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.IO.Abstractions.TestingHelpers;
using Shouldly;

namespace BotNexus.Gateway.Tests;

/// <summary>
/// Pins the credential-outcome distinction introduced by #3281.
///
/// <para>
/// Before this change, credential resolution answered with a bare <c>string?</c> and <c>null</c>
/// meant three unrelated things at once: no auth entry, a failed refresh, and no credential. A
/// seven-hour upstream outage was therefore indistinguishable from a provider nobody had configured,
/// and no caller could react to either. These tests assert the two now produce <em>different</em>
/// results, which is the property that makes a health signal possible at all.
/// </para>
/// </summary>
public sealed class GatewayAuthManagerCredentialOutcomeTests
{
    private readonly MockFileSystem _fileSystem = new();
    private readonly string _authFilePath;
    private readonly string _legacyAuthFilePath;

    public GatewayAuthManagerCredentialOutcomeTests()
    {
        var root = Path.Combine(Path.GetTempPath(), "botnexus", "gateway-auth-outcome-tests");
        _fileSystem.Directory.CreateDirectory(root);
        _authFilePath = Path.Combine(root, "auth.json");
        _legacyAuthFilePath = Path.Combine(root, "legacy-auth.json");
    }

    /// <summary>An expired oauth entry, so that resolution is forced down the refresh path.</summary>
    private const string ExpiredCopilotAuthJson = """
        {
          "github-copilot": {
            "type": "oauth",
            "refresh": "refresh-token",
            "access": "stale-access",
            "expires": 1,
            "endpoint": "https://api.test"
          }
        }
        """;

    private sealed class CapturingObserver : IProviderHealthObserver
    {
        public List<(string Provider, ProviderCredentialOutcome Outcome)> Records { get; } = [];

        public Task RecordAsync(string providerId, ProviderCredentialOutcome outcome, CancellationToken cancellationToken = default)
        {
            Records.Add((providerId, outcome));
            return Task.CompletedTask;
        }
    }

    private GatewayAuthManager CreateManager(
        IProviderHealthObserver observer,
        Func<GatewayAuthManager.AuthEntry, CancellationToken, Task<GatewayAuthManager.AuthEntry>>? refresh = null)
    {
        var monitor = new StaticOptionsMonitor<PlatformConfig>(new PlatformConfig());

        // Default refresh reproduces the exact upstream failure from the observed outage: a 503
        // surfaced by EnsureSuccessStatusCode during the Copilot token exchange.
        refresh ??= (_, _) => throw new HttpRequestException(
            "Response status code does not indicate success: 503 (Service Unavailable).",
            inner: null,
            statusCode: HttpStatusCode.ServiceUnavailable);

        var manager = new GatewayAuthManager(
            monitor,
            NullLogger<GatewayAuthManager>.Instance,
            _fileSystem,
            observer,
            refresh);

        typeof(GatewayAuthManager).GetField("_authFilePath", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(manager, _authFilePath);
        typeof(GatewayAuthManager).GetField("_legacyAuthFilePath", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(manager, _legacyAuthFilePath);

        return manager;
    }

    /// <summary>
    /// AC1, first half: an upstream refresh failure is reported as a provider fault carrying the
    /// status code, not as an absent credential.
    /// </summary>
    [Fact]
    public async Task RefreshFailure_ReportsProviderFaultWithStatusCode()
    {
        await _fileSystem.File.WriteAllTextAsync(_authFilePath, ExpiredCopilotAuthJson);
        var observer = new CapturingObserver();
        var manager = CreateManager(observer);

        var outcome = await manager.ResolveCredentialAsync("github-copilot");

        outcome.Status.ShouldBe(ProviderCredentialStatus.RefreshFailed);
        outcome.IsProviderFault.ShouldBeTrue();
        outcome.StatusCode.ShouldBe(503);
        outcome.FailureClass.ShouldBe("HttpRequestException");
        outcome.ApiKey.ShouldBeNull();
    }

    /// <summary>
    /// AC1, second half: a provider with no configuration at all is NOT a fault. This is the clause
    /// that fails on the pre-fix tree, where both conditions produced an identical bare null.
    /// </summary>
    [Fact]
    public async Task UnconfiguredProvider_IsNotReportedAsAFault()
    {
        await _fileSystem.File.WriteAllTextAsync(_authFilePath, "{}");
        var observer = new CapturingObserver();
        var manager = CreateManager(observer);

        var outcome = await manager.ResolveCredentialAsync("provider-that-does-not-exist");

        outcome.Status.ShouldBe(ProviderCredentialStatus.NotConfigured);
        outcome.IsProviderFault.ShouldBeFalse();
    }

    /// <summary>
    /// The two conditions must be distinguishable from one another - stated directly, because this
    /// single property is what the whole issue turns on.
    /// </summary>
    [Fact]
    public async Task OutageAndUnconfigured_ProduceDifferentOutcomes()
    {
        await _fileSystem.File.WriteAllTextAsync(_authFilePath, ExpiredCopilotAuthJson);
        var manager = CreateManager(new CapturingObserver());

        var outage = await manager.ResolveCredentialAsync("github-copilot");
        var absent = await manager.ResolveCredentialAsync("provider-that-does-not-exist");

        outage.Status.ShouldNotBe(absent.Status);
    }

    /// <summary>
    /// The health observer is told about the failure. Without this the reason would be computed and
    /// then discarded again, which is precisely the original defect in a new location.
    /// </summary>
    [Fact]
    public async Task RefreshFailure_IsReportedToHealthObserver()
    {
        await _fileSystem.File.WriteAllTextAsync(_authFilePath, ExpiredCopilotAuthJson);
        var observer = new CapturingObserver();
        var manager = CreateManager(observer);

        await manager.ResolveCredentialAsync("github-copilot");

        var record = observer.Records.ShouldHaveSingleItem();
        record.Provider.ShouldBe("github-copilot");
        record.Outcome.IsProviderFault.ShouldBeTrue();
        record.Outcome.StatusCode.ShouldBe(503);
    }

    /// <summary>
    /// An unconfigured provider must not reach the observer as a fault, or every host that does not
    /// use a given provider would report a permanent outage of it.
    /// </summary>
    [Fact]
    public async Task UnconfiguredProvider_ReportsNoFaultToObserver()
    {
        await _fileSystem.File.WriteAllTextAsync(_authFilePath, "{}");
        var observer = new CapturingObserver();
        var manager = CreateManager(observer);

        await manager.ResolveCredentialAsync("provider-that-does-not-exist");

        observer.Records.ShouldNotContain(r => r.Outcome.IsProviderFault);
    }

    /// <summary>
    /// An observer that throws must not break credential resolution: it sits on the critical path of
    /// every agent turn, and failing to report an outage must not itself cause one.
    /// </summary>
    [Fact]
    public async Task ThrowingObserver_DoesNotBreakCredentialResolution()
    {
        await _fileSystem.File.WriteAllTextAsync(_authFilePath, ExpiredCopilotAuthJson);
        var manager = CreateManager(new ThrowingObserver());

        var outcome = await manager.ResolveCredentialAsync("github-copilot");

        outcome.Status.ShouldBe(ProviderCredentialStatus.RefreshFailed);
    }

    private sealed class ThrowingObserver : IProviderHealthObserver
    {
        public Task RecordAsync(string providerId, ProviderCredentialOutcome outcome, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("observer is broken");
    }

    /// <summary>
    /// Minimal options monitor over a fixed value. Declared locally to match the convention in the
    /// sibling auth tests, which each carry their own copy rather than sharing one.
    /// </summary>
    private sealed class StaticOptionsMonitor<T> : IOptionsMonitor<T>
    {
        public StaticOptionsMonitor(T value) => CurrentValue = value;

        public T CurrentValue { get; }
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    /// <summary>
    /// The legacy <c>GetApiKeyAsync</c> projection still returns null on failure, so existing callers
    /// are unaffected by the richer result. Behaviour parity matters: this is a fix, not a rewrite.
    /// </summary>
    [Fact]
    public async Task GetApiKeyAsync_StillReturnsNullOnRefreshFailure()
    {
        await _fileSystem.File.WriteAllTextAsync(_authFilePath, ExpiredCopilotAuthJson);
        var manager = CreateManager(new CapturingObserver());

        var apiKey = await manager.GetApiKeyAsync("github-copilot");

        apiKey.ShouldBeNull();
    }
}
