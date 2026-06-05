using System.Reflection;
using System.Text.Json;
using BotNexus.Gateway.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.IO.Abstractions.TestingHelpers;

namespace BotNexus.Gateway.Tests.Configuration;

/// <summary>
/// Phase 0f of the Copilot provider carve-out (#810): deployment-safety
/// regression for the OAuth token cache.
///
/// Loads a representative pre-carve-out <c>auth.json</c> fixture into a
/// real <see cref="GatewayAuthManager"/> (over a <see cref="MockFileSystem"/>)
/// and asserts that every surface a user's existing token file relies on
/// keeps working through Phase 1 — the carve-out must not require users to
/// re-authenticate or hand-edit their cached tokens.
///
/// Compatibility surfaces fenced here:
/// - S3a: The auth.json schema — <c>type</c>, <c>refresh</c>, <c>access</c>,
///   <c>expires</c>, <c>endpoint</c> — must keep deserializing. Renaming or
///   dropping any of these fields breaks every existing install.
/// - S3b: The provider key <c>"github-copilot"</c> in auth.json must resolve
///   when callers ask for either <c>"github-copilot"</c> (canonical) or
///   <c>"copilot"</c> (alias), via <see cref="GatewayAuthManager.TryGetAuthEntry"/>.
/// - S3c: The <c>endpoint</c> field must continue to flow through
///   <see cref="GatewayAuthManager.GetApiEndpoint"/> for both alias and
///   canonical keys.
/// - S3d: A future-dated <c>expires</c> must NOT trigger a refresh attempt
///   (we don't want every test or first boot to hit GitHub).
///
/// Sister to <c>ConfigCompatibilityTests</c> (#875) — config + token cache
/// together cover every user-managed file the carve-out can touch.
/// </summary>
public sealed class OAuthTokenCacheCompatibilityTests : IDisposable
{
    private static readonly string FixturePath = Path.Combine(
        AppContext.BaseDirectory,
        "Fixtures",
        "Auth",
        "precarveout-auth.json");

    private readonly string _rootPath;
    private readonly string _authFilePath;
    private readonly string _legacyAuthFilePath;
    private readonly MockFileSystem _fileSystem;
    private readonly string _fixtureJson;

    public OAuthTokenCacheCompatibilityTests()
    {
        _fixtureJson = File.ReadAllText(FixturePath);

        _fileSystem = new MockFileSystem();
        _rootPath = Path.Combine(Path.GetTempPath(), "botnexus", "phase0f-token-cache-tests");
        _fileSystem.Directory.CreateDirectory(_rootPath);
        _authFilePath = Path.Combine(_rootPath, "auth.json");
        _legacyAuthFilePath = Path.Combine(_rootPath, "legacy-auth.json");
    }

    [Fact]
    public void Fixture_ExposesExpectedAuthEntrySchema_OnGitHubCopilotEntry()
    {
        using var doc = JsonDocument.Parse(_fixtureJson);
        var copilot = doc.RootElement.GetProperty("github-copilot");

        copilot.GetProperty("type").GetString().ShouldBe("oauth");
        copilot.GetProperty("refresh").GetString().ShouldStartWith("gho_");
        copilot.GetProperty("access").GetString().ShouldStartWith("ghu_");
        copilot.GetProperty("expires").GetInt64().ShouldBeGreaterThan(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        copilot.GetProperty("endpoint").GetString().ShouldBe("https://api.enterprise.githubcopilot.com");

        // Exactly these five property names — no more, no less. A Phase 1 schema
        // change (rename / drop / add) trips this assertion.
        var actualPropertyNames = copilot.EnumerateObject().Select(p => p.Name).OrderBy(n => n).ToArray();
        actualPropertyNames.ShouldBe(["access", "endpoint", "expires", "refresh", "type"]);
    }

    [Fact]
    public async Task PreCarveOutAuthJson_LoadsAndResolvesGitHubCopilotApiKey()
    {
        WriteFixtureToAuthPath();
        var manager = CreateManager(new PlatformConfig());

        var apiKey = await manager.GetApiKeyAsync("github-copilot");

        apiKey.ShouldBe("ghu_REDACTED_ACCESS_TOKEN");
    }

    [Fact]
    public async Task PreCarveOutAuthJson_ResolvesCopilotAlias_FromGitHubCopilotEntry()
    {
        WriteFixtureToAuthPath();
        var manager = CreateManager(new PlatformConfig());

        var apiKey = await manager.GetApiKeyAsync("copilot");

        // S3b: the alias fallback in TryGetAuthEntry must keep working.
        apiKey.ShouldBe("ghu_REDACTED_ACCESS_TOKEN");
    }

    [Fact]
    public void PreCarveOutAuthJson_ExposesEndpoint_ForCanonicalKey()
    {
        WriteFixtureToAuthPath();
        var manager = CreateManager(new PlatformConfig());

        manager.GetApiEndpoint("github-copilot")
            .ShouldBe("https://api.enterprise.githubcopilot.com");
    }

    [Fact]
    public void PreCarveOutAuthJson_ExposesEndpoint_ForAliasKey()
    {
        WriteFixtureToAuthPath();
        var manager = CreateManager(new PlatformConfig());

        // S3c: alias resolution must include the endpoint override.
        manager.GetApiEndpoint("copilot")
            .ShouldBe("https://api.enterprise.githubcopilot.com");
    }

    [Fact]
    public async Task PreCarveOutAuthJson_FutureDatedExpires_DoesNotTriggerRefresh()
    {
        WriteFixtureToAuthPath();
        var manager = CreateManager(new PlatformConfig());

        // If refresh fired, this would attempt a real HTTP call to GitHub and
        // either hang on the timeout or fall through to null on failure. A
        // direct match on the cached access token proves no refresh occurred.
        (await manager.GetApiKeyAsync("github-copilot")).ShouldBe("ghu_REDACTED_ACCESS_TOKEN");
        (await manager.GetApiKeyAsync("github-copilot")).ShouldBe("ghu_REDACTED_ACCESS_TOKEN");
    }

    [Fact]
    public async Task PreCarveOutAuthJson_LegacyRepoLocation_StillResolves()
    {
        // ./.botnexus-agent/auth.json must remain a valid fallback location.
        await _fileSystem.File.WriteAllTextAsync(_legacyAuthFilePath, _fixtureJson);
        var manager = CreateManager(new PlatformConfig(), usePrimaryAuthPath: false);

        (await manager.GetApiKeyAsync("github-copilot")).ShouldBe("ghu_REDACTED_ACCESS_TOKEN");
    }

    public void Dispose()
    {
        if (_fileSystem.Directory.Exists(_rootPath))
            _fileSystem.Directory.Delete(_rootPath, recursive: true);
    }

    private void WriteFixtureToAuthPath() =>
        _fileSystem.File.WriteAllText(_authFilePath, _fixtureJson);

    private GatewayAuthManager CreateManager(PlatformConfig platformConfig, bool usePrimaryAuthPath = true)
    {
        var monitor = new StaticOptionsMonitor<PlatformConfig>(platformConfig);
        var manager = new GatewayAuthManager(monitor, NullLogger<GatewayAuthManager>.Instance, _fileSystem);
        var authPathField = typeof(GatewayAuthManager).GetField("_authFilePath", BindingFlags.NonPublic | BindingFlags.Instance);
        var legacyAuthPathField = typeof(GatewayAuthManager).GetField("_legacyAuthFilePath", BindingFlags.NonPublic | BindingFlags.Instance);
        authPathField.ShouldNotBeNull();
        legacyAuthPathField.ShouldNotBeNull();
        authPathField!.SetValue(manager, usePrimaryAuthPath ? _authFilePath : Path.Combine(_rootPath, "missing-auth.json"));
        legacyAuthPathField!.SetValue(manager, _legacyAuthFilePath);
        return manager;
    }
}

file sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
{
    public T CurrentValue { get; } = value;
    public T Get(string? name) => CurrentValue;
    public IDisposable OnChange(Action<T, string?> listener) => NoopDisposable.Instance;

    private sealed class NoopDisposable : IDisposable
    {
        public static readonly NoopDisposable Instance = new();
        public void Dispose() { }
    }
}
