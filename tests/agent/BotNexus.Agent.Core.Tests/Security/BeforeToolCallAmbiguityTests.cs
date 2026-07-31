using System.Collections.Concurrent;
using System.Text.Json;
using BotNexus.Agent.Core.Configuration;
using BotNexus.Agent.Core.Hooks;
using BotNexus.Agent.Core.Loop;
using BotNexus.Agent.Core.Tests.TestUtils;
using BotNexus.Agent.Core.Tools;
using BotNexus.Agent.Core.Types;
using BotNexus.Agent.Providers.Core.Models;
using Moq;

namespace BotNexus.Agent.Core.Tests.Security;

/// <summary>
/// Issue #2476: an automated approval decision that is not an <b>unambiguous allow</b> must fail
/// closed. Before this change <see cref="BeforeToolCallResult"/> could only say "block" or
/// "not block", so an approval provider with no clear verdict - a reviewer quorum that split, a
/// policy engine that returned no opinion, an aggregation race between concurrent reviewers - had
/// no way to express its ambiguity and was silently coerced into ALLOW.
///
/// These tests assert the observable outcome (did the tool body actually run?) rather than the
/// shape of the returned record, so they remain meaningful if the representation changes.
/// </summary>
public sealed class BeforeToolCallAmbiguityTests
{
    /// <summary>
    /// The whole point of the issue: an indeterminate verdict blocks, and the tool never runs.
    /// </summary>
    [Fact]
    [Trait("Category", "Security")]
    public async Task IndeterminateResult_BlocksToolCall_AndToolNeverExecutes()
    {
        var toolInvoked = false;
        var tool = CreateTool("dangerous", _ =>
        {
            toolInvoked = true;
            return Task.FromResult(Ok("executed"));
        });

        var config = TestHelpers.CreateTestConfig(
            beforeToolCall: (_, _) => Task.FromResult<BeforeToolCallResult?>(
                BeforeToolCallResult.Indeterminate()));

        var results = await ExecuteAsync(config, tool, "dangerous", CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(10));

        toolInvoked.ShouldBeFalse();
        results.ShouldHaveSingleItem();
        results[0].IsError.ShouldBeTrue();
    }

    /// <summary>
    /// An indeterminate verdict carrying an explanation surfaces that explanation to the model, so
    /// the block is diagnosable rather than an opaque refusal.
    /// </summary>
    [Fact]
    [Trait("Category", "Security")]
    public async Task IndeterminateResult_SurfacesItsReason()
    {
        var tool = CreateTool("dangerous", _ => Task.FromResult(Ok("executed")));

        var config = TestHelpers.CreateTestConfig(
            beforeToolCall: (_, _) => Task.FromResult<BeforeToolCallResult?>(
                BeforeToolCallResult.Indeterminate("reviewer quorum split")));

        var results = await ExecuteAsync(config, tool, "dangerous", CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(10));

        results[0].IsError.ShouldBeTrue();
        results[0].Result.Content[0].Value.ShouldContain("reviewer quorum split");
    }

    /// <summary>
    /// An indeterminate verdict with no reason still fails closed and says why in general terms.
    /// </summary>
    [Fact]
    [Trait("Category", "Security")]
    public async Task IndeterminateResult_WithoutReason_StillBlocksWithDefaultMessage()
    {
        var toolInvoked = false;
        var tool = CreateTool("dangerous", _ =>
        {
            toolInvoked = true;
            return Task.FromResult(Ok("executed"));
        });

        var config = TestHelpers.CreateTestConfig(
            beforeToolCall: (_, _) => Task.FromResult<BeforeToolCallResult?>(
                BeforeToolCallResult.Indeterminate()));

        var results = await ExecuteAsync(config, tool, "dangerous", CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(10));

        toolInvoked.ShouldBeFalse();
        results[0].Result.Content[0].Value.ShouldContain("not unambiguously approved");
    }

    // ── Non-breaking guarantee for existing callers ──────────────────────────

    /// <summary>
    /// Backwards compatibility: a hook written against today's positional shape must keep working
    /// identically. <c>Block: false</c> is still an unambiguous allow and the tool still runs.
    /// </summary>
    [Fact]
    [Trait("Category", "Security")]
    public async Task LegacyAllowShape_StillAllows()
    {
        var toolInvoked = false;
        var tool = CreateTool("safe", _ =>
        {
            toolInvoked = true;
            return Task.FromResult(Ok("executed"));
        });

        var config = TestHelpers.CreateTestConfig(
            beforeToolCall: (_, _) => Task.FromResult<BeforeToolCallResult?>(
                new BeforeToolCallResult(Block: false)));

        var results = await ExecuteAsync(config, tool, "safe", CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(10));

        toolInvoked.ShouldBeTrue();
        results[0].IsError.ShouldBeFalse();
    }

    /// <summary>Backwards compatibility: a null result (no opinion registered) still allows.</summary>
    [Fact]
    [Trait("Category", "Security")]
    public async Task NullResult_StillAllows()
    {
        var toolInvoked = false;
        var tool = CreateTool("safe", _ =>
        {
            toolInvoked = true;
            return Task.FromResult(Ok("executed"));
        });

        var config = TestHelpers.CreateTestConfig(
            beforeToolCall: (_, _) => Task.FromResult<BeforeToolCallResult?>(null));

        var results = await ExecuteAsync(config, tool, "safe", CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(10));

        toolInvoked.ShouldBeTrue();
        results[0].IsError.ShouldBeFalse();
    }

    /// <summary>Backwards compatibility: <c>Block: true</c> still blocks with its own reason.</summary>
    [Fact]
    [Trait("Category", "Security")]
    public async Task LegacyBlockShape_StillBlocksWithItsOwnReason()
    {
        var toolInvoked = false;
        var tool = CreateTool("dangerous", _ =>
        {
            toolInvoked = true;
            return Task.FromResult(Ok("executed"));
        });

        var config = TestHelpers.CreateTestConfig(
            beforeToolCall: (_, _) => Task.FromResult<BeforeToolCallResult?>(
                new BeforeToolCallResult(Block: true, Reason: "denied by policy")));

        var results = await ExecuteAsync(config, tool, "dangerous", CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(10));

        toolInvoked.ShouldBeFalse();
        results[0].IsError.ShouldBeTrue();
        results[0].Result.Content[0].Value.ShouldContain("denied by policy");
    }

    /// <summary>
    /// The record's own predicate must agree with the executor: only <c>Block=false</c> plus a
    /// determinate verdict is an allow.
    /// </summary>
    [Theory]
    [InlineData(false, false, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [Trait("Category", "Security")]
    public void IsUnambiguousAllow_OnlyWhenNotBlockedAndDeterminate(bool block, bool indeterminate, bool expected)
    {
        var result = new BeforeToolCallResult(block) { IsIndeterminate = indeterminate };
        result.IsUnambiguousAllow.ShouldBe(expected);
    }

    // ── Concurrency stress ───────────────────────────────────────────────────

    /// <summary>
    /// Issue #2476 concurrency clause. The ambiguity that motivated this issue was found under
    /// <b>concurrent reviewers</b>: two reviewers race, their verdicts are aggregated, and the
    /// aggregate is only meaningful when they agree. A single-threaded test cannot discharge that.
    ///
    /// Here every one of many parallel tool calls consults two genuinely concurrent reviewers whose
    /// verdicts are decided by a real race. The hook aggregates them: unanimous allow -> allow,
    /// any deny -> block, disagreement -> indeterminate. The safety invariant asserted is exact and
    /// race-count-independent: <b>the tool body executed exactly as many times as there were
    /// unanimous allows, and never once for a split or denied verdict</b>. If ambiguity ever
    /// coerced to allow, the executed count would exceed the unanimous-allow count.
    /// </summary>
    [Fact]
    [Trait("Category", "Security")]
    public async Task ConcurrentReviewers_SplitVerdictsNeverExecuteTheTool()
    {
        const int iterations = 256;

        var executed = 0;
        var unanimousAllows = 0;
        var splitVerdicts = 0;
        var denials = 0;

        var tool = CreateTool("dangerous", _ =>
        {
            Interlocked.Increment(ref executed);
            return Task.FromResult(Ok("executed"));
        });

        var outcomes = new ConcurrentBag<(bool IsError, bool Allowed)>();

        var config = TestHelpers.CreateTestConfig(
            beforeToolCall: async (_, ct) =>
            {
                // Two reviewers running genuinely concurrently. Their verdicts are driven by a
                // barrier-released race so the split/agree distribution is decided at runtime,
                // not baked into the test.
                using var gate = new SemaphoreSlim(0, 2);
                var flag = 0;

                async Task<bool> ReviewAsync(int seed)
                {
                    await Task.Yield();
                    gate.Release();
                    await Task.Delay(seed % 2, ct).ConfigureAwait(false);
                    // Each reviewer flips a shared cell; who wins the race decides the verdict.
                    var observed = Interlocked.Exchange(ref flag, seed);
                    return (observed + seed) % 2 == 0;
                }

                var a = Task.Run(() => ReviewAsync(Environment.CurrentManagedThreadId), ct);
                var b = Task.Run(() => ReviewAsync(Environment.TickCount), ct);
                var verdicts = await Task.WhenAll(a, b).ConfigureAwait(false);

                if (verdicts[0] && verdicts[1])
                {
                    Interlocked.Increment(ref unanimousAllows);
                    return new BeforeToolCallResult(Block: false);
                }

                if (!verdicts[0] && !verdicts[1])
                {
                    Interlocked.Increment(ref denials);
                    return new BeforeToolCallResult(Block: true, Reason: "both reviewers denied");
                }

                Interlocked.Increment(ref splitVerdicts);
                return BeforeToolCallResult.Indeterminate("reviewers disagreed");
            },
            beforeToolCallTimeout: TimeSpan.FromSeconds(30));

        await Parallel.ForEachAsync(
            Enumerable.Range(0, iterations),
            new ParallelOptions { MaxDegreeOfParallelism = 32 },
            async (_, ct) =>
            {
                var results = await ExecuteAsync(config, tool, "dangerous", ct)
                    .WaitAsync(TimeSpan.FromSeconds(30), ct);
                outcomes.Add((results[0].IsError, !results[0].IsError));
            });

        outcomes.Count.ShouldBe(iterations);
        (unanimousAllows + splitVerdicts + denials).ShouldBe(iterations);

        // The safety invariant. Execution count is bounded above by unanimous allows: an
        // indeterminate or denied verdict can never have run the tool.
        executed.ShouldBe(unanimousAllows);
        outcomes.Count(o => o.Allowed).ShouldBe(unanimousAllows);
        outcomes.Count(o => o.IsError).ShouldBe(splitVerdicts + denials);
    }

    /// <summary>
    /// A degenerate but decisive companion to the stress test: when every one of many concurrent
    /// reviewers is indeterminate, the tool must execute exactly zero times. This pins the failure
    /// mode directly, independent of how the race above happens to distribute on a given machine
    /// (which could, in principle, produce zero splits and leave the invariant vacuously true).
    /// </summary>
    [Fact]
    [Trait("Category", "Security")]
    public async Task ConcurrentIndeterminateReviewers_NeverExecuteTheTool()
    {
        const int iterations = 256;

        var executed = 0;
        var tool = CreateTool("dangerous", _ =>
        {
            Interlocked.Increment(ref executed);
            return Task.FromResult(Ok("executed"));
        });

        var errors = 0;

        var config = TestHelpers.CreateTestConfig(
            beforeToolCall: async (_, ct) =>
            {
                await Task.Yield();
                await Task.Delay(1, ct).ConfigureAwait(false);
                return BeforeToolCallResult.Indeterminate("no quorum");
            },
            beforeToolCallTimeout: TimeSpan.FromSeconds(30));

        await Parallel.ForEachAsync(
            Enumerable.Range(0, iterations),
            new ParallelOptions { MaxDegreeOfParallelism = 32 },
            async (_, ct) =>
            {
                var results = await ExecuteAsync(config, tool, "dangerous", ct)
                    .WaitAsync(TimeSpan.FromSeconds(30), ct);
                results[0].IsError.ShouldBeTrue();
                Interlocked.Increment(ref errors);
            });

        executed.ShouldBe(0);
        errors.ShouldBe(iterations);
    }

    private static Task<IReadOnlyList<ToolResultAgentMessage>> ExecuteAsync(
        AgentLoopConfig config,
        IAgentTool tool,
        string toolName,
        CancellationToken cancellationToken)
    {
        var context = new AgentContext(null, [], [tool]);
        var assistant = new AssistantAgentMessage(
            string.Empty,
            [new ToolCallContent("tc-1", toolName, new Dictionary<string, object?>())],
            StopReason.ToolUse);

        return ToolExecutor.ExecuteAsync(context, assistant, config, _ => Task.CompletedTask, cancellationToken);
    }

    private static AgentToolResult Ok(string text) =>
        new([new AgentToolContent(AgentToolContentType.Text, text)]);

    private static IAgentTool CreateTool(string name, Func<CancellationToken, Task<AgentToolResult>> execute)
    {
        var mock = new Mock<IAgentTool>();
        mock.Setup(t => t.Name).Returns(name);
        mock.Setup(t => t.Label).Returns(name);
        mock.Setup(t => t.Definition).Returns(
            new Tool(name, name, JsonDocument.Parse("""{"type":"object"}""").RootElement.Clone()));
        mock.Setup(t => t.PrepareArgumentsAsync(It.IsAny<IReadOnlyDictionary<string, object?>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyDictionary<string, object?> args, CancellationToken _) => args);
        mock.Setup(t => t.ExecuteAsync(
                It.IsAny<string>(),
                It.IsAny<IReadOnlyDictionary<string, object?>>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<AgentToolUpdateCallback?>()))
            .Returns((string _, IReadOnlyDictionary<string, object?> _, CancellationToken ct, AgentToolUpdateCallback? _) => execute(ct));
        return mock.Object;
    }
}
