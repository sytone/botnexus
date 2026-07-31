using System.Text.Json;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Services;
using BotNexus.Gateway.Tools;

namespace BotNexus.Gateway.Tests.Tools;

/// <summary>
/// Regression coverage for issue #2415, row <c>ask_user.timeout_seconds</c>. Streaming tool-call
/// parsing boxes a JSON number as a CLR <see cref="double"/> (and integers as <see cref="long"/>),
/// so a payload that satisfies the published schema was rejected with
/// <c>Argument 'timeout_seconds' must be an integer.</c> - a message asserting a requirement the
/// payload already met, which sends the model into blind retries.
/// </summary>
public sealed class AskUserToolArgumentCoercionTests
{
    /// <summary>
    /// Every boxing shape a provider can legitimately deliver for a schema-valid integer must be
    /// accepted. <c>300d</c> is the exact shape reported in #2415.
    /// </summary>
    public static TheoryData<object> AcceptedTimeoutShapes() => new()
    {
        300,
        300L,
        300d,
        300.0m,
        (short)300,
        "300",
        JsonDocument.Parse("300").RootElement.Clone(),
        JsonDocument.Parse("300.0").RootElement.Clone(),
        JsonDocument.Parse("\"300\"").RootElement.Clone(),
    };

    [Theory]
    [MemberData(nameof(AcceptedTimeoutShapes))]
    public async Task PrepareArgumentsAsync_AcceptsEveryBoxingShapeOfAValidTimeout(object timeout)
    {
        var tool = CreateTool();

        var prepared = await tool.PrepareArgumentsAsync(new Dictionary<string, object?>
        {
            ["prompt"] = "Which environment should I deploy to?",
            ["timeout_seconds"] = timeout,
        });

        prepared.ShouldNotBeNull();
    }

    /// <summary>
    /// A <see cref="long"/> outside <see cref="int"/> range must NOT be silently truncated by an
    /// unchecked cast (the pre-#2415 code did exactly that, turning a bogus value into a plausible
    /// one). Rejection is the correct behaviour.
    /// </summary>
    [Theory]
    [InlineData(long.MaxValue)]
    [InlineData(long.MinValue)]
    public async Task PrepareArgumentsAsync_RejectsOutOfRangeLongRatherThanTruncating(long timeout)
    {
        var tool = CreateTool();

        var act = () => tool.PrepareArgumentsAsync(new Dictionary<string, object?>
        {
            ["prompt"] = "How long?",
            ["timeout_seconds"] = timeout,
        });

        await act.ShouldThrowAsync<ArgumentException>();
    }

    /// <summary>
    /// A non-integral number is genuinely invalid for an <c>"type": "integer"</c> property and must
    /// still be rejected - widening the coercion must not degrade into "anything goes".
    /// </summary>
    [Theory]
    [InlineData(300.5)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public async Task PrepareArgumentsAsync_StillRejectsNonIntegralNumber(double timeout)
    {
        var tool = CreateTool();

        var act = () => tool.PrepareArgumentsAsync(new Dictionary<string, object?>
        {
            ["prompt"] = "How long?",
            ["timeout_seconds"] = timeout,
        });

        await act.ShouldThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task PrepareArgumentsAsync_StillRejectsNonNumericValue()
    {
        var tool = CreateTool();

        var act = () => tool.PrepareArgumentsAsync(new Dictionary<string, object?>
        {
            ["prompt"] = "How long?",
            ["timeout_seconds"] = new List<string> { "300" },
        });

        await act.ShouldThrowAsync<ArgumentException>();
    }

    /// <summary>
    /// #2415's core complaint: the rejection message asserted a requirement without saying what was
    /// actually received, so the model could not tell what to change. A rejection must name the
    /// received shape AND the expected shape.
    /// </summary>
    [Fact]
    public async Task PrepareArgumentsAsync_RejectionMessageStatesReceivedAndExpectedShape()
    {
        var tool = CreateTool();

        var act = () => tool.PrepareArgumentsAsync(new Dictionary<string, object?>
        {
            ["prompt"] = "How long?",
            ["timeout_seconds"] = 300.5,
        });

        var ex = await act.ShouldThrowAsync<ArgumentException>();

        ex.Message.ShouldContain("timeout_seconds");
        ex.Message.ShouldContain("received", Case.Insensitive);
        ex.Message.ShouldContain("300.5");
        ex.Message.ShouldContain("expected", Case.Insensitive);
    }

    private static AskUserTool CreateTool()
        => new(
            new AskUserResponseRegistry(),
            AgentId.From("agent-a"),
            SessionId.From("session-1"),
            ConversationId.From("conversation-1"));
}
