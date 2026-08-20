using BotNexus.Gateway.Diagnostics;
using Microsoft.Extensions.Logging;

namespace BotNexus.Gateway.Tests.Diagnostics;

/// <summary>
/// #2633: a non-terminating fault (e.g. an unobserved task exception on an otherwise healthy
/// process) must not be logged at <see cref="LogLevel.Critical"/>. Monitoring keyed on [FTL]
/// otherwise fires on a gateway that is still serving. Terminating faults keep Critical.
/// </summary>
public sealed class LastChanceFaultSeverityTests
{
    [Fact]
    public void Emit_NonTerminatingFault_LogsAtErrorNotCritical()
    {
        var logger = new FakeLogger<LastChanceFaultHandler>();
        var handler = new LastChanceFaultHandler(logger);

        handler.Emit("UnobservedTaskException", "System.InvalidOperationException: boom", isTerminating: false, force: true);

        var entry = logger.Entries.ShouldHaveSingleItem();
        entry.Level.ShouldBe(LogLevel.Error);
        entry.Message.ShouldContain("terminating=false");
    }

    [Fact]
    public void Emit_NonTerminatingFault_EmitsNoCriticalRecord()
    {
        var logger = new FakeLogger<LastChanceFaultHandler>();
        var handler = new LastChanceFaultHandler(logger);

        handler.Emit("UnobservedTaskException", "System.InvalidOperationException: boom", isTerminating: false, force: true);

        logger.Entries.ShouldNotContain(e => e.Level == LogLevel.Critical);
    }

    [Fact]
    public void Emit_TerminatingFault_StillLogsAtCritical()
    {
        var logger = new FakeLogger<LastChanceFaultHandler>();
        var handler = new LastChanceFaultHandler(logger);

        handler.Emit("UnhandledException", "System.StackOverflowException: dead", isTerminating: true, force: true);

        var entry = logger.Entries.ShouldHaveSingleItem();
        entry.Level.ShouldBe(LogLevel.Critical);
        entry.Message.ShouldContain("terminating=true");
    }

    /// <summary>
    /// #3382 AC4: #2633's severity policy is unchanged by the marker change. The two policies are
    /// independent - the level keys off <c>isTerminating</c> exactly as before, and dropping the
    /// literal <c>[FTL]</c> token from the non-terminating text must not perturb it in either
    /// direction. Pinned by name so a future edit to one cannot silently move the other.
    /// </summary>
    [Fact]
    public void Emit_SeverityPolicy_IsIndependentOfTheFtlMarkerPolicy()
    {
        var nonTerminating = new FakeLogger<LastChanceFaultHandler>();
        new LastChanceFaultHandler(nonTerminating)
            .Emit("UnobservedTaskException", "boom", isTerminating: false, force: true);

        var terminating = new FakeLogger<LastChanceFaultHandler>();
        new LastChanceFaultHandler(terminating)
            .Emit("UnhandledException", "dead", isTerminating: true, force: true);

        // Severity: unchanged from #2633.
        nonTerminating.Entries.ShouldHaveSingleItem().Level.ShouldBe(LogLevel.Error);
        terminating.Entries.ShouldHaveSingleItem().Level.ShouldBe(LogLevel.Critical);

        // Marker: the #3382 change, asserted on the same records so the pairing is explicit.
        nonTerminating.Entries[0].Message.ShouldNotContain("[FTL]");
        terminating.Entries[0].Message.ShouldContain("[FTL]");
    }

    /// <summary>
    /// #3382: the emitted record for a cancelled-turn unobserved task exception - the exact live-site
    /// shape from the issue evidence - is neither Critical nor <c>[FTL]</c>-marked, while remaining
    /// fully attributable via <c>reason=</c>. This is the end-to-end statement of the reported defect.
    /// </summary>
    [Fact]
    public void Emit_UnobservedTaskExceptionOnHealthyGateway_IsNeitherCriticalNorFtlMarked()
    {
        var logger = new FakeLogger<LastChanceFaultHandler>();
        var handler = new LastChanceFaultHandler(logger);

        handler.Emit(
            "UnobservedTaskException",
            "System.AggregateException: A Task's exception(s) were not observed "
                + "(LLM stream ended without a result: Copilot Responses stream parse failed: The operation was canceled.)",
            isTerminating: false,
            force: true);

        var entry = logger.Entries.ShouldHaveSingleItem();
        entry.Level.ShouldBe(LogLevel.Error);
        entry.Message.ShouldNotContain("[FTL]");
        entry.Message.ShouldContain("reason=UnobservedTaskException");
        entry.Message.ShouldContain("terminating=false");
    }
}
