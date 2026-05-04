using System.Reflection;
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

