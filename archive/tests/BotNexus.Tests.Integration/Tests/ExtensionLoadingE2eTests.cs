using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using BotNexus.Agent;
using BotNexus.Agent.Tools;
using BotNexus.Core.Abstractions;
using BotNexus.Core.Configuration;
using BotNexus.Core.Models;
using BotNexus.Gateway;
using BotNexus.Providers.Base;
using BotNexus.Session;
using BotNexus.Tests.Extensions.E2E;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BotNexus.Tests.Integration.Tests;

[CollectionDefinition("extension-loading-e2e", DisableParallelization = true)]
public sealed class ExtensionLoadingE2eCollection
{
    public const string Name = "extension-loading-e2e";
}

[Collection(ExtensionLoadingE2eCollection.Name)]
public sealed class ExtensionLoadingE2eTests : IAsyncLifetime
{
    private readonly string _testRoot = Path.Combine(AppContext.BaseDirectory, "extension-loading-e2e", Guid.NewGuid().ToString("N"));
    private string _extensionsRoot = string.Empty;
    private string ExtensionsRoot => _extensionsRoot;
    private string WorkspaceRoot => Path.Combine(_testRoot, "workspace");

    [Fact]
    public async Task GatewayStart_WithConfiguredExtensions_LoadsChannelsProvidersToolsAndTheyWork()
    {
        ResetTestFolders();
        DeployFixtureAssembly("providers", "fixture-provider");
        DeployFixtureAssembly("channels", "fixture-channel");
        DeployFixtureAssembly("tools", "fixture-tool");

        using var env = ApplyEnvironment(
            new Dictionary<string, string?>
            {
                ["BotNexus:Providers:fixture-provider:ApiKey"] = "test-key",
                ["BotNexus:Providers:fixture-provider:DefaultModel"] = "fixture-model",
                ["BotNexus:Channels:Instances:fixture-channel:Enabled"] = "true",
                ["BotNexus:Channels:Instances:fixture-channel:Name"] = "fixture-channel",
                ["BotNexus:Tools:Extensions:fixture-tool:Enabled"] = "true"
            });
        using var factory = CreateFactory();

        using var client = factory.CreateClient();
        (await client.GetAsync("/health")).EnsureSuccessStatusCode();

        using var scope = factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var channels = sp.GetServices<IChannel>().Select(c => c.Name).ToList();
        var registry = sp.GetRequiredService<ProviderRegistry>();
        var provider = registry.GetRequired("fixture-provider");
        var tool = sp.GetServices<ITool>().Single(t => t.Definition.Name == FixtureEchoTool.ToolName);

        channels.Should().Contain(["websocket", "fixture-channel"]);
        registry.GetProviderNames().Should().Contain("fixture-provider");
        (await tool.ExecuteAsync(new Dictionary<string, object?> { ["text"] = "ok" })).Should().Be("fixture-tool:ok");

        var providerResponse = await provider.ChatAsync(new ChatRequest(
            [new ChatMessage("user", "hello")],
            new GenerationSettings { Model = "fixture-model" }));
        providerResponse.Content.Should().Be("provider[fixture-model]:hello");
    }

    [Fact]
    public async Task GatewayStart_WithNoExtensions_StartsCleanlyWithOnlyWebSocketChannel()
    {
        ResetTestFolders();

        using var env = ApplyEnvironment();
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        (await client.GetAsync("/health")).EnsureSuccessStatusCode();
        var channelNames = await GetChannelNamesAsync(client);
        channelNames.Should().Equal("websocket");
    }

    [Fact]
    public async Task GatewayStart_WithPartialExtensions_LoadsAvailableAndLogsWarnings()
    {
        ResetTestFolders();
        DeployFixtureAssembly("tools", "available-tool");

        using var env = ApplyEnvironment(
            new Dictionary<string, string?>
            {
                ["BotNexus:Tools:Extensions:available-tool:Enabled"] = "true",
                ["BotNexus:Channels:Instances:missing-channel:Enabled"] = "true",
                ["BotNexus:Providers:missing-provider:ApiKey"] = "test-key"
            });

        using var writer = new StringWriter();
        var originalOut = Console.Out;
        Console.SetOut(writer);
        try
        {
            using var factory = CreateFactory();

            using var client = factory.CreateClient();
            (await client.GetAsync("/health")).EnsureSuccessStatusCode();

            using var scope = factory.Services.CreateScope();
            scope.ServiceProvider.GetServices<ITool>()
                .Select(t => t.Definition.Name)
                .Should()
                .Contain(FixtureEchoTool.ToolName);
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        var logs = writer.ToString();
        logs.Should().Contain("Extension folder not found");
    }

    [Fact]
    public async Task EndToEndMessageFlow_WebSocketToGatewayToAgentWithDynamicProviderToolCallToResponse()
    {
        ResetTestFolders();
        DeployFixtureAssembly("providers", "fixture-provider");
        DeployFixtureAssembly("tools", "fixture-tool");

        using var env = ApplyEnvironment(
            new Dictionary<string, string?>
            {
                ["BotNexus:Providers:fixture-provider:ApiKey"] = "test-key",
                ["BotNexus:Providers:fixture-provider:DefaultModel"] = "fixture-model",
                ["BotNexus:Tools:Extensions:fixture-tool:Enabled"] = "true",
                ["BotNexus:Agents:Model"] = "fixture-model"
            },
            addDefaultSettings: true);
        using var factory = CreateFactory(addTestRunner: true);

        using var ws = await factory.Server.CreateWebSocketClient().ConnectAsync(
            new Uri("ws://localhost/ws"),
            CancellationToken.None);

        var connected = await ReceiveWebSocketJsonAsync(ws, CancellationToken.None);
        connected.GetProperty("type").GetString().Should().Be("connected");

        await SendWebSocketJsonAsync(ws, new
        {
            type = "message",
            content = "please use tool now"
        }, CancellationToken.None);

        JsonElement response = default;
        for (var i = 0; i < 5; i++)
        {
            var incoming = await ReceiveWebSocketJsonAsync(ws, CancellationToken.None);
            if (incoming.TryGetProperty("type", out var type) && type.GetString() == "response")
            {
                response = incoming;
                break;
            }
        }

        response.ValueKind.Should().NotBe(JsonValueKind.Undefined);
        response.GetProperty("content").GetString()
            .Should()
            .Be("provider[fixture-model]:tool-finished:fixture-tool:from-provider");
    }

    [Fact]
    public async Task ChannelApi_IncludesDynamicallyLoadedChannels()
    {
        ResetTestFolders();
        DeployFixtureAssembly("channels", "fixture-channel");

        using var env = ApplyEnvironment(
            new Dictionary<string, string?>
            {
                ["BotNexus:Channels:Instances:fixture-channel:Enabled"] = "true",
                ["BotNexus:Channels:Instances:fixture-channel:Name"] = "fixture-channel"
            });
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var channelNames = await GetChannelNamesAsync(client);
        channelNames.Should().Contain(["websocket", "fixture-channel"]);
    }

    [Fact]
    public async Task ProviderSelection_ByModelName_UsesProviderRegistry()
    {
        ResetTestFolders();
        DeployFixtureAssembly("providers", "alpha");
        DeployFixtureAssembly("providers", "beta");

        using var env = ApplyEnvironment(
            new Dictionary<string, string?>
            {
                ["BotNexus:Providers:alpha:ApiKey"] = "test-key",
                ["BotNexus:Providers:alpha:DefaultModel"] = "alpha-model",
                ["BotNexus:Providers:beta:ApiKey"] = "test-key",
                ["BotNexus:Providers:beta:DefaultModel"] = "beta-model",
                ["BotNexus:Agents:Model"] = "beta-model"
            },
            addDefaultSettings: true);
        using var factory = CreateFactory(addTestRunner: true);

        using var ws = await factory.Server.CreateWebSocketClient().ConnectAsync(
            new Uri("ws://localhost/ws"),
            CancellationToken.None);

        _ = await ReceiveWebSocketJsonAsync(ws, CancellationToken.None); // connected
        await SendWebSocketJsonAsync(ws, new { type = "message", content = "provider selection check" }, CancellationToken.None);

        JsonElement response = default;
        for (var i = 0; i < 5; i++)
        {
            var incoming = await ReceiveWebSocketJsonAsync(ws, CancellationToken.None);
            if (incoming.TryGetProperty("type", out var type) && type.GetString() == "response")
            {
                response = incoming;
                break;
            }
        }

        response.ValueKind.Should().NotBe(JsonValueKind.Undefined);
        response.GetProperty("content").GetString().Should().Be("provider[beta-model]:provider selection check");
    }

    private WebApplicationFactory<Program> CreateFactory(bool addTestRunner = false)
    {
        return new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                if (addTestRunner)
                {
                    builder.ConfigureServices(services =>
                    {
                        services.AddSingleton<IAgentRunner>(sp =>
                        {
                            var cfg = sp.GetRequiredService<IOptions<BotNexusConfig>>().Value;
                            var agentCfg = cfg.Agents.Named.GetValueOrDefault("default");
                            var generation = new GenerationSettings
                            {
                                Model = cfg.Agents.Model,
                                MaxTokens = cfg.Agents.MaxTokens,
                                Temperature = cfg.Agents.Temperature,
                                ContextWindowTokens = cfg.Agents.ContextWindowTokens,
                                MaxToolIterations = cfg.Agents.MaxToolIterations
                            };

                            var loop = new AgentLoop(
                                agentName: "default",
                                providerRegistry: sp.GetRequiredService<ProviderRegistry>(),
                                sessionManager: sp.GetRequiredService<ISessionManager>(),
                                contextBuilder: new IntegrationContextBuilder("default", agentCfg?.SystemPrompt),
                                toolRegistry: new ToolRegistry(),
                                settings: generation,
                                additionalTools: sp.GetServices<ITool>().ToList(),
                                enableMemory: agentCfg?.EnableMemory == true,
                                memoryStore: sp.GetRequiredService<IMemoryStore>(),
                                logger: NullLogger<AgentLoop>.Instance,
                                maxToolIterations: cfg.Agents.MaxToolIterations);

                            return new AgentRunner(
                                agentName: "default",
                                agentLoop: loop,
                                logger: NullLogger<AgentRunner>.Instance,
                                responseChannel: sp.GetRequiredService<WebSocketChannel>());
                        });
                    });
                }
            });
    }

    private void DeployFixtureAssembly(string typeFolder, string key)
    {
        var source = typeof(FixtureLlmProvider).Assembly.Location;
        var destinationFolder = Path.Combine(ExtensionsRoot, typeFolder, key);
        Directory.CreateDirectory(destinationFolder);
        File.Copy(source, Path.Combine(destinationFolder, Path.GetFileName(source)), overwrite: true);
    }

    private void ResetTestFolders()
    {
        if (Directory.Exists(_testRoot))
        {
            try
            {
                Directory.Delete(_testRoot, recursive: true);
            }
            catch
            {
                // Best-effort cleanup; extension assemblies may remain locked until process end.
            }
        }

        _extensionsRoot = Path.Combine(_testRoot, "extensions", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(ExtensionsRoot);
        Directory.CreateDirectory(WorkspaceRoot);
    }

    private IDisposable ApplyEnvironment(
        Dictionary<string, string?>? overrides = null,
        bool addDefaultSettings = true)
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["BOTNEXUS_HOME"] = WorkspaceRoot
        };

        if (addDefaultSettings)
        {
            values["BotNexus:ExtensionsPath"] = ExtensionsRoot;
            values["BotNexus:Agents:Workspace"] = WorkspaceRoot;
            values["BotNexus:Gateway:ApiKey"] = string.Empty;
            values["BotNexus:Gateway:WebSocketEnabled"] = "true";
        }

        if (overrides is not null)
        {
            foreach (var (key, value) in overrides)
                values[key] = value;
        }

        return new EnvironmentOverride(values);
    }

    private static async Task<IReadOnlyList<string>> GetChannelNamesAsync(HttpClient client)
    {
        var response = await client.GetAsync("/api/channels");
        response.EnsureSuccessStatusCode();

        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return payload.RootElement
            .EnumerateArray()
            .Select(e => e.GetProperty("name").GetString() ?? string.Empty)
            .ToList();
    }

    private static async Task SendWebSocketJsonAsync(WebSocket socket, object payload, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(payload);
        var bytes = Encoding.UTF8.GetBytes(json);
        await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, cancellationToken);
    }

    private static async Task<JsonElement> ReceiveWebSocketJsonAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        using var ms = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer, cancellationToken);
            result.MessageType.Should().NotBe(WebSocketMessageType.Close);
            ms.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);

        using var json = JsonDocument.Parse(ms.ToArray());
        return json.RootElement.Clone();
    }

    public Task InitializeAsync()
    {
        ResetTestFolders();
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(_testRoot))
        {
            try
            {
                Directory.Delete(_testRoot, recursive: true);
            }
            catch
            {
                // Best-effort cleanup; extension assemblies may remain locked until process end.
            }
        }
        return Task.CompletedTask;
    }

    private sealed class EnvironmentOverride : IDisposable
    {
        private readonly Dictionary<string, string?> _original = new(StringComparer.OrdinalIgnoreCase);

        public EnvironmentOverride(IReadOnlyDictionary<string, string?> values)
        {
            foreach (var (key, value) in values)
            {
                var envKey = key.Replace(":", "__", StringComparison.Ordinal);
                _original[envKey] = Environment.GetEnvironmentVariable(envKey);
                Environment.SetEnvironmentVariable(envKey, value);
            }
        }

        public void Dispose()
        {
            foreach (var (key, value) in _original)
                Environment.SetEnvironmentVariable(key, value);
        }
    }
}

internal sealed class IntegrationContextBuilder(string agentName, string? configuredSystemPrompt) : IContextBuilder
{
    private readonly string _systemPrompt = string.IsNullOrWhiteSpace(configuredSystemPrompt)
        ? $"You are {agentName}"
        : configuredSystemPrompt;

    public Task<string> BuildSystemPromptAsync(string _, CancellationToken cancellationToken = default)
        => Task.FromResult(_systemPrompt);

    public Task<List<ChatMessage>> BuildMessagesAsync(
        string _,
        IReadOnlyList<ChatMessage> history,
        string currentMessage,
        string? channel = null,
        string? chatId = null,
        CancellationToken cancellationToken = default)
    {
        var messages = new List<ChatMessage>(history.Count + 2)
        {
            new("system", _systemPrompt)
        };
        messages.AddRange(history);
        messages.Add(new("user", currentMessage));
        return Task.FromResult(messages);
    }
}
