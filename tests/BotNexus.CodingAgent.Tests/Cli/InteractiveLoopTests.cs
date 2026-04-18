using System.Reflection;
using System.Text.Json;
using BotNexus.Agent.Core;
using BotNexus.Agent.Core.Configuration;
using BotNexus.Agent.Core.Tools;
using BotNexus.Agent.Core.Types;
using BotNexus.CodingAgent.Auth;
using BotNexus.CodingAgent.Cli;
using BotNexus.CodingAgent.Extensions;
using BotNexus.CodingAgent.Session;
using BotNexus.Agent.Providers.Core;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Registry;
using BotNexus.Agent.Providers.Core.Streaming;
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
    public async Task HandleCommandAsync_ThinkingWithoutLevel_ShowsCurrentLevel()
    {
        var agent = CreateAgent();
        agent.State.ThinkingLevel = ThinkingLevel.Medium;
        var session = await _sessionManager.CreateSessionAsync(_workingDirectory, "thinking-current");

        var text = await CaptureConsoleAsync(async () =>
            await InvokeHandleCommandAsync("/thinking", agent, session));

        text.Should().Contain("Thinking level: medium");
    }

    [Fact]
    public async Task HandleCommandAsync_ThinkingWithValidLevel_ChangesLevelAndWritesMetadata()
    {
        var agent = CreateAgent();
        var session = await _sessionManager.CreateSessionAsync(_workingDirectory, "thinking-change");

        var updated = await InvokeHandleCommandAsync("/thinking high", agent, session);

        agent.State.ThinkingLevel.Should().Be(ThinkingLevel.High);
        updated.Should().NotBeNull();

        var sessionPath = Path.Combine(_workingDirectory, ".botnexus-agent", "sessions", $"{session.Id}.jsonl");
        var fileContent = await File.ReadAllTextAsync(sessionPath);
        fileContent.Should().Contain("\"key\":\"thinking_level_change\"");
        fileContent.Should().Contain("\"value\":\"off \\u2192 high\"");
    }

    [Fact]
    public async Task HandleCommandAsync_ThinkingWithInvalidLevel_ShowsError()
    {
        var agent = CreateAgent();
        var session = await _sessionManager.CreateSessionAsync(_workingDirectory, "thinking-invalid");

        var text = await CaptureConsoleAsync(async () =>
            await InvokeHandleCommandAsync("/thinking turbo", agent, session));

        text.Should().Contain("Invalid thinking level");
        agent.State.ThinkingLevel.Should().BeNull();
    }

    [Fact]
    public async Task HandleCommandAsync_Help_IncludesThinkingCommand()
    {
        var agent = CreateAgent();
        var session = await _sessionManager.CreateSessionAsync(_workingDirectory, "help");

        var text = await CaptureConsoleAsync(async () =>
            await InvokeHandleCommandAsync("/help", agent, session));

        text.Should().Contain("/thinking [level]");
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

    [Fact]
    public async Task RunAsync_AfterPrompt_PersistsSessionFile()
    {
        var session = await _sessionManager.CreateSessionAsync(_workingDirectory, "persist-after-prompt");
        var interactiveLoop = new InteractiveLoop();
        var llmClient = CreateLlmClient(new ScriptedProvider(_ => CreateAssistantTextStream("assistant reply")));
        var agent = CreateRuntimeAgent(llmClient);
        var extensionRunner = new ExtensionRunner([]);
        var originalIn = Console.In;
        Console.SetIn(new StringReader("hello\n/quit\n"));

        try
        {
            await interactiveLoop.RunAsync(
                agent,
                _config,
                llmClient,
                _modelRegistry,
                _authManager,
                extensionRunner,
                _sessionManager,
                session,
                _output,
                CancellationToken.None);
        }
        finally
        {
            Console.SetIn(originalIn);
        }

        var sessionPath = Path.Combine(_workingDirectory, ".botnexus-agent", "sessions", $"{session.Id}.jsonl");
        var fileContent = await File.ReadAllTextAsync(sessionPath);
        fileContent.Should().Contain("\"type\":\"message\"");
        fileContent.Should().Contain("assistant reply");
    }

    [Fact]
    public async Task RunAsync_WithToolResult_DoesNotPersistIntermediateToolLeaf()
    {
        var session = await _sessionManager.CreateSessionAsync(_workingDirectory, "persist-assistant-only");
        var interactiveLoop = new InteractiveLoop();
        var provider = new ScriptedProvider(callNumber => callNumber == 1
            ? CreateToolUseStream("bash", "tc-1")
            : CreateAssistantTextStream("done"));
        var llmClient = CreateLlmClient(provider);
        var agent = CreateRuntimeAgent(llmClient, [new StubTool("bash")]);
        var extensionRunner = new ExtensionRunner([]);
        var originalIn = Console.In;
        Console.SetIn(new StringReader("run tool\n/quit\n"));

        try
        {
            await interactiveLoop.RunAsync(
                agent,
                _config,
                llmClient,
                _modelRegistry,
                _authManager,
                extensionRunner,
                _sessionManager,
                session,
                _output,
                CancellationToken.None);
        }
        finally
        {
            Console.SetIn(originalIn);
        }

        var sessionPath = Path.Combine(_workingDirectory, ".botnexus-agent", "sessions", $"{session.Id}.jsonl");
        var lines = await File.ReadAllLinesAsync(sessionPath);
        lines.Count(line => line.Contains("\"key\":\"leaf\"", StringComparison.Ordinal)).Should().Be(1);
    }

    [Fact]
    public async Task RunAsync_WhenSessionPersistenceThrows_PropagatesError()
    {
        var session = new SessionInfo(
            Id: "missing-session",
            Name: "missing",
            CreatedAt: DateTimeOffset.UtcNow,
            UpdatedAt: DateTimeOffset.UtcNow,
            MessageCount: 0,
            Model: null,
            WorkingDirectory: _workingDirectory,
            Version: 2,
            ParentSessionId: null,
            ActiveLeafId: null,
            SessionFilePath: Path.Combine(_workingDirectory, ".botnexus-agent", "sessions", "missing-session.jsonl"));
        var interactiveLoop = new InteractiveLoop();
        var llmClient = CreateLlmClient(new ScriptedProvider(_ => CreateAssistantTextStream("assistant")));
        var agent = CreateRuntimeAgent(llmClient);
        var extensionRunner = new ExtensionRunner([]);
        var originalIn = Console.In;
        Console.SetIn(new StringReader("/quit\n"));
        var missingPath = session.SessionFilePath!;
        if (File.Exists(missingPath))
        {
            File.Delete(missingPath);
        }

        try
        {
            var action = async () => await interactiveLoop.RunAsync(
                agent,
                _config,
                llmClient,
                _modelRegistry,
                _authManager,
                extensionRunner,
                _sessionManager,
                session,
                _output,
                CancellationToken.None);

            await action.Should().ThrowAsync<FileNotFoundException>();
        }
        finally
        {
            Console.SetIn(originalIn);
        }
    }

    private BotNexus.Agent.Core.Agent CreateAgent()
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

        return new BotNexus.Agent.Core.Agent(options);
    }

    private LlmClient CreateLlmClient(IApiProvider provider)
    {
        var registry = new ApiProviderRegistry();
        registry.Register(provider);
        return new LlmClient(registry, _modelRegistry);
    }

    private BotNexus.Agent.Core.Agent CreateRuntimeAgent(LlmClient llmClient, IReadOnlyList<IAgentTool>? tools = null)
    {
        var model = new LlmModel(
            Id: "test-model",
            Name: "test-model",
            Api: "test-api",
            Provider: "github-copilot",
            BaseUrl: "https://example.invalid",
            Reasoning: false,
            Input: ["text"],
            Cost: new ModelCost(0, 0, 0, 0),
            ContextWindow: 100000,
            MaxTokens: 8192,
            Headers: null);

        var options = new AgentOptions(
            InitialState: new AgentInitialState(Model: model, Tools: tools ?? []),
            Model: model,
            LlmClient: llmClient,
            ConvertToLlm: DefaultMessageConverter.ConvertToLlm,
            TransformContext: (messages, _) => Task.FromResult(messages),
            GetApiKey: (_, _) => Task.FromResult<string?>("test-key"),
            GetSteeringMessages: null,
            GetFollowUpMessages: null,
            ToolExecutionMode: ToolExecutionMode.Sequential,
            BeforeToolCall: null,
            AfterToolCall: null,
            GenerationSettings: new SimpleStreamOptions(),
            SteeringMode: QueueMode.OneAtATime,
            FollowUpMode: QueueMode.OneAtATime,
            SessionId: null);

        return new BotNexus.Agent.Core.Agent(options);
    }

    private async Task<SessionInfo?> InvokeHandleCommandAsync(string command, BotNexus.Agent.Core.Agent agent, SessionInfo session)
    {
        var method = typeof(InteractiveLoop).GetMethod("HandleCommandAsync", BindingFlags.NonPublic | BindingFlags.Static);
        method.Should().NotBeNull();

        var task = (Task<SessionInfo?>)method!.Invoke(
            null,
            [command, agent, _config, _modelRegistry, _authManager, _output, _sessionManager, session])!;

        return await task.ConfigureAwait(false);
    }

    private static async Task<string> CaptureConsoleAsync(Func<Task> action)
    {
        var original = Console.Out;
        using var writer = new StringWriter();
        Console.SetOut(writer);
        try
        {
            await action().ConfigureAwait(false);
            return writer.ToString();
        }
        finally
        {
            Console.SetOut(original);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_workingDirectory))
        {
            Directory.Delete(_workingDirectory, recursive: true);
        }
    }

    private static LlmStream CreateAssistantTextStream(string text)
    {
        var stream = new LlmStream();
        var message = new AssistantMessage(
            Content: [new TextContent(text)],
            Api: "test-api",
            Provider: "github-copilot",
            ModelId: "test-model",
            Usage: Usage.Empty(),
            StopReason: StopReason.Stop,
            ErrorMessage: null,
            ResponseId: "resp_1",
            Timestamp: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        stream.Push(new StartEvent(message));
        stream.Push(new TextStartEvent(0, message));
        stream.Push(new TextDeltaEvent(0, text, message));
        stream.Push(new TextEndEvent(0, text, message));
        stream.Push(new DoneEvent(StopReason.Stop, message));
        stream.End(message);
        return stream;
    }

    private static LlmStream CreateToolUseStream(string toolName, string toolCallId)
    {
        var stream = new LlmStream();
        var toolCall = new ToolCallContent(toolCallId, toolName, new Dictionary<string, object?> { ["value"] = "x" });
        var message = new AssistantMessage(
            Content: [toolCall],
            Api: "test-api",
            Provider: "github-copilot",
            ModelId: "test-model",
            Usage: Usage.Empty(),
            StopReason: StopReason.ToolUse,
            ErrorMessage: null,
            ResponseId: "resp_tool",
            Timestamp: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        stream.Push(new StartEvent(message));
        stream.Push(new ToolCallStartEvent(0, message));
        stream.Push(new ToolCallDeltaEvent(0, "{\"value\":\"x\"}", message));
        stream.Push(new ToolCallEndEvent(0, toolCall, message));
        stream.Push(new DoneEvent(StopReason.ToolUse, message));
        stream.End(message);
        return stream;
    }

    private sealed class ScriptedProvider(Func<int, LlmStream> streamFactory) : IApiProvider
    {
        private int _callCount;
        public string Api => "test-api";

        public LlmStream Stream(LlmModel model, Context context, StreamOptions? options = null)
        {
            _ = model;
            _ = context;
            _ = options;
            return streamFactory(Interlocked.Increment(ref _callCount));
        }

        public LlmStream StreamSimple(LlmModel model, Context context, SimpleStreamOptions? options = null)
            => Stream(model, context, options);
    }

    private sealed class StubTool(string name) : IAgentTool
    {
        private static readonly JsonElement Schema = JsonDocument.Parse("""{"type":"object"}""").RootElement.Clone();
        public string Name => name;
        public string Label => name;
        public Tool Definition => new(name, "stub tool", Schema);

        public Task<IReadOnlyDictionary<string, object?>> PrepareArgumentsAsync(
            IReadOnlyDictionary<string, object?> arguments,
            CancellationToken cancellationToken = default)
            => Task.FromResult(arguments);

        public Task<AgentToolResult> ExecuteAsync(
            string toolCallId,
            IReadOnlyDictionary<string, object?> arguments,
            CancellationToken cancellationToken = default,
            AgentToolUpdateCallback? onUpdate = null)
            => Task.FromResult(new AgentToolResult([new AgentToolContent(AgentToolContentType.Text, "ok")]));
    }
}
