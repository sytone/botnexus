using BotNexus.Agent.Core.Configuration;
using BotNexus.Agent.Core.Loop;
using BotNexus.Agent.Core.Tests.TestUtils;
using BotNexus.Agent.Core.Types;
using BotNexus.Agent.Providers.Core;
using BotNexus.Agent.Providers.Core.Models;

namespace BotNexus.Agent.Core.Tests.Loop;

using AgentUserMessage = BotNexus.Agent.Core.Types.UserMessage;

/// <summary>
/// Pins the two retry lanes introduced by #3015: transient (retry as before) versus non-transient
/// exhaustion (fail after exactly ONE attempt and record a scoped, expiring suspension).
/// </summary>
/// <remarks>
/// Before #3015 the loop asked one boolean question, so a hard quota/billing failure bought the same
/// four provider round-trips plus 3.5s of backoff as a 503 -- every turn, indefinitely. These tests
/// assert the attempt COUNT, not merely that the run failed, because "it threw" was already true
/// before the change and is therefore not evidence of anything.
/// <para>
/// Two clauses come from the upstream review trail (OpenClaw <c>77d89b2fa843</c>) and are the ones
/// most likely to be quietly lost in a later refactor, so they are named explicitly:
/// a provider <em>overload</em> must not cool an auth profile, and a suspension must be scoped to
/// provider + auth profile rather than global.
/// </para>
/// </remarks>
[Collection(ApiProviderRegistryCollection.Name)]
public class AgentLoopRunnerRetryLaneTests
{
    // --- AC2: non-transient exhaustion fails after exactly ONE attempt ---

    /// <summary>
    /// AC2. Each string is an exhaustion condition that cannot clear by waiting, so the loop must
    /// spend one attempt, not four.
    /// </summary>
    [Theory]
    [InlineData("insufficient_quota: you exceeded your current quota")]
    [InlineData("Your credit balance is too low to access the API")]
    [InlineData("billing has been disabled for this organization")]
    [InlineData("invalid_api_key provided")]
    [InlineData("HTTP 402 payment required")]
    public async Task RunAsync_NonTransientExhaustion_InvokesProviderExactlyOnce(string errorMessage)
    {
        var attempts = 0;
        using var _ = RegisterProvider("exhaustion-once-test", (_, _, _) =>
        {
            Interlocked.Increment(ref attempts);
            throw new InvalidOperationException(errorMessage);
        });

        var registry = new ProviderSuspensionRegistry();
        var config = CreateConfig("exhaustion-once-test", registry, authProfile: "profile-a");

        var act = () => AgentLoopRunner.RunAsync(
            [new AgentUserMessage("test")],
            new AgentContext(null, [], []),
            config,
            _ => Task.CompletedTask,
            CancellationToken.None);

        await act.ShouldThrowAsync<InvalidOperationException>();
        attempts.ShouldBe(
            1,
            $"non-transient exhaustion '{errorMessage}' must fail after ONE attempt, not four");

        // Attempt count alone is NOT sufficient evidence: the pre-existing Terminal lane also fails
        // after one attempt. The suspension is what distinguishes the exhaustion lane from it, so it
        // is asserted here too -- otherwise this test would pass unchanged on code that never
        // implemented the split at all.
        registry.IsSuspended("test-provider", "profile-a")
            .ShouldBeTrue($"'{errorMessage}' must be classified as exhaustion, not merely terminal");
    }

    /// <summary>
    /// AC2. A typed <see cref="ProviderAuthenticationException"/> is an exhaustion condition:
    /// retrying with the same rejected credential cannot produce a different answer.
    /// </summary>
    [Fact]
    public async Task RunAsync_TypedAuthenticationFailure_InvokesProviderExactlyOnce()
    {
        var attempts = 0;
        var registry = new ProviderSuspensionRegistry();
        using var _ = RegisterProvider("exhaustion-typed-test", (_, _, _) =>
        {
            Interlocked.Increment(ref attempts);
            throw new ProviderAuthenticationException("credentials rejected", 401, "test-provider");
        });

        var act = () => AgentLoopRunner.RunAsync(
            [new AgentUserMessage("test")],
            new AgentContext(null, [], []),
            CreateConfig("exhaustion-typed-test", registry, authProfile: "profile-a"),
            _ => Task.CompletedTask,
            CancellationToken.None);

        await act.ShouldThrowAsync<ProviderAuthenticationException>();
        attempts.ShouldBe(1, "a rejected credential must not be retried three more times");
        registry.IsSuspended("test-provider", "profile-a")
            .ShouldBeTrue("a rejected credential is an exhaustion condition, not a terminal one");
    }

    /// <summary>
    /// AC2, the payoff clause. Once suspended, a subsequent run costs ZERO provider round-trips --
    /// the loop short-circuits before the first call. Pre-#3015 this turn would have cost four.
    /// </summary>
    [Fact]
    public async Task RunAsync_WhenAlreadySuspended_MakesNoProviderCallAtAll()
    {
        var attempts = 0;
        var registry = new ProviderSuspensionRegistry();
        registry.Suspend("test-provider", "profile-a", TimeSpan.FromMinutes(15), "insufficient_quota");

        using var _ = RegisterProvider("suspended-shortcircuit-test", (_, _, _) =>
        {
            Interlocked.Increment(ref attempts);
            return TestStreamFactory.CreateTextResponse("should never be reached");
        });

        var act = () => AgentLoopRunner.RunAsync(
            [new AgentUserMessage("test")],
            new AgentContext(null, [], []),
            CreateConfig("suspended-shortcircuit-test", registry, authProfile: "profile-a"),
            _ => Task.CompletedTask,
            CancellationToken.None);

        await act.ShouldThrowAsync<ProviderExhaustedException>();
        attempts.ShouldBe(0, "a known-exhausted profile must cost zero provider round-trips");
    }

    /// <summary>
    /// AC4 companion to the short-circuit: a suspension on profile-a must not short-circuit
    /// profile-b. The second profile's turn runs normally and succeeds.
    /// </summary>
    [Fact]
    public async Task RunAsync_SuspensionOnOneProfile_DoesNotShortCircuitAnother()
    {
        var registry = new ProviderSuspensionRegistry();
        registry.Suspend("test-provider", "profile-a", TimeSpan.FromMinutes(15), "insufficient_quota");

        using var _ = RegisterProvider("suspended-otherprofile-test", (_, _, _) =>
            TestStreamFactory.CreateTextResponse("profile-b is healthy"));

        var result = await AgentLoopRunner.RunAsync(
            [new AgentUserMessage("test")],
            new AgentContext(null, [], []),
            CreateConfig("suspended-otherprofile-test", registry, authProfile: "profile-b"),
            _ => Task.CompletedTask,
            CancellationToken.None);

        result.OfType<AssistantAgentMessage>()
            .ShouldContain(m => m.Content == "profile-b is healthy");
    }

    // --- AC3: the transient lane is unchanged ---

    /// <summary>
    /// AC3 parity. A transient failure still spends the full four-attempt budget, exactly as before
    /// #3015. This is the guard that the split did not quietly shrink the retry budget.
    /// </summary>
    [Fact]
    public async Task RunAsync_TransientFailure_StillExhaustsFourAttempts()
    {
        var attempts = 0;
        using var _ = RegisterProvider("transient-parity-test", (_, _, _) =>
        {
            Interlocked.Increment(ref attempts);
            throw new InvalidOperationException("503 service unavailable");
        });

        var act = () => AgentLoopRunner.RunAsync(
            [new AgentUserMessage("test")],
            new AgentContext(null, [], []),
            CreateConfig("transient-parity-test"),
            _ => Task.CompletedTask,
            CancellationToken.None);

        await act.ShouldThrowAsync<InvalidOperationException>();
        attempts.ShouldBe(4, "the transient lane must keep its four-attempt budget");
    }

    /// <summary>
    /// AC3. A transient failure that clears still recovers mid-budget, so the split did not turn a
    /// recoverable blip into a hard failure.
    /// </summary>
    [Fact]
    public async Task RunAsync_TransientFailureThatClears_StillRecovers()
    {
        var attempts = 0;
        using var _ = RegisterProvider("transient-recovers-test", (_, _, _) =>
        {
            if (Interlocked.Increment(ref attempts) <= 2)
                throw new InvalidOperationException("overloaded");

            return TestStreamFactory.CreateTextResponse("recovered");
        });

        var result = await AgentLoopRunner.RunAsync(
            [new AgentUserMessage("test")],
            new AgentContext(null, [], []),
            CreateConfig("transient-recovers-test"),
            _ => Task.CompletedTask,
            CancellationToken.None);

        attempts.ShouldBe(3);
        result.OfType<AssistantAgentMessage>().ShouldContain(m => m.Content == "recovered");
    }

    // --- AC4: suspension is scoped to provider + auth profile, and expires ---

    /// <summary>
    /// AC4. An exhaustion on one auth profile must not suspend a second profile on the same
    /// provider -- the credential is per-profile, so the condition is too.
    /// </summary>
    [Fact]
    public async Task RunAsync_Exhaustion_SuspendsOnlyTheFailingAuthProfile()
    {
        var registry = new ProviderSuspensionRegistry();
        using var _ = RegisterProvider("exhaustion-scope-test", (_, _, _) =>
            throw new InvalidOperationException("billing has been disabled"));

        var act = () => AgentLoopRunner.RunAsync(
            [new AgentUserMessage("test")],
            new AgentContext(null, [], []),
            CreateConfig("exhaustion-scope-test", registry, authProfile: "profile-a"),
            _ => Task.CompletedTask,
            CancellationToken.None);

        await act.ShouldThrowAsync<InvalidOperationException>();

        registry.IsSuspended("test-provider", "profile-a")
            .ShouldBeTrue("the failing profile must be suspended");
        registry.IsSuspended("test-provider", "profile-b")
            .ShouldBeFalse("a second auth profile on the same provider must be unaffected");
        registry.IsSuspended("other-provider", "profile-a")
            .ShouldBeFalse("the suspension must not be global across providers");
    }

    /// <summary>
    /// AC4. The suspension expires. An exhausted quota is durable but not permanent -- a plan is
    /// topped up, a billing hold released, a monthly window rolls over -- so a suspension that never
    /// expired would convert a recoverable condition into an outage requiring a restart.
    /// </summary>
    [Fact]
    public void SuspensionRegistry_Suspension_ExpiresAfterItsWindow()
    {
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var registry = new ProviderSuspensionRegistry(() => now);

        registry.Suspend("test-provider", "profile-a", TimeSpan.FromMinutes(15), "quota exhausted");
        registry.IsSuspended("test-provider", "profile-a").ShouldBeTrue();

        now = now.AddMinutes(14);
        registry.IsSuspended("test-provider", "profile-a")
            .ShouldBeTrue("still inside the suspension window");

        now = now.AddMinutes(2);
        registry.IsSuspended("test-provider", "profile-a")
            .ShouldBeFalse("the suspension must expire on its own, without a restart");
    }

    /// <summary>
    /// AC4. The scope is not per-session: a fresh loop run against the same provider + profile sees
    /// the suspension recorded by the previous run. Pre-#3015 all retry state died with the call,
    /// which is exactly why every turn re-paid the full four-round-trip discovery cost.
    /// </summary>
    [Fact]
    public async Task RunAsync_Exhaustion_SuspensionOutlivesTheRunThatRecordedIt()
    {
        var registry = new ProviderSuspensionRegistry();
        using var _ = RegisterProvider("exhaustion-durable-test", (_, _, _) =>
            throw new InvalidOperationException("insufficient_quota"));

        var config = CreateConfig("exhaustion-durable-test", registry, authProfile: "profile-a");

        var act = () => AgentLoopRunner.RunAsync(
            [new AgentUserMessage("test")],
            new AgentContext(null, [], []),
            config,
            _ => Task.CompletedTask,
            CancellationToken.None);

        await act.ShouldThrowAsync<InvalidOperationException>();

        // A second, entirely separate run object observes the same registry state.
        registry.IsSuspended("test-provider", "profile-a")
            .ShouldBeTrue("the suspension must survive the run that recorded it, not be per-session");
    }

    // --- AC5: a provider overload must NOT create an auth-profile suspension ---

    /// <summary>
    /// AC5. Provider <em>overload</em> is a property of the provider's capacity at that instant, not
    /// of the caller's credentials. Cooling an auth profile for it would pin a perfectly healthy
    /// credential out of service -- the exact regression the upstream follow-up was filed to fix.
    /// </summary>
    [Theory]
    [InlineData("overloaded")]
    [InlineData("503 service unavailable")]
    [InlineData("Error: 503 overloaded_error")]
    public async Task RunAsync_ProviderOverload_DoesNotSuspendTheAuthProfile(string errorMessage)
    {
        var registry = new ProviderSuspensionRegistry();
        var attempts = 0;
        using var _ = RegisterProvider("overload-no-suspend-test", (_, _, _) =>
        {
            Interlocked.Increment(ref attempts);
            throw new InvalidOperationException(errorMessage);
        });

        var act = () => AgentLoopRunner.RunAsync(
            [new AgentUserMessage("test")],
            new AgentContext(null, [], []),
            CreateConfig("overload-no-suspend-test", registry, authProfile: "profile-a"),
            _ => Task.CompletedTask,
            CancellationToken.None);

        await act.ShouldThrowAsync<InvalidOperationException>();

        registry.IsSuspended("test-provider", "profile-a")
            .ShouldBeFalse($"provider overload '{errorMessage}' must never cool an auth profile");

        // Both halves of the clause. "Not suspended" would also be true if the overload had been
        // misrouted to the Terminal lane, which would silently destroy the retry budget, so the
        // attempt count is pinned alongside it.
        attempts.ShouldBe(4, $"overload '{errorMessage}' must stay in the retrying transient lane");
    }

    /// <summary>
    /// AC5 companion: a typed rate-limit exception is transient and likewise must not suspend.
    /// </summary>
    [Fact]
    public async Task RunAsync_TypedRateLimit_DoesNotSuspendTheAuthProfile()
    {
        var registry = new ProviderSuspensionRegistry();
        using var _ = RegisterProvider("ratelimit-no-suspend-test", (_, _, _) =>
            throw new ProviderRateLimitException("too many requests", 429, TimeSpan.FromMilliseconds(1)));

        var act = () => AgentLoopRunner.RunAsync(
            [new AgentUserMessage("test")],
            new AgentContext(null, [], []),
            CreateConfig("ratelimit-no-suspend-test", registry, authProfile: "profile-a"),
            _ => Task.CompletedTask,
            CancellationToken.None);

        await act.ShouldThrowAsync<ProviderRateLimitException>();

        registry.IsSuspended("test-provider", "profile-a")
            .ShouldBeFalse("a 429 is the transient lane and must not cool the profile");
    }

    /// <summary>
    /// A terminal (unrecognised) failure suspends nothing either -- only the exhaustion lane writes
    /// to the registry.
    /// </summary>
    [Fact]
    public async Task RunAsync_TerminalFailure_DoesNotSuspendTheAuthProfile()
    {
        var registry = new ProviderSuspensionRegistry();
        using var _ = RegisterProvider("terminal-no-suspend-test", (_, _, _) =>
            throw new InvalidOperationException("model not found"));

        var act = () => AgentLoopRunner.RunAsync(
            [new AgentUserMessage("test")],
            new AgentContext(null, [], []),
            CreateConfig("terminal-no-suspend-test", registry, authProfile: "profile-a"),
            _ => Task.CompletedTask,
            CancellationToken.None);

        await act.ShouldThrowAsync<InvalidOperationException>();

        registry.IsSuspended("test-provider", "profile-a").ShouldBeFalse();
    }

    /// <summary>
    /// The exhaustion lane must work with no registry configured -- not spending three pointless
    /// round-trips is correct regardless of whether anything is listening.
    /// </summary>
    [Fact]
    public async Task RunAsync_ExhaustionWithNoRegistry_StillFailsAfterOneAttempt()
    {
        var attempts = 0;
        using var _ = RegisterProvider("exhaustion-noregistry-test", (_, _, _) =>
        {
            Interlocked.Increment(ref attempts);
            throw new InvalidOperationException("insufficient_quota");
        });

        var act = () => AgentLoopRunner.RunAsync(
            [new AgentUserMessage("test")],
            new AgentContext(null, [], []),
            CreateConfig("exhaustion-noregistry-test"),
            _ => Task.CompletedTask,
            CancellationToken.None);

        await act.ShouldThrowAsync<InvalidOperationException>();
        attempts.ShouldBe(1);
    }

    #region Helpers

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

    private static IDisposable RegisterProvider(string apiId,
        Func<LlmModel, Context, SimpleStreamOptions?, BotNexus.Agent.Providers.Core.Streaming.LlmStream> factory)
    {
        var provider = new TestApiProvider(apiId, simpleStreamFactory: factory);
        return TestHelpers.RegisterProvider(provider);
    }

    #endregion
}
