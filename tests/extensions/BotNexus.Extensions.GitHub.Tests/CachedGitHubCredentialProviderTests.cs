using System.Reflection;
using Shouldly;

namespace BotNexus.Extensions.GitHub.Tests;

/// <summary>
/// Behaviour tests for the platform-owned GitHub credential provider (#2732).
///
/// <para>The clock is a <see cref="FakeTimeProvider"/>-shaped stub advanced explicitly: no test in
/// this file sleeps, so the expired-token path runs in microseconds and cannot flake on a slow
/// agent (AC3).</para>
/// </summary>
public sealed class CachedGitHubCredentialProviderTests
{
    private static readonly DateTimeOffset Origin = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static GitHubInstallationToken TokenFor(int index, DateTimeOffset expiresAt) =>
        new($"ghs_token_{index}", expiresAt);

    // ---- AC2: caches until expiry -------------------------------------------------------------

    [Fact]
    public async Task ResolveAsync_WithinLifetime_MintsOnceAndReusesTheCachedToken()
    {
        var clock = new TestClock(Origin);
        var source = new CountingTokenSource(i => TokenFor(i, Origin.AddHours(1)));
        var provider = new CachedGitHubCredentialProvider(source, clock);

        var first = await provider.ResolveAsync();
        clock.Advance(TimeSpan.FromMinutes(30));
        var second = await provider.ResolveAsync();

        source.MintCount.ShouldBe(1);
        second.Value.ShouldBe(first.Value);
    }

    [Fact]
    public async Task AuthenticateAsync_SetsBearerHeaderFromTheCachedToken()
    {
        var clock = new TestClock(Origin);
        var source = new CountingTokenSource(i => TokenFor(i, Origin.AddHours(1)));
        var provider = new CachedGitHubCredentialProvider(source, clock);

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/user");
        await provider.AuthenticateAsync(request);

        request.Headers.Authorization.ShouldNotBeNull();
        request.Headers.Authorization!.Scheme.ShouldBe("Bearer");
        request.Headers.Authorization.Parameter.ShouldBe("ghs_token_1");
    }

    // ---- AC3: expired token refreshes transparently, clock advanced not slept ------------------

    [Fact]
    public async Task ResolveAsync_AfterTheCachedTokenExpires_MintsAFreshTokenWithoutAgentInvolvement()
    {
        var clock = new TestClock(Origin);
        var source = new CountingTokenSource(i => TokenFor(i, clock.GetUtcNow().AddHours(1)));
        var provider = new CachedGitHubCredentialProvider(source, clock);

        var first = await provider.ResolveAsync();
        source.MintCount.ShouldBe(1);

        // A second call BEFORE expiry must still not mint. This clause is what makes removing the
        // cache redden THIS test by name (#2732 AC6) rather than only the lifetime test: without a
        // cache the mint count is already 2 here.
        await provider.ResolveAsync();
        source.MintCount.ShouldBe(1, "a call inside the token's lifetime must reuse the cached token");

        // Advance PAST the expiry. No Thread.Sleep, no Task.Delay: the clock is the only thing moving.
        clock.Advance(TimeSpan.FromHours(2));

        var refreshed = await provider.ResolveAsync();

        source.MintCount.ShouldBe(2, "an expired cached token must be re-minted on the next call");
        refreshed.Value.ShouldNotBe(first.Value);
        refreshed.ExpiresAt.ShouldBeGreaterThan(clock.GetUtcNow());
    }

    [Fact]
    public async Task AuthenticateAsync_AfterExpiry_SucceedsAndCarriesTheRefreshedToken()
    {
        var clock = new TestClock(Origin);
        var source = new CountingTokenSource(i => TokenFor(i, clock.GetUtcNow().AddHours(1)));
        var provider = new CachedGitHubCredentialProvider(source, clock);

        using var before = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/user");
        await provider.AuthenticateAsync(before);

        clock.Advance(TimeSpan.FromHours(2));

        using var after = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/user");
        await provider.AuthenticateAsync(after);

        after.Headers.Authorization!.Parameter.ShouldBe("ghs_token_2");
        before.Headers.Authorization!.Parameter.ShouldBe("ghs_token_1");
    }

    [Fact]
    public async Task ResolveAsync_WithExpirySkew_RefreshesBeforeTheReportedExpiry()
    {
        var clock = new TestClock(Origin);
        var source = new CountingTokenSource(i => TokenFor(i, clock.GetUtcNow().AddMinutes(10)));
        var provider = new CachedGitHubCredentialProvider(source, clock, TimeSpan.FromMinutes(2));

        await provider.ResolveAsync();

        // Inside the skew window: still valid to GitHub, but treated as expired so it cannot die
        // mid-flight on the wire.
        clock.Advance(TimeSpan.FromMinutes(9));
        await provider.ResolveAsync();

        source.MintCount.ShouldBe(2);
    }

    [Fact]
    public async Task ResolveAsync_ConcurrentCallersOnAColdCache_MintExactlyOnce()
    {
        var clock = new TestClock(Origin);
        var source = new CountingTokenSource(i => TokenFor(i, Origin.AddHours(1)));
        var provider = new CachedGitHubCredentialProvider(source, clock);

        var results = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => provider.ResolveAsync()));

        source.MintCount.ShouldBe(1);
        results.Select(t => t.Value).Distinct().Count().ShouldBe(1);
    }

    // ---- Sad paths ----------------------------------------------------------------------------

    [Fact]
    public async Task ResolveAsync_WhenTheSourceFails_PropagatesAndCachesNothing()
    {
        var clock = new TestClock(Origin);
        var source = new ThrowingTokenSource(new GitHubCredentialException("GitHub App id is not configured."));
        var provider = new CachedGitHubCredentialProvider(source, clock);

        await Should.ThrowAsync<GitHubCredentialException>(() => provider.ResolveAsync());

        // A failed mint must not poison the gate: a second attempt still reaches the source.
        await Should.ThrowAsync<GitHubCredentialException>(() => provider.ResolveAsync());
    }

    [Fact]
    public async Task AuthenticateAsync_WithNullRequest_Throws()
    {
        var provider = new CachedGitHubCredentialProvider(
            new CountingTokenSource(i => TokenFor(i, Origin.AddHours(1))),
            new TestClock(Origin));

        await Should.ThrowAsync<ArgumentNullException>(() => provider.AuthenticateAsync(null!));
    }

    [Fact]
    public void Constructor_WithNullSource_Throws() =>
        Should.Throw<ArgumentNullException>(() => new CachedGitHubCredentialProvider(null!));

    // ---- AC4: the token never reaches a log line, an error message, or a public result ---------

    [Fact]
    public async Task Provider_NeverWritesTheTokenValueToAnyLogLine()
    {
        const string secret = "ghs_super_secret_value";
        var clock = new TestClock(Origin);
        var logger = new CapturingLogger();
        var source = new CountingTokenSource(_ => new GitHubInstallationToken(secret, clock.GetUtcNow().AddHours(1)));
        var provider = new CachedGitHubCredentialProvider(source, clock, TimeSpan.Zero, logger);

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/user");
        await provider.AuthenticateAsync(request);
        clock.Advance(TimeSpan.FromHours(2));
        using var second = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/user");
        await provider.AuthenticateAsync(second);

        logger.Lines.ShouldNotBeEmpty("vacuity guard: the provider must have logged something to check");
        foreach (var line in logger.Lines)
        {
            line.ShouldNotContain(secret);
        }
    }

    [Fact]
    public void InstallationToken_ToString_RedactsTheValue()
    {
        const string secret = "ghs_super_secret_value";
        var token = new GitHubInstallationToken(secret, Origin);

        // Records print every property by default; an accidental interpolation into a log template
        // or an exception message would otherwise leak the secret verbatim.
        token.ToString().ShouldNotContain(secret);
        token.ToString().ShouldContain("[redacted]");
        $"token: {token}".ShouldNotContain(secret);
    }

    [Fact]
    public void CredentialProviderContract_ExposesNoTokenReturningMember()
    {
        // AC4, structural half: the PUBLIC surface must have no way to obtain the secret. A method
        // named GetToken* or a property of token type would defeat every runtime redaction above.
        var offenders = typeof(IGitHubCredentialProvider)
            .GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(IsTokenBearing)
            .Select(m => m.Name)
            .ToArray();

        offenders.ShouldBeEmpty(
            "IGitHubCredentialProvider must not expose the installation token to callers (#2732): " +
            string.Join(", ", offenders));
    }

    [Fact]
    public void CachedProvider_HasNoPublicMemberReturningTheToken()
    {
        var offenders = typeof(CachedGitHubCredentialProvider)
            .GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(IsTokenBearing)
            .Select(m => m.Name)
            .ToArray();

        offenders.ShouldBeEmpty(
            "CachedGitHubCredentialProvider must keep the token off its public result surface (#2732): " +
            string.Join(", ", offenders));
    }

    private static bool IsTokenBearing(MemberInfo member) => member switch
    {
        PropertyInfo p => IsTokenType(p.PropertyType),
        MethodInfo m => IsTokenType(m.ReturnType) || IsTokenType(UnwrapTask(m.ReturnType)),
        FieldInfo f => IsTokenType(f.FieldType),
        _ => false,
    };

    private static Type UnwrapTask(Type type) =>
        type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Task<>)
            ? type.GetGenericArguments()[0]
            : type;

    private static bool IsTokenType(Type type) =>
        type == typeof(GitHubInstallationToken) || type == typeof(string);

    /// <summary>
    /// A manually advanced clock. Deliberately hand-rolled rather than pulling in
    /// <c>Microsoft.Extensions.TimeProvider.Testing</c>: one overridable method is cheaper than a new
    /// package in the extension test closure, and the point of AC3 is that expiry is evaluated
    /// against an injected clock at all.
    /// </summary>
    private sealed class TestClock : TimeProvider
    {
        private DateTimeOffset _now;

        public TestClock(DateTimeOffset start) => _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan delta) => _now += delta;
    }
}
