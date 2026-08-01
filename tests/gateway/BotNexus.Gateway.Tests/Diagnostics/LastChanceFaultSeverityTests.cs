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
}
