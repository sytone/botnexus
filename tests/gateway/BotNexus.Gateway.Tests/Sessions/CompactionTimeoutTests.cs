using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Sessions;
using BotNexus.Agent.Providers.Core;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Registry;
using BotNexus.Agent.Providers.Core.Streaming;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace BotNexus.Gateway.Tests.Sessions;

public sealed class CompactionTimeoutTests
{
    private static readonly LlmModel TestModel = new(
        Id: "test-model",
        Name: "Test Model",
        Api: "test-api",
        Provider: "test-provider",
        BaseUrl: "https://example.com",
        Reasoning: false,
        Input: ["text"],
        Cost: new ModelCost(0, 0, 0, 0),
        ContextWindow: 32000,
        MaxTokens: 4096);

    [Fact]
    public void CompactionOptions_TimeoutSeconds_DefaultIs90()
    {
        var options = new CompactionOptions();
        options.TimeoutSeconds.ShouldBe(90);
    }

    [Fact]
    public void CompactionOptions_TimeoutSeconds_CanBeCustomized()
    {
        var options = new CompactionOptions { TimeoutSeconds = 30 };
        options.TimeoutSeconds.ShouldBe(30);
    }

    [Fact]
    public async Task CompactAsync_HungLlmCall_TimesOutAndFails()
    {
        // Simulate an LLM call that never completes
        var session = CreateLargeSession(100);
        var compactor = CreateHungCompactor();
        var options = new CompactionOptions
        {
            TimeoutSeconds = 1, // 1 second timeout for test speed
            ContextWindowTokens = 100,
            TokenThresholdRatio = 0.01,
            PreservedTurns = 1,
            SummarizationModel = TestModel.Id
        };

        var result = await compactor.CompactAsync(session, options);

        // Should fail gracefully rather than hang forever
        result.Succeeded.ShouldBeFalse();
    }

    [Fact]
    public async Task CompactAsync_CallerCancellation_ThrowsOperationCanceled()
    {
        var session = CreateLargeSession(100);
        var compactor = CreateHungCompactor();
        var options = new CompactionOptions
        {
            TimeoutSeconds = 300, // Long timeout — cancellation comes from caller
            ContextWindowTokens = 100,
            TokenThresholdRatio = 0.01,
            PreservedTurns = 1,
            SummarizationModel = TestModel.Id
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        // Caller cancellation should propagate as OperationCanceledException
        // (the coordinator layer catches this and returns a failed outcome)
        await Should.ThrowAsync<TaskCanceledException>(
            () => compactor.CompactAsync(session, options, cts.Token));
    }

    [Fact]
    public async Task CompactAsync_NormalCompletion_NotAffectedByTimeout()
    {
        var session = CreateLargeSession(100);
        var compactor = CreateCompactor("Good summary");
        var options = new CompactionOptions
        {
            TimeoutSeconds = 90,
            ContextWindowTokens = 100,
            TokenThresholdRatio = 0.01,
            PreservedTurns = 1,
            SummarizationModel = TestModel.Id
        };

        var result = await compactor.CompactAsync(session, options);

        result.Succeeded.ShouldBeTrue();
        result.Summary.ShouldContain("Good summary");
    }

    [Fact]
    public async Task CompactAsync_TimeoutIncrementsCircuitBreaker()
    {
        var session = CreateLargeSession(100);
        var compactor = CreateHungCompactor();
        var options = new CompactionOptions
        {
            TimeoutSeconds = 1,
            ContextWindowTokens = 100,
            TokenThresholdRatio = 0.01,
            PreservedTurns = 1,
            SummarizationModel = TestModel.Id
        };

        // Each timeout increments the failure counter
        for (var i = 0; i < LlmSessionCompactor.MaxConsecutiveFailures; i++)
        {
            var r = await compactor.CompactAsync(session, options);
            r.Succeeded.ShouldBeFalse();
        }

        // Circuit breaker should now be open
        var result = await compactor.CompactAsync(session, options);
        result.Succeeded.ShouldBeFalse();
        result.EntriesPreserved.ShouldBe(0); // short-circuits without snapshotting
    }

    private static GatewaySession CreateLargeSession(int entryCount)
    {
        var session = new GatewaySession
        {
            SessionId = SessionId.From(Guid.NewGuid().ToString("N")),
            AgentId = AgentId.From("agent")
        };

        var entries = new List<SessionEntry>();
        for (var i = 0; i < entryCount; i++)
        {
            entries.Add(new SessionEntry
            {
                Role = i % 2 == 0 ? "user" : "assistant",
                Content = $"message {i} " + new string('x', 50)
            });
        }

        session.AddEntries(entries);
        return session;
    }

    private static LlmSessionCompactor CreateCompactor(string summary)
    {
        var providers = new ApiProviderRegistry();
        var models = new ModelRegistry();
        models.Register(TestModel.Provider, TestModel);

        var provider = new Mock<IApiProvider>();
        provider.SetupGet(item => item.Api).Returns(TestModel.Api);
        provider.Setup(item => item.StreamSimple(
                It.IsAny<LlmModel>(),
                It.IsAny<Context>(),
                It.IsAny<SimpleStreamOptions?>()))
            .Returns(() => CreateStream(summary));

        providers.Register(provider.Object);

        var llmClient = new LlmClient(providers, models);
        return new LlmSessionCompactor(llmClient, NullLogger<LlmSessionCompactor>.Instance);
    }

    /// <summary>
    /// Creates a compactor that simulates a hung LLM call (never completes).
    /// The stream's GetResultAsync() will never resolve.
    /// </summary>
    private static LlmSessionCompactor CreateHungCompactor()
    {
        var providers = new ApiProviderRegistry();
        var models = new ModelRegistry();
        models.Register(TestModel.Provider, TestModel);

        var provider = new Mock<IApiProvider>();
        provider.SetupGet(item => item.Api).Returns(TestModel.Api);
        provider.Setup(item => item.StreamSimple(
                It.IsAny<LlmModel>(),
                It.IsAny<Context>(),
                It.IsAny<SimpleStreamOptions?>()))
            .Returns(() =>
            {
                // Return a stream that never pushes a result — simulates hung provider
                return new LlmStream();
            });

        providers.Register(provider.Object);

        var llmClient = new LlmClient(providers, models);
        return new LlmSessionCompactor(llmClient, NullLogger<LlmSessionCompactor>.Instance);
    }

    private static LlmStream CreateStream(string summary)
    {
        var stream = new LlmStream();
        var completion = new AssistantMessage(
            Content: [new TextContent(summary)],
            Api: TestModel.Api,
            Provider: TestModel.Provider,
            ModelId: TestModel.Id,
            Usage: Usage.Empty(),
            StopReason: StopReason.Stop,
            ErrorMessage: null,
            ResponseId: null,
            Timestamp: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        stream.Push(new DoneEvent(StopReason.Stop, completion));
        stream.End(completion);
        return stream;
    }
}
