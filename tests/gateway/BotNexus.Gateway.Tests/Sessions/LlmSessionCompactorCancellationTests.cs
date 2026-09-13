using System.Diagnostics;
using System.IO.Abstractions.TestingHelpers;
using BotNexus.Agent.Providers.Core;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Registry;
using BotNexus.Agent.Providers.Core.Streaming;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Configuration;
using BotNexus.Gateway.Sessions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace BotNexus.Gateway.Tests.Sessions;

public sealed class LlmSessionCompactorCancellationTests
{
    private static readonly LlmModel PrimaryModel = new(
        Id: "timeout-primary", Name: "Timeout primary", Api: "timeout-api", Provider: "timeout-provider",
        BaseUrl: "https://timeout.example.com", Reasoning: false, Input: ["text"],
        Cost: new ModelCost(0, 0, 0, 0), ContextWindow: 32000, MaxTokens: 4096);

    private static readonly LlmModel FallbackModel = new(
        Id: "claude-haiku-4.5", Name: "Fallback", Api: "fallback-api", Provider: "fallback-provider",
        BaseUrl: "https://fallback.example.com", Reasoning: false, Input: ["text"],
        Cost: new ModelCost(0, 0, 0, 0), ContextWindow: 32000, MaxTokens: 4096);

    [Fact]
    public async Task CompactAsync_TimeoutWithoutAuthManager_CancelsProviderBeforeFallback()
    {
        CancellationToken providerToken = default;
        var primaryCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fallbackStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var compactor = CreateCompactor(
            authManager: null,
            (PrimaryModel, options =>
            {
                providerToken = options!.CancellationToken;
                return CancellationAwarePendingStream(options, _ => primaryCancelled.TrySetResult());
            }),
            (FallbackModel, _ =>
            {
                fallbackStarted.TrySetResult();
                return SuccessStream("fallback summary");
            }));

        var result = await compactor.CompactAsync(CreateLargeSession(), TimeoutOptions());

        await primaryCancelled.Task.WaitAsync(TimeSpan.FromSeconds(1));
        result.Succeeded.ShouldBeTrue();
        result.Summary.ShouldContain("fallback summary");
        providerToken.CanBeCanceled.ShouldBeTrue();
        providerToken.IsCancellationRequested.ShouldBeTrue();
        fallbackStarted.Task.IsCompletedSuccessfully.ShouldBeTrue();
    }

    [Fact]
    public async Task CompactAsync_TimeoutWithAuthManager_CancelsProviderAcrossBoundedRetryBudget()
    {
        var observedTokens = new List<CancellationToken>();
        var callCount = 0;
        var compactor = CreateCompactor(
            CreateAuthManagerWithToken(PrimaryModel.Provider, "credential"),
            (PrimaryModel, options =>
            {
                observedTokens.Add(options!.CancellationToken);
                callCount++;
                if (callCount == 1)
                    throw new ProviderAuthenticationException("rejected", 401, PrimaryModel.Provider);

                return CancellationAwarePendingStream(options, _ => { });
            }));

        var stopwatch = Stopwatch.StartNew();
        var result = await compactor.CompactAsync(CreateLargeSession(), TimeoutOptions());
        stopwatch.Stop();

        result.Succeeded.ShouldBeFalse();
        observedTokens.Count.ShouldBe(2);
        observedTokens.ShouldAllBe(token => token.CanBeCanceled);
        observedTokens.ShouldAllBe(token => token.IsCancellationRequested);
        observedTokens[0].ShouldBe(observedTokens[1]);
        stopwatch.Elapsed.ShouldBeLessThan(TimeSpan.FromMilliseconds(1800),
            "the configured timeout is one budget for the model candidate, not one budget per auth attempt");
    }

    [Fact]
    public async Task CompactAsync_CallerCancellation_RemainsCallerCancellationAndReachesProvider()
    {
        CancellationToken providerToken = default;
        var providerCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var compactor = CreateCompactor(
            authManager: null,
            (PrimaryModel, options =>
            {
                providerToken = options!.CancellationToken;
                return CancellationAwarePendingStream(options, _ => providerCancelled.TrySetResult());
            }));
        using var callerCts = new CancellationTokenSource();
        callerCts.CancelAfter(TimeSpan.FromMilliseconds(100));

        await Should.ThrowAsync<OperationCanceledException>(() =>
            compactor.CompactAsync(CreateLargeSession(), TimeoutOptions(timeoutSeconds: 30), callerCts.Token));

        await providerCancelled.Task.WaitAsync(TimeSpan.FromSeconds(1));
        providerToken.IsCancellationRequested.ShouldBeTrue();
    }

    private static CompactionOptions TimeoutOptions(int timeoutSeconds = 1) => new()
    {
        ContextWindowTokens = 100,
        TokenThresholdRatio = 0.01,
        PreservedTurns = 2,
        MaxSummaryChars = 5000,
        SummarizationModel = PrimaryModel.Id,
        SummarizationProvider = PrimaryModel.Provider,
        TimeoutSeconds = timeoutSeconds
    };

    private static LlmSessionCompactor CreateCompactor(
        GatewayAuthManager? authManager,
        params (LlmModel Model, Func<SimpleStreamOptions?, LlmStream> StreamFactory)[] streams)
    {
        var providers = new ApiProviderRegistry();
        var models = new ModelRegistry();

        foreach (var (model, streamFactory) in streams)
        {
            models.Register(model.Provider, model);
            var provider = new Mock<IApiProvider>();
            provider.SetupGet(item => item.Api).Returns(model.Api);
            provider.Setup(item => item.StreamSimple(
                    It.IsAny<LlmModel>(), It.IsAny<Context>(), It.IsAny<SimpleStreamOptions?>()))
                .Returns((LlmModel _, Context _, SimpleStreamOptions? options) => streamFactory(options));
            providers.Register(provider.Object);
        }

        return new LlmSessionCompactor(
            new LlmClient(providers, models),
            NullLogger<LlmSessionCompactor>.Instance,
            authManager: authManager);
    }

    private static LlmStream CancellationAwarePendingStream(
        SimpleStreamOptions? options,
        Action<CancellationToken> onCancellation)
    {
        options.ShouldNotBeNull();
        var stream = new LlmStream();
        options.CancellationToken.Register(() =>
        {
            onCancellation(options.CancellationToken);
            stream.EndCancelled(options.CancellationToken);
        });
        return stream;
    }

    private static LlmStream SuccessStream(string text)
    {
        var completion = new AssistantMessage(
            Content: [new TextContent(text)], Api: "any", Provider: "any", ModelId: "any",
            Usage: Usage.Empty(), StopReason: StopReason.Stop, ErrorMessage: null, ResponseId: null,
            Timestamp: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var stream = new LlmStream();
        stream.Push(new DoneEvent(StopReason.Stop, completion));
        stream.End(completion);
        return stream;
    }

    private static GatewaySession CreateLargeSession()
    {
        var session = new GatewaySession
        {
            SessionId = SessionId.From("compaction-cancellation-session"),
            AgentId = AgentId.From("compaction-agent")
        };
        session.AddEntries(Enumerable.Range(0, 300).Select(index => new SessionEntry
        {
            Role = index % 2 == 0 ? MessageRole.User : MessageRole.Assistant,
            Content = $"message {index} " + new string('x', 50)
        }));
        return session;
    }

    private static GatewayAuthManager CreateAuthManagerWithToken(string provider, string accessToken)
    {
        var fileSystem = new MockFileSystem();
        var configDirectory = PlatformConfigLoader.GetDefaultConfigDirectory(fileSystem);
        fileSystem.Directory.CreateDirectory(configDirectory);
        fileSystem.File.WriteAllText(
            Path.Combine(configDirectory, "auth.json"),
            $$"""
            {
              "{{provider}}": {
                "type": "apikey",
                "refresh": "unused",
                "access": "{{accessToken}}",
                "expires": 4102444800000,
                "endpoint": "https://timeout.example.com"
              }
            }
            """);

        return new GatewayAuthManager(
            new CancellationStaticOptionsMonitor<PlatformConfig>(new PlatformConfig()),
            NullLogger<GatewayAuthManager>.Instance,
            fileSystem);
    }
}

file sealed class CancellationStaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
{
    public T CurrentValue => value;
    public T Get(string? name) => value;
    public IDisposable? OnChange(Action<T, string?> listener) => null;
}
