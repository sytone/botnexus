using System.Diagnostics;
using System.Text.Json;
using BotNexus.Gateway.Configuration;
using FluentAssertions;

namespace BotNexus.Gateway.Tests.Configuration;

public sealed class ConfigPathResolverTests
{
    [Fact]
    public async Task ConfigGet_WithTopLevelPath_ReturnsGatewayObject()
    {
        await using var fixture = await CliConfigFixture.CreateAsync("""
            {
              "gateway": {
                "listenUrl": "http://localhost:5005",
                "defaultAgentId": "assistant"
              }
            }
            """);

        var result = await fixture.RunCliAsync("config", "get", "gateway");

        result.ExitCode.Should().Be(0);
        result.StdOut.Should().Contain("\"listenUrl\": \"http://localhost:5005\"");
        result.StdOut.Should().Contain("\"defaultAgentId\": \"assistant\"");
    }

    [Fact]
    public async Task ConfigGet_WithNestedPath_ReturnsPropertyValue()
    {
        await using var fixture = await CliConfigFixture.CreateAsync("""
            {
              "gateway": {
                "listenUrl": "http://localhost:5005"
              }
            }
            """);

        var result = await fixture.RunCliAsync("config", "get", "gateway.listenUrl");

        result.ExitCode.Should().Be(0);
        result.StdOut.Trim().Should().Be("http://localhost:5005");
    }

    [Fact]
    public async Task ConfigGet_WithDictionaryPath_ReturnsValue()
    {
        await using var fixture = await CliConfigFixture.CreateAsync("""
            {
              "providers": {
                "copilot": {
                  "apiKey": "abc123"
                }
              }
            }
            """);

        var result = await fixture.RunCliAsync("config", "get", "providers.copilot.apiKey");

        result.ExitCode.Should().Be(0);
        result.StdOut.Trim().Should().Be("abc123");
    }

    [Fact]
    public async Task ConfigSet_WithNestedBoolPath_ConvertsAndPersistsValue()
    {
        await using var fixture = await CliConfigFixture.CreateAsync("""
            {
              "agents": {
                "assistant": {
                  "provider": "copilot",
                  "model": "gpt-4.1",
                  "enabled": true
                }
              }
            }
            """);

        var setResult = await fixture.RunCliAsync("config", "set", "agents.assistant.enabled", "false");

        setResult.ExitCode.Should().Be(0);
        var config = await fixture.LoadConfigAsync();
        config.Agents!["assistant"].Enabled.Should().BeFalse();
    }

    [Fact]
    public async Task ConfigSet_WithDictionaryPath_SetsObjectValue()
    {
        await using var fixture = await CliConfigFixture.CreateAsync("""
            {
              "providers": {}
            }
            """);

        var setResult = await fixture.RunCliAsync("config", "set", "providers.copilot", "{\"apiKey\":\"token-1\"}");

        setResult.ExitCode.Should().Be(0);
        var config = await fixture.LoadConfigAsync();
        config.Providers!["copilot"].ApiKey.Should().Be("token-1");
    }

    [Fact]
    public async Task ConfigSet_WithJsonArrayValue_DeserializesList()
    {
        await using var fixture = await CliConfigFixture.CreateAsync("""
            {
              "gateway": {}
            }
            """);

        var setResult = await fixture.RunCliAsync(
            "config",
            "set",
            "gateway.cors.allowedOrigins",
            "[\"https://one.test\",\"https://two.test\"]");

        setResult.ExitCode.Should().Be(0);
        var config = await fixture.LoadConfigAsync();
        config.Gateway?.Cors?.AllowedOrigins.Should().Equal("https://one.test", "https://two.test");
    }

    [Fact]
    public async Task ConfigSet_WithNullForNullablePath_WritesNull()
    {
        await using var fixture = await CliConfigFixture.CreateAsync("""
            {
              "gateway": {
                "defaultAgentId": "assistant"
              }
            }
            """);

        var setResult = await fixture.RunCliAsync("config", "set", "gateway.defaultAgentId", "null");
        var getResult = await fixture.RunCliAsync("config", "get", "gateway.defaultAgentId");

        setResult.ExitCode.Should().Be(0);
        getResult.ExitCode.Should().Be(0);
        getResult.StdOut.Trim().Should().Be("null");
    }

    [Fact]
    public async Task ConfigGet_WithInvalidPath_ReturnsError()
    {
        await using var fixture = await CliConfigFixture.CreateAsync("""{"gateway":{"listenUrl":"http://localhost:5005"}}""");

        var result = await fixture.RunCliAsync("config", "get", "gateway.missing");

        result.ExitCode.Should().Be(1);
        result.CombinedOutput.Should().Contain("Property 'missing' does not exist");
    }

    [Fact]
    public async Task ConfigSet_WithInvalidBooleanValue_ReturnsError()
    {
        await using var fixture = await CliConfigFixture.CreateAsync("""
            {
              "agents": {
                "assistant": {
                  "provider": "copilot",
                  "model": "gpt-4.1",
                  "enabled": true
                }
              }
            }
            """);

        var result = await fixture.RunCliAsync("config", "set", "agents.assistant.enabled", "not-a-bool");

        result.ExitCode.Should().Be(1);
        result.CombinedOutput.Should().Contain("is not a valid boolean");
    }

    [Fact]
    public async Task ConfigPathLookup_IsCaseInsensitiveForPropertiesAndDictionaryKeys()
    {
        await using var fixture = await CliConfigFixture.CreateAsync("""
            {
              "providers": {
                "Copilot": {
                  "apiKey": "abc123"
                }
              }
            }
            """);

        var result = await fixture.RunCliAsync("config", "get", "PrOvIdErS.cOpIlOt.ApIkEy");

        result.ExitCode.Should().Be(0);
        result.StdOut.Trim().Should().Be("abc123");
    }

    [Fact]
    public async Task ConfigGet_WithBracketArrayIndexPath_ReturnsIndexedValue()
    {
        await using var fixture = await CliConfigFixture.CreateAsync("""
            {
              "gateway": {
                "cors": {
                  "allowedOrigins": [ "https://one.test", "https://two.test" ]
                }
              }
            }
            """);

        var result = await fixture.RunCliAsync("config", "get", "gateway.cors.allowedOrigins[1]");

        result.ExitCode.Should().Be(0);
        result.StdOut.Trim().Should().Be("https://two.test");
    }

    [Fact]
    public async Task ConfigSet_WithBracketArrayIndexPath_UpdatesIndexedValue()
    {
        await using var fixture = await CliConfigFixture.CreateAsync("""
            {
              "gateway": {
                "cors": {
                  "allowedOrigins": [ "https://one.test", "https://two.test" ]
                }
              }
            }
            """);

        var setResult = await fixture.RunCliAsync("config", "set", "gateway.cors.allowedOrigins[0]", "https://updated.test");

        setResult.ExitCode.Should().Be(0);
        var config = await fixture.LoadConfigAsync();
        config.Gateway?.Cors?.AllowedOrigins?[0].Should().Be("https://updated.test");
    }

    [Fact]
    public async Task ConfigGet_WithArrayIndexPath_ReturnsError()
    {
        await using var fixture = await CliConfigFixture.CreateAsync("""
            {
              "gateway": {
                "cors": {
                  "allowedOrigins": [ "https://one.test" ]
                }
              }
            }
            """);

        var result = await fixture.RunCliAsync("config", "get", "gateway.cors.allowedOrigins.0");

        result.ExitCode.Should().Be(1);
        result.CombinedOutput.Should().Contain("Property '0' does not exist");
    }

    private sealed class CliConfigFixture : IAsyncDisposable
    {
        private static readonly JsonSerializerOptions ReadOptions = new() { PropertyNameCaseInsensitive = true };
        private readonly string _rootPath;

        private CliConfigFixture(string rootPath)
        {
            _rootPath = rootPath;
            ConfigPath = Path.Combine(_rootPath, "config.json");
        }

        public string ConfigPath { get; }

        public static async Task<CliConfigFixture> CreateAsync(string json)
        {
            var rootPath = Path.Combine(Path.GetTempPath(), "botnexus-cli-config-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(rootPath);
            var fixture = new CliConfigFixture(rootPath);
            await File.WriteAllTextAsync(fixture.ConfigPath, json);
            return fixture;
        }

        public async Task<PlatformConfig> LoadConfigAsync()
        {
            await using var stream = File.OpenRead(ConfigPath);
            return (await JsonSerializer.DeserializeAsync<PlatformConfig>(stream, ReadOptions))!;
        }

        public async Task<CliResult> RunCliAsync(params string[] args)
        {
            var process = new Process
            {
                StartInfo = new ProcessStartInfo("dotnet")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            process.StartInfo.ArgumentList.Add(ResolveCliAssemblyPath());
            foreach (var argument in args)
                process.StartInfo.ArgumentList.Add(argument);
            process.StartInfo.Environment["BOTNEXUS_HOME"] = _rootPath;

            process.Start();
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                throw new TimeoutException("CLI command timed out.");
            }

            return new CliResult(
                process.ExitCode,
                await stdoutTask,
                await stderrTask);
        }

        private static string ResolveCliAssemblyPath()
        {
            var localCopy = Path.Combine(AppContext.BaseDirectory, "BotNexus.Cli.dll");
            var root = FindRepositoryRoot();
            var fallback = Path.Combine(root, "src", "gateway", "BotNexus.Cli", "bin", "Debug", "net10.0", "BotNexus.Cli.dll");
            var hasLocalCopy = File.Exists(localCopy);
            var hasFallback = File.Exists(fallback);

            if (hasLocalCopy && hasFallback)
            {
                var localTimestamp = File.GetLastWriteTimeUtc(localCopy);
                var fallbackTimestamp = File.GetLastWriteTimeUtc(fallback);
                return fallbackTimestamp > localTimestamp ? fallback : localCopy;
            }

            if (hasLocalCopy)
                return localCopy;

            if (hasFallback)
                return fallback;

            throw new FileNotFoundException("Unable to locate BotNexus.Cli.dll for config path tests.");
        }

        private static string FindRepositoryRoot()
        {
            var current = new DirectoryInfo(AppContext.BaseDirectory);
            while (current is not null)
            {
                if (File.Exists(Path.Combine(current.FullName, "BotNexus.slnx")))
                    return current.FullName;
                current = current.Parent;
            }

            throw new DirectoryNotFoundException("Repository root could not be resolved from test base path.");
        }

        public ValueTask DisposeAsync()
        {
            if (Directory.Exists(_rootPath))
                Directory.Delete(_rootPath, recursive: true);

            return ValueTask.CompletedTask;
        }
    }

    private sealed record CliResult(int ExitCode, string StdOut, string StdErr)
    {
        public string CombinedOutput => $"{StdOut}{Environment.NewLine}{StdErr}";
    }
}

