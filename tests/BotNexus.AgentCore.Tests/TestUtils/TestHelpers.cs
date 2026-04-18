using BotNexus.Agent.Core.Configuration;
using BotNexus.Agent.Core.Types;
using BotNexus.Agent.Providers.Core;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Registry;

namespace BotNexus.AgentCore.Tests.TestUtils;

using AgentUserMessage = BotNexus.Agent.Core.Types.UserMessage;

internal static class TestHelpers
{
    private static readonly ApiProviderRegistry SharedApiProviderRegistry = new();
    private static readonly ModelRegistry SharedModelRegistry = new();

    public static LlmClient CreateLlmClient() => new(SharedApiProviderRegistry, SharedModelRegistry);

    public static LlmModel CreateTestModel(string api = "test-api")
    {
        return new LlmModel(
            Id: "test-model",
            Name: "Test Model",
            Api: api,
            Provider: "test-provider",
            BaseUrl: "http://localhost",
            Reasoning: false,
            Input: ["text"],
            Cost: new ModelCost(0, 0, 0, 0),
            ContextWindow: 4096,
            MaxTokens: 1024);
    }

    public static AgentLoopConfig CreateTestConfig(
        LlmModel? model = null,
        ToolExecutionMode toolExecutionMode = ToolExecutionMode.Sequential,
        BeforeToolCallDelegate? beforeToolCall = null,
        AfterToolCallDelegate? afterToolCall = null)
    {
        return new AgentLoopConfig(
            Model: model ?? CreateTestModel(),
            LlmClient: CreateLlmClient(),
            ConvertToLlm: (messages, _) => Task.FromResult<IReadOnlyList<Message>>(ConvertMessages(messages)),
            TransformContext: (messages, _) => Task.FromResult(messages),
            GetApiKey: (_, _) => Task.FromResult<string?>(null),
            GetSteeringMessages: null,
            GetFollowUpMessages: null,
            ToolExecutionMode: toolExecutionMode,
            BeforeToolCall: beforeToolCall,
            AfterToolCall: afterToolCall,
            GenerationSettings: new SimpleStreamOptions());
    }

    public static AgentOptions CreateTestOptions(
        AgentInitialState? initialState = null,
        LlmModel? model = null,
        QueueMode steeringMode = QueueMode.All,
        QueueMode followUpMode = QueueMode.All)
    {
        return new AgentOptions(
            InitialState: initialState,
            Model: model ?? CreateTestModel(),
            LlmClient: CreateLlmClient(),
            ConvertToLlm: (messages, _) => Task.FromResult<IReadOnlyList<Message>>(ConvertMessages(messages)),
            TransformContext: (messages, _) => Task.FromResult(messages),
            GetApiKey: (_, _) => Task.FromResult<string?>(null),
            GetSteeringMessages: null,
            GetFollowUpMessages: null,
            ToolExecutionMode: ToolExecutionMode.Sequential,
            BeforeToolCall: null,
            AfterToolCall: null,
            GenerationSettings: new SimpleStreamOptions(),
            SteeringMode: steeringMode,
            FollowUpMode: followUpMode,
            SessionId: "test-session");
    }

    public static AgentContext CreateEmptyContext() => new(null, [], []);

    public static AgentUserMessage CreateUserMessage(string text) => new(text);

    public static ToolResultAgentMessage CreateToolResultMessage(string toolCallId, string toolName, string content)
    {
        return new ToolResultAgentMessage(
            ToolCallId: toolCallId,
            ToolName: toolName,
            Result: new AgentToolResult([new AgentToolContent(AgentToolContentType.Text, content)]));
    }

    public static IDisposable RegisterProvider(TestApiProvider provider, string? sourceId = null)
    {
        var resolvedSourceId = sourceId ?? $"tests-{Guid.NewGuid():N}";
        SharedApiProviderRegistry.Register(provider, resolvedSourceId);
        return new ApiProviderScope(SharedApiProviderRegistry, resolvedSourceId);
    }

    private static IReadOnlyList<Message> ConvertMessages(IReadOnlyList<AgentMessage> messages)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var providerMessages = new List<Message>();

        foreach (var message in messages)
        {
            switch (message)
            {
                case AgentUserMessage userMessage:
                    providerMessages.Add(new BotNexus.Agent.Providers.Core.Models.UserMessage(new UserMessageContent(userMessage.Content), timestamp));
                    break;
                case ToolResultAgentMessage toolResult:
                    providerMessages.Add(new ToolResultMessage(
                        toolResult.ToolCallId,
                        toolResult.ToolName,
                        toolResult.Result.Content.Select(content => (ContentBlock)new TextContent(content.Value)).ToList(),
                        toolResult.IsError,
                        timestamp));
                    break;
                case AssistantAgentMessage assistant:
                    providerMessages.Add(new AssistantMessage(
                        Content: [new TextContent(assistant.Content)],
                        Api: "agent-core-test",
                        Provider: "agent-core-test",
                        ModelId: "agent-core-test",
                        Usage: Usage.Empty(),
                        StopReason: assistant.FinishReason,
                        ErrorMessage: assistant.ErrorMessage,
                        ResponseId: null,
                        Timestamp: timestamp));
                    break;
            }
        }

        return providerMessages;
    }
}
