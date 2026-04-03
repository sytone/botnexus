using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using BotNexus.Agent;
using BotNexus.Agent.Tools;
using BotNexus.Core.Abstractions;
using BotNexus.Core.Configuration;
using BotNexus.Core.Models;
using BotNexus.Gateway;
using BotNexus.Providers.Base;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BotNexus.Tests.Integration.Tests;

[CollectionDefinition("workspace-integration", DisableParallelization = true)]
public sealed class WorkspaceIntegrationCollection;

[Collection("workspace-integration")]
public sealed class WorkspaceIntegrationTests
{
    [Fact]
    public async Task ConfigureAgent_FirstMessageInitializesWorkspace_WithBootstrapStubs()
    {
        using var home = new HomeOverrideScope();
        var provider = new ScriptedTestLlmProvider(static _ => new LlmResponse("ok", FinishReason.Stop));
        using var factory = CreateFactory(provider);

        var workspacePath = GetWorkspacePath(home.HomePath);
        Directory.Exists(workspacePath).Should().BeFalse();

        _ = await SendAndReceiveAsync(factory, "hello");

        File.Exists(Path.Combine(workspacePath, "SOUL.md")).Should().BeTrue();
        File.Exists(Path.Combine(workspacePath, "IDENTITY.md")).Should().BeTrue();
        File.Exists(Path.Combine(workspacePath, "USER.md")).Should().BeTrue();
    }

    [Fact]
    public async Task AgentWithWorkspaceFiles_SystemPromptIncludesWorkspaceFileContent()
    {
        using var home = new HomeOverrideScope();
        var workspacePath = PrepareWorkspace(home.HomePath);
        await File.WriteAllTextAsync(Path.Combine(workspacePath, "SOUL.md"), "soul-workspace-content");
        await File.WriteAllTextAsync(Path.Combine(workspacePath, "IDENTITY.md"), "identity-workspace-content");
        await File.WriteAllTextAsync(Path.Combine(workspacePath, "USER.md"), "user-workspace-content");

        var provider = new ScriptedTestLlmProvider(static _ => new LlmResponse("ok", FinishReason.Stop));
        using var factory = CreateFactory(provider);

        _ = await SendAndReceiveAsync(factory, "inspect workspace");
        var prompt = provider.Requests.Single().SystemPrompt;

        prompt.Should().Contain("## SOUL.md").And.Contain("soul-workspace-content");
        prompt.Should().Contain("## IDENTITY.md").And.Contain("identity-workspace-content");
        prompt.Should().Contain("## USER.md").And.Contain("user-workspace-content");
    }

    [Fact]
    public async Task AgentWithMemoryMarkdown_LongTermMemoryAppearsInSystemPrompt()
    {
        using var home = new HomeOverrideScope();
        var workspacePath = PrepareWorkspace(home.HomePath);
        await File.WriteAllTextAsync(Path.Combine(workspacePath, "MEMORY.md"), "long-term-memory-content");

        var provider = new ScriptedTestLlmProvider(static _ => new LlmResponse("ok", FinishReason.Stop));
        using var factory = CreateFactory(provider);

        _ = await SendAndReceiveAsync(factory, "load memory");
        var prompt = provider.Requests.Single().SystemPrompt;

        prompt.Should().Contain("## MEMORY.md");
        prompt.Should().Contain("long-term-memory-content");
    }

    [Fact]
    public async Task AgentWithDailyMemoryFiles_LoadsTodayAndYesterday_NotOlder()
    {
        using var home = new HomeOverrideScope();
        var workspacePath = PrepareWorkspace(home.HomePath);
        var dailyPath = Path.Combine(workspacePath, "memory", "daily");
        Directory.CreateDirectory(dailyPath);

        var today = DateTime.UtcNow.ToString("yyyy-MM-dd");
        var yesterday = DateTime.UtcNow.AddDays(-1).ToString("yyyy-MM-dd");
        var older = DateTime.UtcNow.AddDays(-7).ToString("yyyy-MM-dd");
        await File.WriteAllTextAsync(Path.Combine(dailyPath, $"{today}.md"), "today-memory-content");
        await File.WriteAllTextAsync(Path.Combine(dailyPath, $"{yesterday}.md"), "yesterday-memory-content");
        await File.WriteAllTextAsync(Path.Combine(dailyPath, $"{older}.md"), "older-memory-content");

        var provider = new ScriptedTestLlmProvider(static _ => new LlmResponse("ok", FinishReason.Stop));
        using var factory = CreateFactory(provider);

        _ = await SendAndReceiveAsync(factory, "load daily memory");
        var prompt = provider.Requests.Single().SystemPrompt;

        prompt.Should().Contain($"## memory/daily/{today}.md").And.Contain("today-memory-content");
        prompt.Should().Contain($"## memory/daily/{yesterday}.md").And.Contain("yesterday-memory-content");
        prompt.Should().NotContain("older-memory-content");
    }

    [Fact]
    public async Task EnableMemoryTrue_MemoryToolsAreAvailable_AndCallable()
    {
        using var home = new HomeOverrideScope();
        var workspacePath = PrepareWorkspace(home.HomePath);
        await File.WriteAllTextAsync(Path.Combine(workspacePath, "MEMORY.md"), "integration-memory-seed");

        var provider = new ScriptedTestLlmProvider(
            static _ => new LlmResponse(
                string.Empty,
                FinishReason.ToolCalls,
                [
                    new ToolCallRequest("call-1", "memory_save", new Dictionary<string, object?> { ["content"] = "saved from tool", ["target"] = "daily" }),
                    new ToolCallRequest("call-2", "memory_get", new Dictionary<string, object?> { ["file"] = "memory" }),
                    new ToolCallRequest("call-3", "memory_search", new Dictionary<string, object?> { ["query"] = "integration", ["max_results"] = 3 })
                ]),
            static _ => new LlmResponse("done", FinishReason.Stop));

        using var factory = CreateFactory(
            provider,
            new Dictionary<string, string?> { ["BotNexus:Agents:Named:default:EnableMemory"] = "true" });

        var response = await SendAndReceiveAsync(factory, "use memory tools");
        response.Should().Be("done");
        provider.Requests.Should().HaveCount(2);

        provider.Requests[0].Tools!.Select(t => t.Name).Should().Contain(["memory_search", "memory_save", "memory_get"]);
        provider.Requests[1].Messages.Count(m => m.Role == "tool").Should().BeGreaterThanOrEqualTo(3);

        var today = DateTimeOffset.Now.ToString("yyyy-MM-dd");
        var dailyPath = Path.Combine(workspacePath, "memory", "daily", $"{today}.md");
        File.Exists(dailyPath).Should().BeTrue();
        var saved = await File.ReadAllTextAsync(dailyPath);
        saved.Should().Contain("saved from tool");
    }

    [Fact]
    public async Task EnableMemoryFalse_MemoryToolsAreNotAvailable()
    {
        using var home = new HomeOverrideScope();
        var provider = new ScriptedTestLlmProvider(static _ => new LlmResponse("ok", FinishReason.Stop));
        using var factory = CreateFactory(
            provider,
            new Dictionary<string, string?> { ["BotNexus:Agents:Named:default:EnableMemory"] = "false" });

        _ = await SendAndReceiveAsync(factory, "no memory tools");

        provider.Requests.Should().ContainSingle();
        var toolNames = provider.Requests[0].Tools?.Select(t => t.Name).ToList() ?? [];
        toolNames.Should().NotContain(["memory_search", "memory_save", "memory_get"]);
    }

    [Fact]
    public async Task LargeWorkspaceFiles_AreTruncatedInSystemPrompt()
    {
        using var home = new HomeOverrideScope();
        var workspacePath = PrepareWorkspace(home.HomePath);
        var longContent = new string('Z', 120);
        await File.WriteAllTextAsync(Path.Combine(workspacePath, "SOUL.md"), longContent);

        var provider = new ScriptedTestLlmProvider(static _ => new LlmResponse("ok", FinishReason.Stop));
        using var factory = CreateFactory(
            provider,
            new Dictionary<string, string?> { ["BotNexus:Agents:Named:default:MaxContextFileChars"] = "40" });

        _ = await SendAndReceiveAsync(factory, "truncate context");
        var prompt = provider.Requests.Single().SystemPrompt;

        prompt.Should().Contain(new string('Z', 40));
        prompt.Should().Contain("[truncated]");
        prompt.Should().NotContain(new string('Z', 80));
    }

    [Fact]
    public async Task AutoGeneratedAgentsMarkdown_ListsAllConfiguredAgents()
    {
        using var home = new HomeOverrideScope();
        var provider = new ScriptedTestLlmProvider(static _ => new LlmResponse("ok", FinishReason.Stop));
        using var factory = CreateFactory(
            provider,
            new Dictionary<string, string?>
            {
                ["BotNexus:Agents:Model"] = "gpt-4o",
                ["BotNexus:Agents:Named:amy:Model"] = "gpt-4o-mini",
                ["BotNexus:Agents:Named:fry:Provider"] = "copilot",
                ["BotNexus:Agents:Named:fry:SystemPromptFile"] = "FRY_PROMPT.md"
            });

        _ = await SendAndReceiveAsync(factory, "list agents");
        var prompt = provider.Requests.Single().SystemPrompt;

        prompt.Should().Contain("## AGENTS.md");
        prompt.Should().Contain("### default");
        prompt.Should().Contain("### amy");
        prompt.Should().Contain("### fry");
        prompt.Should().Contain("- Provider: copilot");
        prompt.Should().Contain("- Role: from FRY_PROMPT.md");
    }

    [Fact]
    public async Task AutoGeneratedToolsMarkdown_ListsAvailableTools()
    {
        using var home = new HomeOverrideScope();
        var provider = new ScriptedTestLlmProvider(static _ => new LlmResponse("ok", FinishReason.Stop));
        using var factory = CreateFactory(
            provider,
            tools:
            [
                new TestTool("zeta", "Zeta tool"),
                new TestTool("alpha", "Alpha tool")
            ]);

        _ = await SendAndReceiveAsync(factory, "list tools");
        var prompt = provider.Requests.Single().SystemPrompt;

        prompt.Should().Contain("## TOOLS.md");
        prompt.Should().Contain("- alpha: Alpha tool");
        prompt.Should().Contain("- zeta: Zeta tool");
    }

    [Fact]
    public async Task AgentContextBuilderAndLoop_FullMessageFlowIncludesWorkspaceContext()
    {
        using var home = new HomeOverrideScope();
        var workspacePath = PrepareWorkspace(home.HomePath);
        await File.WriteAllTextAsync(Path.Combine(workspacePath, "SOUL.md"), "workspace-context-marker");

        var provider = new ScriptedTestLlmProvider(request =>
        {
            var sawMarker = request.SystemPrompt?.Contains("workspace-context-marker", StringComparison.Ordinal) == true;
            return new LlmResponse(sawMarker ? "context-seen" : "context-missing", FinishReason.Stop);
        });

        using var factory = CreateFactory(provider);
        var response = await SendAndReceiveAsync(factory, "end-to-end context");

        response.Should().Be("context-seen");
    }

    private static WebApplicationFactory<Program> CreateFactory(
        ScriptedTestLlmProvider provider,
        Dictionary<string, string?>? configOverrides = null,
        IReadOnlyList<ITool>? tools = null)
    {
        var config = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["BotNexus:Gateway:ApiKey"] = string.Empty,
            ["BotNexus:Gateway:WebSocketEnabled"] = "true",
            ["BotNexus:Agents:Model"] = "test-model",
            ["BotNexus:Agents:ContextWindowTokens"] = "4096"
        };

        if (configOverrides is not null)
        {
            foreach (var (key, value) in configOverrides)
                config[key] = value;
        }

        return new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((_, cb) =>
                {
                    cb.AddInMemoryCollection(config);
                });

                builder.ConfigureServices(services =>
                {
                    services.AddSingleton<ILlmProvider>(provider);

                    if (tools is not null)
                    {
                        foreach (var tool in tools)
                            services.AddSingleton(typeof(ITool), tool);
                    }

                    services.AddSingleton<IAgentRunner>(sp =>
                    {
                        var cfg = sp.GetRequiredService<IOptions<BotNexusConfig>>().Value;
                        var agentName = "default";
                        var agentCfg = cfg.Agents.Named.GetValueOrDefault(agentName);
                        var model = agentCfg?.Model ?? cfg.Agents.Model;
                        var generation = new GenerationSettings
                        {
                            Model = model,
                            MaxTokens = agentCfg?.MaxTokens ?? cfg.Agents.MaxTokens,
                            Temperature = agentCfg?.Temperature ?? cfg.Agents.Temperature,
                            ContextWindowTokens = cfg.Agents.ContextWindowTokens,
                            MaxToolIterations = agentCfg?.MaxToolIterations ?? cfg.Agents.MaxToolIterations
                        };

                        var loop = new AgentLoop(
                            agentName: agentName,
                            providerRegistry: sp.GetRequiredService<ProviderRegistry>(),
                            sessionManager: sp.GetRequiredService<ISessionManager>(),
                            contextBuilder: sp.GetRequiredService<IContextBuilderFactory>().Create(agentName),
                            toolRegistry: new ToolRegistry(),
                            settings: generation,
                            model: model,
                            providerName: agentCfg?.Provider,
                            additionalTools: sp.GetServices<ITool>().ToList(),
                            enableMemory: agentCfg?.EnableMemory == true,
                            memoryStore: sp.GetRequiredService<IMemoryStore>(),
                            logger: NullLogger<AgentLoop>.Instance,
                            maxToolIterations: agentCfg?.MaxToolIterations ?? cfg.Agents.MaxToolIterations);

                        return new AgentRunner(
                            agentName,
                            loop,
                            NullLogger<AgentRunner>.Instance,
                            sp.GetRequiredService<WebSocketChannel>());
                    });
                });
            });
    }

    private static async Task<string> SendAndReceiveAsync(WebApplicationFactory<Program> factory, string message)
    {
        using var socket = await factory.Server.CreateWebSocketClient().ConnectAsync(new Uri("ws://localhost/ws"), CancellationToken.None);

        _ = await ReceiveWebSocketJsonAsync(socket, CancellationToken.None); // connected
        await SendWebSocketJsonAsync(socket, new { type = "message", content = message }, CancellationToken.None);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!cts.IsCancellationRequested)
        {
            var payload = await ReceiveWebSocketJsonAsync(socket, cts.Token);
            if (payload.TryGetProperty("type", out var type) && type.GetString() == "response")
                return payload.GetProperty("content").GetString() ?? string.Empty;
        }

        throw new TimeoutException("Timed out waiting for websocket response.");
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

    private static string PrepareWorkspace(string homePath)
    {
        var workspacePath = GetWorkspacePath(homePath);
        Directory.CreateDirectory(Path.Combine(workspacePath, "memory", "daily"));
        return workspacePath;
    }

    private static string GetWorkspacePath(string homePath)
        => Path.Combine(homePath, "agents", "default");

    private sealed class HomeOverrideScope : IDisposable
    {
        private readonly string? _previous;
        public string HomePath { get; } = Path.Combine(Path.GetTempPath(), $"botnexus-workspace-int-{Guid.NewGuid():N}");

        public HomeOverrideScope()
        {
            _previous = Environment.GetEnvironmentVariable("BOTNEXUS_HOME");
            Environment.SetEnvironmentVariable("BOTNEXUS_HOME", HomePath);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("BOTNEXUS_HOME", _previous);
            if (Directory.Exists(HomePath))
                Directory.Delete(HomePath, recursive: true);
        }
    }

    private sealed class ScriptedTestLlmProvider(params Func<ChatRequest, LlmResponse>[] steps) : ILlmProvider
    {
        private int _index;
        public string DefaultModel => "test-model";
        public GenerationSettings Generation { get; set; } = new();

        public Task<IReadOnlyList<string>> GetAvailableModelsAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<string>>(new[] { DefaultModel });
        }
        public List<ChatRequest> Requests { get; } = [];

        public Task<LlmResponse> ChatAsync(ChatRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            if (_index < steps.Length)
                return Task.FromResult(steps[_index++](request));

            var fallback = request.Messages.LastOrDefault(static m => m.Role == "user")?.Content ?? "ok";
            return Task.FromResult(new LlmResponse(fallback, FinishReason.Stop));
        }

        public async IAsyncEnumerable<string> ChatStreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var response = await ChatAsync(request, cancellationToken);
            yield return response.Content;
        }
    }

    private sealed class TestTool(string name, string description) : ITool
    {
        public ToolDefinition Definition { get; } = new(name, description, new Dictionary<string, ToolParameterSchema>());
        public Task<string> ExecuteAsync(IReadOnlyDictionary<string, object?> arguments, CancellationToken cancellationToken = default)
            => Task.FromResult("ok");
    }
}
