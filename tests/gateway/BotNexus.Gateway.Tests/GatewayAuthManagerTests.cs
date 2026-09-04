using System.Reflection;
using BotNexus.Agent.Providers.Core;
using BotNexus.Gateway.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.IO.Abstractions.TestingHelpers;

namespace BotNexus.Gateway.Tests;

public sealed class GatewayAuthManagerTests : IDisposable
{
    private readonly string _rootPath;
    private readonly string _authFilePath;
    private readonly string _legacyAuthFilePath;
    private readonly MockFileSystem _fileSystem;
    private readonly Dictionary<string, string?> _originalEnvironmentVariables = new(StringComparer.Ordinal);

    public GatewayAuthManagerTests()
    {
        _fileSystem = new MockFileSystem();
        _rootPath = Path.Combine(Path.GetTempPath(), "botnexus", "gateway-auth-tests");
        _fileSystem.Directory.CreateDirectory(_rootPath);
        _authFilePath = Path.Combine(_rootPath, "auth.json");
        _legacyAuthFilePath = Path.Combine(_rootPath, "legacy-auth.json");
    }

    [Fact]
    public async Task GetApiKeyAsync_WhenAuthJsonHasValidEntry_ReturnsAccessToken()
    {
        await _fileSystem.File.WriteAllTextAsync(_authFilePath, """
                                             {
                                               "openai": {
                                                 "type": "token",
                                                 "refresh": "unused",
                                                 "access": "auth-access-key",
                                                 "expires": 4102444800000,
                                                 "endpoint": "https://api.openai.test"
                                               }
                                             }
                                             """);

        var manager = CreateManager(new PlatformConfig());

        var apiKey = await manager.GetApiKeyAsync("openai");

        apiKey.ShouldBe("auth-access-key");
    }

    [Fact]
    public async Task GetApiKeyAsync_WhenCopilotUsesGithubCopilotEntry_ReturnsAccessToken()
    {
        await _fileSystem.File.WriteAllTextAsync(_authFilePath, """
                                             {
                                               "github-copilot": {
                                                 "type": "oauth",
                                                 "refresh": "unused",
                                                 "access": "copilot-access-key",
                                                 "expires": 4102444800000,
                                                 "endpoint": "https://api.enterprise.githubcopilot.com"
                                               }
                                             }
                                             """);

        var manager = CreateManager(new PlatformConfig());

        var apiKey = await manager.GetApiKeyAsync("copilot");

        apiKey.ShouldBe("copilot-access-key");
    }

    [Fact]
    public async Task GetApiKeyAsync_WhenHomeAuthMissing_UsesLegacyRepoAuthFile()
    {
        await _fileSystem.File.WriteAllTextAsync(_legacyAuthFilePath, """
                                                   {
                                                     "openai": {
                                                       "type": "token",
                                                       "refresh": "unused",
                                                       "access": "legacy-auth-access-key",
                                                       "expires": 4102444800000,
                                                       "endpoint": "https://api.openai.test"
                                                     }
                                                   }
                                                   """);

        var manager = CreateManager(new PlatformConfig(), usePrimaryAuthPath: false);

        var apiKey = await manager.GetApiKeyAsync("openai");

        apiKey.ShouldBe("legacy-auth-access-key");
    }

    [Fact]
    public async Task GetApiKeyAsync_WhenAuthJsonMissing_FallsBackToEnvironmentVariable()
    {
        SetEnvironmentVariable("OPENAI_API_KEY", "env-openai-key");
        var manager = CreateManager(new PlatformConfig());

        var apiKey = await manager.GetApiKeyAsync("openai");

        apiKey.ShouldBe("env-openai-key");
    }

    [Fact]
    public async Task GetApiKeyAsync_WhenAuthJsonIsInvalid_FallsBackToEnvironmentVariable()
    {
        await _fileSystem.File.WriteAllTextAsync(_authFilePath, "{ invalid json");
        SetEnvironmentVariable("OPENAI_API_KEY", "env-openai-key");
        var manager = CreateManager(new PlatformConfig());

        var apiKey = await manager.GetApiKeyAsync("openai");

        apiKey.ShouldBe("env-openai-key");
    }

    [Fact]
    public async Task GetApiKeyAsync_WhenNoAuthOrEnv_FallsBackToPlatformConfigApiKey()
    {
        SetEnvironmentVariable("OPENAI_API_KEY", null);
        var manager = CreateManager(new PlatformConfig
        {
            Providers = new Dictionary<string, ProviderConfig>
            {
                ["openai"] = new()
                {
                    ApiKey = "config-openai-key"
                }
            }
        });

        var apiKey = await manager.GetApiKeyAsync("openai");

        apiKey.ShouldBe("config-openai-key");
    }

    [Fact]
    public async Task GetApiKeyAsync_WhenPlatformConfigUsesAuthPrefix_ResolvesFromAuthJson()
    {
        SetEnvironmentVariable("OPENAI_API_KEY", null);
        await _fileSystem.File.WriteAllTextAsync(_authFilePath, """
                                             {
                                               "github-copilot": {
                                                 "type": "token",
                                                 "refresh": "unused",
                                                 "access": "copilot-auth-access-key",
                                                 "expires": 4102444800000,
                                                 "endpoint": "https://copilot.test"
                                               }
                                             }
                                             """);

        var manager = CreateManager(new PlatformConfig
        {
            Providers = new Dictionary<string, ProviderConfig>
            {
                ["openai"] = new()
                {
                    ApiKey = "auth:github-copilot"
                }
            }
        });

        var apiKey = await manager.GetApiKeyAsync("openai");

        apiKey.ShouldBe("copilot-auth-access-key");
    }

    [Fact]
    public async Task GetApiKeyAsync_WhenPlatformConfigUsesAuthCopilotPrefix_ResolvesGithubCopilotEntry()
    {
        SetEnvironmentVariable("OPENAI_API_KEY", null);
        await _fileSystem.File.WriteAllTextAsync(_authFilePath, """
                                             {
                                               "github-copilot": {
                                                 "type": "token",
                                                 "refresh": "unused",
                                                 "access": "copilot-auth-access-key",
                                                 "expires": 4102444800000,
                                                 "endpoint": "https://copilot.test"
                                               }
                                             }
                                             """);

        var manager = CreateManager(new PlatformConfig
        {
            Providers = new Dictionary<string, ProviderConfig>
            {
                ["openai"] = new()
                {
                    ApiKey = "auth:copilot"
                }
            }
        });

        var apiKey = await manager.GetApiKeyAsync("openai");

        apiKey.ShouldBe("copilot-auth-access-key");
    }

    [Fact]
    public async Task GetApiKeyAsync_WhenProviderIsNull_ReturnsNull()
    {
        var manager = CreateManager(new PlatformConfig());

        var apiKey = await manager.GetApiKeyAsync(null!);

        apiKey.ShouldBeNull();
    }

    [Fact]
    public void GetApiEndpoint_WhenAuthJsonHasEndpoint_ReturnsEndpoint()
    {
        _fileSystem.File.WriteAllText(_authFilePath, """
                                        {
                                          "openai": {
                                            "type": "token",
                                            "refresh": "unused",
                                            "access": "auth-access-key",
                                            "expires": 4102444800000,
                                            "endpoint": "https://auth-endpoint.test"
                                          }
                                        }
                                        """);
        var manager = CreateManager(new PlatformConfig());

        var endpoint = manager.GetApiEndpoint("openai");

        endpoint.ShouldBe("https://auth-endpoint.test");
    }

    [Fact]
    public void GetApiEndpoint_WhenAuthJsonMissing_FallsBackToPlatformConfigBaseUrl()
    {
        var manager = CreateManager(new PlatformConfig
        {
            Providers = new Dictionary<string, ProviderConfig>
            {
                ["openai"] = new()
                {
                    BaseUrl = "https://platform-base-url.test"
                }
            }
        });

        var endpoint = manager.GetApiEndpoint("openai");

        endpoint.ShouldBe("https://platform-base-url.test");
    }

    [Fact]
    public void GetApiEndpoint_WhenNoConfig_ReturnsNull()
    {
        var manager = CreateManager(new PlatformConfig());

        var endpoint = manager.GetApiEndpoint("openai");

        endpoint.ShouldBeNull();
    }

    [Fact]
    public void GetCopilotMcpEndpoint_WhenEnterpriseEndpointConfigured_DerivesEnterpriseMcpHost()
    {
        _fileSystem.File.WriteAllText(_authFilePath, """
                                        {
                                          "github-copilot": {
                                            "type": "oauth",
                                            "refresh": "unused",
                                            "access": "copilot-access-key",
                                            "expires": 4102444800000,
                                            "endpoint": "https://api.enterprise.githubcopilot.com"
                                          }
                                        }
                                        """);

        var manager = CreateManager(new PlatformConfig());

        // The enterprise chat host must flow through to a ready-to-use MCP endpoint (#1797).
        manager.GetCopilotMcpEndpoint("github-copilot")
            .ShouldBe("https://api.enterprise.githubcopilot.com/mcp");
    }

    [Fact]
    public void GetCopilotMcpEndpoint_WhenEndpointAlreadyHasMcpPath_DoesNotDoubleAppend()
    {
        _fileSystem.File.WriteAllText(_authFilePath, """
                                        {
                                          "github-copilot": {
                                            "type": "oauth",
                                            "refresh": "unused",
                                            "access": "copilot-access-key",
                                            "expires": 4102444800000,
                                            "endpoint": "https://api.enterprise.githubcopilot.com/mcp"
                                          }
                                        }
                                        """);

        var manager = CreateManager(new PlatformConfig());

        manager.GetCopilotMcpEndpoint("github-copilot")
            .ShouldBe("https://api.enterprise.githubcopilot.com/mcp");
    }

    [Fact]
    public void GetCopilotMcpEndpoint_WhenNoEndpointConfigured_ReturnsIndividualFallbackHost()
    {
        var manager = CreateManager(new PlatformConfig());

        // No override => individual/fallback host, unchanged from prior behaviour (#1797).
        manager.GetCopilotMcpEndpoint("github-copilot")
            .ShouldBe("https://api.githubcopilot.com/mcp");
    }

    // ---- #3673: out-of-process credential rotation must be observed without a restart ----

    private const string RotationTemplate = """
                                            {
                                              "openai": {
                                                "type": "token",
                                                "refresh": "unused",
                                                "access": "TOKENVALUE",
                                                "expires": 4102444800000,
                                                "endpoint": "https://api.openai.test"
                                              }
                                            }
                                            """;

    private static string AuthJsonWithToken(string token) => RotationTemplate.Replace("TOKENVALUE", token);

    [Fact]
    public async Task GetApiKeyAsync_WhenAuthFileRewrittenOutOfProcess_ReturnsRotatedToken()
    {
        // The rotation case from #3673: `botnexus provider login` rewrites auth.json while the
        // gateway is running. The cache used to latch on the first load, so the revoked token was
        // served until a restart. Same manager instance throughout - no restart is simulated.
        _fileSystem.File.WriteAllText(_authFilePath, AuthJsonWithToken("stale-token-aaa"));
        _fileSystem.File.SetLastWriteTimeUtc(_authFilePath, new DateTime(2026, 8, 29, 15, 40, 0, DateTimeKind.Utc));

        var manager = CreateManager(new PlatformConfig());
        (await manager.GetApiKeyAsync("openai")).ShouldBe("stale-token-aaa");

        _fileSystem.File.WriteAllText(_authFilePath, AuthJsonWithToken("rotated-token-bbb"));
        _fileSystem.File.SetLastWriteTimeUtc(_authFilePath, new DateTime(2026, 8, 29, 15, 47, 28, DateTimeKind.Utc));

        (await manager.GetApiKeyAsync("openai")).ShouldBe("rotated-token-bbb");
    }

    [Fact]
    public void GetApiEndpoint_WhenAuthFileRewrittenOutOfProcess_ReturnsRotatedEndpoint()
    {
        // The endpoint override rides the same cache, so it must rotate too.
        _fileSystem.File.WriteAllText(_authFilePath, AuthJsonWithToken("stale-token-aaa"));
        _fileSystem.File.SetLastWriteTimeUtc(_authFilePath, new DateTime(2026, 8, 29, 15, 40, 0, DateTimeKind.Utc));

        var manager = CreateManager(new PlatformConfig());
        manager.GetApiEndpoint("openai").ShouldBe("https://api.openai.test");

        _fileSystem.File.WriteAllText(_authFilePath, AuthJsonWithToken("stale-token-aaa").Replace("https://api.openai.test", "https://rotated.openai.test"));
        _fileSystem.File.SetLastWriteTimeUtc(_authFilePath, new DateTime(2026, 8, 29, 15, 47, 28, DateTimeKind.Utc));

        manager.GetApiEndpoint("openai").ShouldBe("https://rotated.openai.test");
    }

    [Fact]
    public async Task GetApiKeyAsync_WhenAuthFileUnchanged_DoesNotRereadFromDisk()
    {
        // Acceptance criterion 2: no per-call disk read in the steady state. Proven by mutating the
        // file's CONTENT while holding its observable stat (last-write time and length) fixed - a
        // manager that re-read on every call would return the new bytes. Both tokens are the same
        // length so the length component of the signature cannot be what carries the test.
        _fileSystem.File.WriteAllText(_authFilePath, AuthJsonWithToken("first-token-aaaa"));
        var frozenStamp = new DateTime(2026, 8, 29, 15, 40, 0, DateTimeKind.Utc);
        _fileSystem.File.SetLastWriteTimeUtc(_authFilePath, frozenStamp);

        var manager = CreateManager(new PlatformConfig());
        (await manager.GetApiKeyAsync("openai")).ShouldBe("first-token-aaaa");

        _fileSystem.File.WriteAllText(_authFilePath, AuthJsonWithToken("second-token-bbb"));
        _fileSystem.File.SetLastWriteTimeUtc(_authFilePath, frozenStamp);

        (await manager.GetApiKeyAsync("openai")).ShouldBe("first-token-aaaa");
        (await manager.GetApiKeyAsync("openai")).ShouldBe("first-token-aaaa");
    }

    [Fact]
    public async Task InvalidateCache_WhenStatIsUnchanged_ForcesReread()
    {
        // The explicit seam a 401/403 handler uses: it must override the stat-based shortcut, so the
        // stamp is deliberately held fixed here to prove invalidation - not mtime - did the work.
        _fileSystem.File.WriteAllText(_authFilePath, AuthJsonWithToken("first-token-aaaa"));
        var frozenStamp = new DateTime(2026, 8, 29, 15, 40, 0, DateTimeKind.Utc);
        _fileSystem.File.SetLastWriteTimeUtc(_authFilePath, frozenStamp);

        var manager = CreateManager(new PlatformConfig());
        (await manager.GetApiKeyAsync("openai")).ShouldBe("first-token-aaaa");

        _fileSystem.File.WriteAllText(_authFilePath, AuthJsonWithToken("second-token-bbb"));
        _fileSystem.File.SetLastWriteTimeUtc(_authFilePath, frozenStamp);

        manager.InvalidateCache();

        (await manager.GetApiKeyAsync("openai")).ShouldBe("second-token-bbb");
    }

    [Fact]
    public async Task GetApiKeyAsync_WhenAuthFileAppearsAfterFirstResolution_PicksItUp()
    {
        // A first-login-after-start rotation: the absent->present transition is a signature change
        // too, so the freshly written credential must be visible without a restart.
        SetEnvironmentVariable("OPENAI_API_KEY", null);
        var manager = CreateManager(new PlatformConfig());
        (await manager.GetApiKeyAsync("openai")).ShouldBeNull();

        _fileSystem.File.WriteAllText(_authFilePath, AuthJsonWithToken("post-login-token"));

        (await manager.GetApiKeyAsync("openai")).ShouldBe("post-login-token");
    }

    [Fact]
    public async Task InvokeWithAuthRetryAsync_WhenProviderReturns403_InvalidatesOnceAndRetriesOnce()
    {
        // Acceptance criterion 1 and 3. The stat is held FIXED across the rotation so that only an
        // explicit invalidation can make the second attempt see the new credential - if the retry
        // were relying on mtime granularity this test would hand it the stale token and fail.
        _fileSystem.File.WriteAllText(_authFilePath, AuthJsonWithToken("stale-token-aaaa"));
        var frozenStamp = new DateTime(2026, 8, 29, 15, 40, 0, DateTimeKind.Utc);
        _fileSystem.File.SetLastWriteTimeUtc(_authFilePath, frozenStamp);
        var manager = CreateManager(new PlatformConfig());

        var keysSeen = new List<string?>();
        var result = await manager.InvokeWithAuthRetryAsync(
            "openai",
            (apiKey, _) =>
            {
                keysSeen.Add(apiKey);
                if (keysSeen.Count == 1)
                {
                    // The out-of-process rotation lands between the two attempts, with the file's
                    // observable stat deliberately unchanged.
                    _fileSystem.File.WriteAllText(_authFilePath, AuthJsonWithToken("fresh-token-bbbb"));
                    _fileSystem.File.SetLastWriteTimeUtc(_authFilePath, frozenStamp);
                    throw new ProviderAuthenticationException("forbidden", 403, "openai");
                }

                return Task.FromResult("ok");
            });

        result.ShouldBe("ok");
        keysSeen.Count.ShouldBe(2);
        keysSeen[0].ShouldBe("stale-token-aaaa");
        keysSeen[1].ShouldBe("fresh-token-bbbb");
    }

    [Fact]
    public async Task InvokeWithAuthRetryAsync_WhenProviderIsPersistently403_DoesNotLoop()
    {
        // Acceptance criteria 2 and 4: the retry is structural, not a policy, so a provider that
        // never recovers is called exactly twice and the second failure surfaces unmodified.
        _fileSystem.File.WriteAllText(_authFilePath, AuthJsonWithToken("stale-token-aaaa"));
        var manager = CreateManager(new PlatformConfig());

        var attempts = 0;
        var failure = await Should.ThrowAsync<ProviderAuthenticationException>(async () =>
            await manager.InvokeWithAuthRetryAsync<string>(
                "openai",
                (_, _) =>
                {
                    attempts++;
                    throw new ProviderAuthenticationException($"forbidden #{attempts}", 403, "openai");
                }));

        attempts.ShouldBe(2);
        // The SECOND failure is what reaches the caller - not a rethrow of the first, and not a
        // wrapper that would hide the provider's own message.
        failure.Message.ShouldBe("forbidden #2");
    }

    [Fact]
    public async Task InvokeWithAuthRetryAsync_WhenProviderReturns500_DoesNotInvalidateOrRetry()
    {
        // Acceptance criterion 5. A non-auth fault must not spend an invalidation: proven by
        // rotating the file behind a frozen stat and showing a later resolution still sees the
        // stale value, which is only true if no invalidation happened.
        _fileSystem.File.WriteAllText(_authFilePath, AuthJsonWithToken("stale-token-aaaa"));
        var frozenStamp = new DateTime(2026, 8, 29, 15, 40, 0, DateTimeKind.Utc);
        _fileSystem.File.SetLastWriteTimeUtc(_authFilePath, frozenStamp);
        var manager = CreateManager(new PlatformConfig());

        var attempts = 0;
        await Should.ThrowAsync<HttpRequestException>(async () =>
            await manager.InvokeWithAuthRetryAsync<string>(
                "openai",
                (_, _) =>
                {
                    attempts++;
                    _fileSystem.File.WriteAllText(_authFilePath, AuthJsonWithToken("fresh-token-bbbb"));
                    _fileSystem.File.SetLastWriteTimeUtc(_authFilePath, frozenStamp);
                    throw new HttpRequestException("HTTP 500: upstream exploded");
                }));

        attempts.ShouldBe(1);
        (await manager.GetApiKeyAsync("openai")).ShouldBe("stale-token-aaaa");
    }

    public void Dispose()
    {
        foreach (var (name, value) in _originalEnvironmentVariables)
            Environment.SetEnvironmentVariable(name, value);

        if (_fileSystem.Directory.Exists(_rootPath))
            _fileSystem.Directory.Delete(_rootPath, recursive: true);
    }

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

    private void SetEnvironmentVariable(string name, string? value)
    {
        if (!_originalEnvironmentVariables.ContainsKey(name))
            _originalEnvironmentVariables[name] = Environment.GetEnvironmentVariable(name);

        Environment.SetEnvironmentVariable(name, value);
    }
}

/// <summary>Minimal IOptionsMonitor wrapper for tests that don't need change callbacks.</summary>
file sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
{
    public T CurrentValue => value;
    public T Get(string? name) => value;
    public IDisposable? OnChange(Action<T, string?> listener) => null;
}

