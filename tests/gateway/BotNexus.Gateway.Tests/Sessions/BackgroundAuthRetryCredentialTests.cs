using System.IO.Abstractions.TestingHelpers;
using BotNexus.Agent.Providers.Core;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Registry;
using BotNexus.Agent.Providers.Core.Streaming;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Configuration;
using BotNexus.Gateway.Services;
using BotNexus.Gateway.Sessions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace BotNexus.Gateway.Tests.Sessions;

public sealed class BackgroundAuthRetryCredentialTests
{
    private static readonly LlmModel Model = new(
        Id: "retry-model", Name: "Retry model", Api: "retry-api", Provider: "retry-provider",
        BaseUrl: "https://retry.example.com", Reasoning: false, Input: ["text"],
        Cost: new ModelCost(0, 0, 0, 0), ContextWindow: 32000, MaxTokens: 4096);

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    [InlineData("credential-b")]
    public async Task AutoTitle_AuthRetry_UsesFreshCredentialWithoutLosingSessionId(string? replacementKey)
    {
        var auth = new MutableAuthFile("credential-a");
        var observed = new List<SimpleStreamOptions?>();
        var llmClient = CreateRetryingClient(observed, auth.ReplaceWith, replacementKey, "Retry title");
        var conversationId = ConversationId.From("retry-conversation");
        var agentId = AgentId.From("retry-agent");
        var sessionId = SessionId.From("retry-title-session");
        var conversation = new Conversation
        {
            ConversationId = conversationId,
            AgentId = agentId,
            Title = ConversationAutoTitleService.DefaultTitle
        };
        var store = new Mock<IConversationStore>();
        store.Setup(item => item.GetAsync(conversationId, It.IsAny<CancellationToken>())).ReturnsAsync(conversation);
        store.Setup(item => item.SaveAsync(It.IsAny<Conversation>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        var service = new ConversationAutoTitleService(
            store.Object, llmClient, NullLogger.Instance, notifier: null, authManager: auth.Manager);

        var result = await service.GenerateAndSaveAsync(
            conversationId, agentId, "question", "answer", preferredModelId: Model.Id, timeoutSeconds: 30,
            CancellationToken.None, sessionId);

        result.ShouldBe("Retry title");
        observed.Count.ShouldBe(2);
        observed[0]!.ApiKey.ShouldBe("credential-a");
        observed[1]!.ApiKey.ShouldBe(string.IsNullOrWhiteSpace(replacementKey) ? null : replacementKey);
        observed.ShouldAllBe(options => options!.SessionId == sessionId.Value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    [InlineData("credential-b")]
    public async Task Compaction_AuthRetry_UsesFreshCredentialAndPreservesNonCredentialOptions(string? replacementKey)
    {
        var auth = new MutableAuthFile("credential-a");
        var observed = new List<SimpleStreamOptions?>();
        var llmClient = CreateRetryingClient(observed, auth.ReplaceWith, replacementKey, "Retry summary");
        var compactor = new LlmSessionCompactor(
            llmClient, NullLogger<LlmSessionCompactor>.Instance, authManager: auth.Manager);
        var session = CreateLargeSession("retry-compaction-session");
        const int setupTimeoutMs = 4321;

        var result = await compactor.CompactAsync(session, new CompactionOptions
        {
            ContextWindowTokens = 100,
            TokenThresholdRatio = 0.01,
            PreservedTurns = 2,
            MaxSummaryChars = 5000,
            SummarizationModel = Model.Id,
            SummarizationProvider = Model.Provider,
            CronLlmIdleTimeoutMs = setupTimeoutMs
        });

        result.Succeeded.ShouldBeTrue();
        observed.Count.ShouldBe(2);
        observed[0]!.ApiKey.ShouldBe("credential-a");
        observed[1]!.ApiKey.ShouldBe(string.IsNullOrWhiteSpace(replacementKey) ? null : replacementKey);
        observed.ShouldAllBe(options => options!.SessionId == session.SessionId.Value);
        observed.ShouldAllBe(options => options!.StreamSetupTimeoutMs == setupTimeoutMs);
        observed.ShouldAllBe(options => options!.CancellationToken.CanBeCanceled == false);
    }

    private static LlmClient CreateRetryingClient(
        List<SimpleStreamOptions?> observed,
        Action<string?> replaceCredential,
        string? replacementKey,
        string responseText)
    {
        var callCount = 0;
        var provider = new Mock<IApiProvider>();
        provider.SetupGet(item => item.Api).Returns(Model.Api);
        provider.Setup(item => item.StreamSimple(
                It.IsAny<LlmModel>(), It.IsAny<Context>(), It.IsAny<SimpleStreamOptions?>()))
            .Returns((LlmModel _, Context _, SimpleStreamOptions? options) =>
            {
                observed.Add(options);
                callCount++;
                if (callCount == 1)
                {
                    replaceCredential(replacementKey);
                    throw new ProviderAuthenticationException("rejected", 401, Model.Provider);
                }

                return SuccessStream(responseText);
            });

        var providers = new ApiProviderRegistry();
        providers.Register(provider.Object);
        var models = new ModelRegistry();
        models.Register(Model.Provider, Model);
        return new LlmClient(providers, models);
    }

    private static LlmStream SuccessStream(string text)
    {
        var completion = new AssistantMessage(
            Content: [new TextContent(text)], Api: Model.Api, Provider: Model.Provider,
            ModelId: Model.Id, Usage: Usage.Empty(), StopReason: StopReason.Stop,
            ErrorMessage: null, ResponseId: null,
            Timestamp: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var stream = new LlmStream();
        stream.Push(new DoneEvent(StopReason.Stop, completion));
        stream.End(completion);
        return stream;
    }

    private static GatewaySession CreateLargeSession(string sessionId)
    {
        var session = new GatewaySession
        {
            SessionId = SessionId.From(sessionId),
            AgentId = AgentId.From("retry-agent")
        };
        session.AddEntries(Enumerable.Range(0, 300).Select(index => new SessionEntry
        {
            Role = index % 2 == 0 ? "user" : "assistant",
            Content = $"message {index} " + new string('x', 50)
        }));
        return session;
    }

    private sealed class MutableAuthFile
    {
        private readonly MockFileSystem _fileSystem = new();
        private readonly string _authFilePath;

        public MutableAuthFile(string initialKey)
        {
            var configDirectory = PlatformConfigLoader.GetDefaultConfigDirectory(_fileSystem);
            _fileSystem.Directory.CreateDirectory(configDirectory);
            _authFilePath = Path.Combine(configDirectory, "auth.json");
            ReplaceWith(initialKey);
            Manager = new GatewayAuthManager(
                new RetryStaticOptionsMonitor<PlatformConfig>(new PlatformConfig()),
                NullLogger<GatewayAuthManager>.Instance,
                _fileSystem);
        }

        public GatewayAuthManager Manager { get; }

        public void ReplaceWith(string? apiKey)
        {
            if (apiKey is null)
            {
                _fileSystem.File.Delete(_authFilePath);
                return;
            }

            var escapedKey = System.Text.Json.JsonSerializer.Serialize(apiKey);
            _fileSystem.File.WriteAllText(_authFilePath, $$"""
                {
                  "{{Model.Provider}}": {
                    "type": "apikey",
                    "refresh": "unused",
                    "access": {{escapedKey}},
                    "expires": 4102444800000,
                    "endpoint": "https://retry.example.com"
                  }
                }
                """);
        }
    }
}

file sealed class RetryStaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
{
    public T CurrentValue => value;
    public T Get(string? name) => value;
    public IDisposable? OnChange(Action<T, string?> listener) => null;
}
