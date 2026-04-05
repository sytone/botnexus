using System.Reflection;
using BotNexus.AgentCore;
using BotNexus.AgentCore.Configuration;
using BotNexus.AgentCore.Types;
using BotNexus.CodingAgent.Auth;
using BotNexus.CodingAgent.Cli;
using BotNexus.CodingAgent.Session;
using BotNexus.Providers.Core;
using BotNexus.Providers.Core.Models;
using BotNexus.Providers.Core.Registry;
using FluentAssertions;

namespace BotNexus.CodingAgent.Tests.Cli;

public sealed class InteractiveLoopTests : IDisposable
{
    private readonly string _workingDirectory = Path.Combine(Path.GetTempPath(), $"botnexus-loop-{Guid.NewGuid():N}");
    private readonly SessionManager _sessionManager = new();
    private readonly CodingAgentConfig _config;
    private readonly ModelRegistry _modelRegistry = new();
    private readonly AuthManager _authManager;
    private readonly OutputFormatter _output = new();

    public InteractiveLoopTests()
    {
        Directory.CreateDirectory(_workingDirectory);
        _config = new CodingAgentConfig
        {
            Provider = "github-copilot",
            MaxContextTokens = 100000
        };
        _authManager = new AuthManager(_workingDirectory);
    }

    [Fact]
    public async Task HandleCommandAsync_ModelChange_WritesMetadata()
    {
        var agent = CreateAgent();
        var session = await _sessionManager.CreateSessionAsync(_workingDirectory, "model-change");

        await InvokeHandleCommandAsync("/model custom-model", agent, session);

        var sessionPath = Path.Combine(_workingDirectory, ".botnexus-agent", "sessions", $"{session.Id}.jsonl");
        var fileContent = await File.ReadAllTextAsync(sessionPath);
        fileContent.Should().Contain("\"key\":\"model_change\"");
        fileContent.Should().Contain("\"value\":\"gpt-4.1 \\u2192 custom-model\"");
    }

    private Agent CreateAgent()
    {
        var model = new LlmModel(
            Id: "gpt-4.1",
            Name: "gpt-4.1",
            Api: "openai-completions",
            Provider: "github-copilot",
            BaseUrl: "https://api.individual.githubcopilot.com",
            Reasoning: false,
            Input: ["text"],
            Cost: new ModelCost(0, 0, 0, 0),
            ContextWindow: 100000,
            MaxTokens: 8192,
            Headers: null);

        var llmClient = new LlmClient(new ApiProviderRegistry(), _modelRegistry);
        var options = new AgentOptions(
            InitialState: new AgentInitialState(Model: model),
            Model: model,
            LlmClient: llmClient,
            ConvertToLlm: (_, _) => Task.FromResult<IReadOnlyList<Message>>([]),
            TransformContext: (messages, _) => Task.FromResult(messages),
            GetApiKey: (_, _) => Task.FromResult<string?>(null),
            GetSteeringMessages: null,
            GetFollowUpMessages: null,
            ToolExecutionMode: ToolExecutionMode.Sequential,
            BeforeToolCall: null,
            AfterToolCall: null,
            GenerationSettings: new SimpleStreamOptions(),
            SteeringMode: QueueMode.OneAtATime,
            FollowUpMode: QueueMode.OneAtATime,
            SessionId: null);

        return new Agent(options);
    }

    private async Task<SessionInfo?> InvokeHandleCommandAsync(string command, Agent agent, SessionInfo session)
    {
        var method = typeof(InteractiveLoop).GetMethod("HandleCommandAsync", BindingFlags.NonPublic | BindingFlags.Static);
        method.Should().NotBeNull();

        var task = (Task<SessionInfo?>)method!.Invoke(
            null,
            [command, agent, _config, _modelRegistry, _authManager, _output, _sessionManager, session])!;

        return await task.ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (Directory.Exists(_workingDirectory))
        {
            Directory.Delete(_workingDirectory, recursive: true);
        }
    }
}
