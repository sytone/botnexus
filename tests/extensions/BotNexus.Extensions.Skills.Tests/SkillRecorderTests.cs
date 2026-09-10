using BotNexus.Domain.Primitives;
using BotNexus.Extensions.Skills.Recording;
using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Skills.Tests;

/// <summary>
/// Projection of persisted session history into the tool calls a run actually made.
/// </summary>
/// <remarks>
/// The two history shapes here are not hypothetical: #78 found both on the same deployment, because
/// a live streaming turn rewrites one row in place while a reloaded or blocking run writes a start
/// row and a result row. A recorder that handled only one of them would report a run of eight steps
/// as sixteen, or lose every error flag.
/// </remarks>
public sealed class SkillRecorderTests
{
    private static SessionEntry ToolRow(
        string toolName,
        string? callId,
        string? args = "{}",
        bool isError = false,
        int minute = 0) => new()
        {
            Role = MessageRole.Tool,
            Content = "result",
            ToolName = toolName,
            ToolCallId = callId,
            ToolArgs = args,
            ToolIsError = isError,
            Timestamp = new DateTimeOffset(2026, 9, 9, 12, minute, 0, TimeSpan.Zero)
        };

    [Fact]
    public void FromHistory_KeepsOnlyToolRows()
    {
        var history = new List<SessionEntry>
        {
            new() { Role = MessageRole.User, Content = "add a film" },
            new() { Role = MessageRole.Assistant, Content = "on it" },
            ToolRow("bash", "call-1"),
            new() { Role = MessageRole.System, Content = "banner" }
        };

        var steps = SkillRecorder.FromHistory(history);

        steps.Count.ShouldBe(1);
        steps[0].ToolName.ShouldBe("bash");
        steps[0].Ordinal.ShouldBe(1);
    }

    [Fact]
    public void FromHistory_CollapsesStartAndResultRowsOfOneCall()
    {
        // The two-row shape: a start row and a result row correlated by tool call id.
        var history = new List<SessionEntry>
        {
            ToolRow("bash", "call-1", args: """{"command":"echo hi"}""", minute: 1),
            ToolRow("bash", "call-1", args: """{"command":"echo hi"}""", minute: 3)
        };

        var steps = SkillRecorder.FromHistory(history);

        steps.Count.ShouldBe(1);
        // The START row's timestamp survives: when the call began, not when it returned.
        steps[0].Timestamp.Minute.ShouldBe(1);
    }

    [Fact]
    public void FromHistory_ReportsAnErrorCarriedOnlyOnTheResultRow()
    {
        // The whole point of merging rather than taking the first row. On the two-row shape the
        // error flag lives on the RESULT row, so "first wins" would report every failure as a pass.
        var history = new List<SessionEntry>
        {
            ToolRow("bash", "call-1", isError: false, minute: 1),
            ToolRow("bash", "call-1", isError: true, minute: 2)
        };

        var steps = SkillRecorder.FromHistory(history);

        steps.Count.ShouldBe(1);
        steps[0].IsError.ShouldBeTrue();
    }

    [Fact]
    public void FromHistory_FillsArgumentsFromAnyRowOfThePair()
    {
        // A start row written before #2906 carries no arguments; the result row may.
        var history = new List<SessionEntry>
        {
            ToolRow("bash", "call-1", args: null, minute: 1),
            ToolRow("bash", "call-1", args: """{"command":"ls"}""", minute: 2)
        };

        var steps = SkillRecorder.FromHistory(history);

        steps.Count.ShouldBe(1);
        steps[0].ArgumentsJson.ShouldBe("""{"command":"ls"}""");
    }

    [Fact]
    public void FromHistory_TreatsUncorrelatableLegacyRowsAsSeparateSteps()
    {
        // Rows with no call id cannot be correlated. An uncorrelatable call still happened, so it
        // is kept rather than dropped — under-reporting a run is worse than over-reporting it.
        var history = new List<SessionEntry>
        {
            ToolRow("bash", callId: null, minute: 1),
            ToolRow("bash", callId: null, minute: 2)
        };

        SkillRecorder.FromHistory(history).Count.ShouldBe(2);
    }

    [Fact]
    public void FromHistory_ExcludesTheSkillMetaTools()
    {
        // Otherwise the last step of every recorded skill is "and then I saved a skill", which on
        // replay saves a skill, which records that it saved a skill.
        var history = new List<SessionEntry>
        {
            ToolRow("bash", "call-1"),
            ToolRow("skill_record", "call-2"),
            ToolRow("skill_manage", "call-3")
        };

        var steps = SkillRecorder.FromHistory(history);

        steps.Count.ShouldBe(1);
        steps[0].ToolName.ShouldBe("bash");
    }

    [Fact]
    public void FromHistory_NumbersStepsContiguouslyAfterExclusions()
    {
        var history = new List<SessionEntry>
        {
            ToolRow("bash", "call-1"),
            ToolRow("skill_manage", "call-2"),
            ToolRow("web_fetch", "call-3")
        };

        var steps = SkillRecorder.FromHistory(history);

        steps.Select(s => s.Ordinal).ShouldBe([1, 2]);
    }

    [Fact]
    public void FromHistory_WithNoToolRows_IsEmptyRatherThanThrowing()
    {
        SkillRecorder.FromHistory([]).ShouldBeEmpty();
    }

    /// <summary>
    /// A trace source whose session is fixed at construction, mirroring production. The absence of
    /// a session argument anywhere in the tool's schema is what stops an agent recording another
    /// agent's run, so the test double must not offer one either.
    /// </summary>
    internal sealed class FakeTraceSource(IReadOnlyList<RecordedStep> steps, string sessionId = "session-1")
        : ISessionTraceSource
    {
        public SessionId SessionId => global::BotNexus.Domain.Primitives.SessionId.From(sessionId);

        public Task<IReadOnlyList<RecordedStep>> GetStepsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(steps);
    }
}
