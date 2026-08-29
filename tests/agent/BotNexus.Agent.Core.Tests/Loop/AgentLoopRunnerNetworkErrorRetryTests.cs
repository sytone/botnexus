using BotNexus.Agent.Core.Configuration;
using BotNexus.Agent.Core.Loop;
using BotNexus.Agent.Core.Tests.TestUtils;
using BotNexus.Agent.Core.Types;
using BotNexus.Agent.Providers.Core;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Streaming;

namespace BotNexus.Agent.Core.Tests.Loop;

using AgentUserMessage = BotNexus.Agent.Core.Types.UserMessage;

/// <summary>
/// End-to-end coverage for #3567: a stream terminated with <c>finish_reason: network_error</c> must
/// engage the existing retry lane instead of ending the run on the first occurrence.
/// </summary>
/// <remarks>
/// The defect was a layering mismatch, not a missing feature: the provider layer classified the
/// condition correctly but expressed it as a returned <see cref="StopReason.Error"/> message, and
/// <c>ExecuteWithRetryAsync</c> retries only inside a <c>catch</c>. These tests assert the ATTEMPT
/// COUNT rather than merely that the run recovered, because "the run ended" was already true before
/// the change and is therefore evidence of nothing.
/// <para>
/// Non-vacuity is carried by two clauses that fail if the change is read as "retry every error":
/// a non-transient finish reason still ends the run after exactly ONE provider call, and an
/// exhausted budget still surfaces a diagnostic naming <c>network_error</c> rather than retrying
/// into silence.
/// </para>
/// </remarks>
[Collection(ApiProviderRegistryCollection.Name)]
public class AgentLoopRunnerNetworkErrorRetryTests
{
    // --- AC2 / AC3: the transient finish reason engages the EXISTING retry lane ---

    /// <summary>
    /// AC2. A <c>network_error</c> stream that clears is retried and the turn succeeds. Before
    /// #3567 the first occurrence ended the run with a terminal message and zero retries.
    /// </summary>
    [Fact]
    public async Task RunAsync_NetworkErrorFinishReasonThatClears_RetriesAndSucceeds()
    {
        var attempts = 0;
        using var _ = RegisterProvider("network-error-clears-test", (_, _, _) =>
            Interlocked.Increment(ref attempts) <= 2
                ? NetworkErrorStream()
                : TestStreamFactory.CreateTextResponse("recovered"));

        var result = await AgentLoopRunner.RunAsync(
            [new AgentUserMessage("test")],
            new AgentContext(null, [], []),
            CreateConfig("network-error-clears-test"),
            _ => Task.CompletedTask,
            CancellationToken.None);

        attempts.ShouldBe(3, "network_error must be retried, not returned as a terminal message");
        result.OfType<AssistantAgentMessage>().ShouldContain(m => m.Content == "recovered");
    }

    /// <summary>
    /// AC3. The retry uses the loop's existing four-attempt budget and backoff path -- no second
    /// retry implementation. A persistent network_error therefore costs exactly four attempts, the
    /// same number a <c>503</c> costs.
    /// </summary>
    [Fact]
    public async Task RunAsync_PersistentNetworkError_SpendsTheFullFourAttemptBudget()
    {
        var attempts = 0;
        using var _ = RegisterProvider("network-error-budget-test", (_, _, _) =>
        {
            Interlocked.Increment(ref attempts);
            return NetworkErrorStream();
        });

        var act = () => AgentLoopRunner.RunAsync(
            [new AgentUserMessage("test")],
            new AgentContext(null, [], []),
            CreateConfig("network-error-budget-test"),
            _ => Task.CompletedTask,
            CancellationToken.None);

        await act.ShouldThrowAsync<ProviderTransientFinishReasonException>();
        attempts.ShouldBe(4, "network_error must share the existing transient budget, not get its own");
    }

    /// <summary>
    /// AC4. An unrecoverable network fault must remain legible: once the budget is spent the run
    /// ends with a diagnostic that names <c>network_error</c>, rather than being retried into
    /// silence or surfacing as an anonymous failure.
    /// </summary>
    [Fact]
    public async Task RunAsync_ExhaustedBudget_EndsWithADiagnosticNamingNetworkError()
    {
        using var _ = RegisterProvider("network-error-diagnostic-test", (_, _, _) => NetworkErrorStream());

        var act = () => AgentLoopRunner.RunAsync(
            [new AgentUserMessage("test")],
            new AgentContext(null, [], []),
            CreateConfig("network-error-diagnostic-test"),
            _ => Task.CompletedTask,
            CancellationToken.None);

        var thrown = await act.ShouldThrowAsync<ProviderTransientFinishReasonException>();

        thrown.FinishReason.ShouldBe("network_error");
        thrown.Message.ShouldContain("network_error");
    }

    /// <summary>
    /// AC3, the seam assertion. A transient finish reason must not cool the auth profile: it is a
    /// transport blip, not an exhausted credential. This is the same clause #3015 pinned for
    /// provider overload, restated for the new entry into the lane.
    /// </summary>
    [Fact]
    public async Task RunAsync_NetworkError_DoesNotSuspendTheAuthProfile()
    {
        var registry = new ProviderSuspensionRegistry();
        using var _ = RegisterProvider("network-error-nosuspend-test", (_, _, _) => NetworkErrorStream());

        var act = () => AgentLoopRunner.RunAsync(
            [new AgentUserMessage("test")],
            new AgentContext(null, [], []),
            CreateConfig("network-error-nosuspend-test", registry, authProfile: "profile-a"),
            _ => Task.CompletedTask,
            CancellationToken.None);

        await act.ShouldThrowAsync<ProviderTransientFinishReasonException>();

        registry.IsSuspended("test-provider", "profile-a")
            .ShouldBeFalse("a transport blip must never cool a healthy credential");
    }

    // --- AC5: non-transient reasons are unchanged ---

    /// <summary>
    /// AC5. The change must not be readable as "retry every error". A terminal
    /// <see cref="StopReason.Error"/> turn -- which is what every other failure-style finish reason
    /// still produces -- ends the run after exactly ONE provider call, with no retry.
    /// </summary>
    [Fact]
    public async Task RunAsync_TerminalErrorStopReason_StillEndsTheRunOnTheFirstOccurrence()
    {
        var attempts = 0;
        using var _ = RegisterProvider("terminal-error-once-test", (_, _, _) =>
        {
            Interlocked.Increment(ref attempts);
            return TestStreamFactory.CreateErrorResponse("Provider finish_reason: some_unknown_reason");
        });

        var result = await AgentLoopRunner.RunAsync(
            [new AgentUserMessage("test")],
            new AgentContext(null, [], []),
            CreateConfig("terminal-error-once-test"),
            _ => Task.CompletedTask,
            CancellationToken.None);

        attempts.ShouldBe(1, "a non-transient failure reason must still end the run on first occurrence");
        result.OfType<AssistantAgentMessage>()
            .ShouldContain(m => m.FinishReason == StopReason.Error);
    }

    #region Helpers

    /// <summary>
    /// A stream shaped exactly as the provider layer now shapes one for a transient finish reason:
    /// faulted with <see cref="ProviderTransientFinishReasonException"/> so the loop's exception-only
    /// retry lane can observe it. Using the real exception type rather than a stand-in is what keeps
    /// these tests coupled to the production seam.
    /// </summary>
    private static LlmStream NetworkErrorStream()
    {
        var stream = new LlmStream();
        stream.EndFaulted(new ProviderTransientFinishReasonException("network_error"));
        return stream;
    }

    private static AgentLoopConfig CreateConfig(
        string apiId,
        IProviderSuspensionRegistry? registry = null,
        string? authProfile = null)
    {
        return new AgentLoopConfig(
            Model: TestHelpers.CreateTestModel(apiId),
            LlmClient: TestHelpers.CreateLlmClient(),
            ConvertToLlm: (messages, _) => Task.FromResult<IReadOnlyList<Message>>(
                messages.OfType<AgentUserMessage>()
                    .Select(m => (Message)new BotNexus.Agent.Providers.Core.Models.UserMessage(
                        new UserMessageContent(m.Content),
                        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()))
                    .ToList()),
            TransformContext: (messages, _) => Task.FromResult(messages),
            GetApiKey: (_, _) => Task.FromResult<string?>(null),
            GetSteeringMessages: null,
            GetFollowUpMessages: null,
            ToolExecutionMode: ToolExecutionMode.Sequential,
            BeforeToolCall: null,
            AfterToolCall: null,
            GenerationSettings: new SimpleStreamOptions(),
            MaxRetryDelayMs: 1, // Fast retries for tests
            SuspensionRegistry: registry,
            AuthProfile: authProfile);
    }

    private static IDisposable RegisterProvider(string apiId, Func<LlmModel, Context, SimpleStreamOptions?, LlmStream> factory)
        => TestHelpers.RegisterProvider(new TestApiProvider(apiId, simpleStreamFactory: factory));

    #endregion
}
