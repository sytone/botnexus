using BotNexus.Gateway.Diagnostics;

namespace BotNexus.Gateway.Tests.Diagnostics;

public sealed class FaultBreadcrumbFormatterTests
{
    [Fact]
    public void Format_HappyPath_IncludesReasonAndSnapshotFields()
    {
        var breadcrumb = new FaultBreadcrumb
        {
            Reason = "UnhandledException",
            Detail = "System.StackOverflowException: stack overflow",
            ExitCode = 134,
            ActiveAgentCount = 5,
            ActiveSessionCount = 12,
            ThreadCount = 88,
            WorkingSetBytes = 1_073_741_824,
            IsTerminating = true
        };

        var line = FaultBreadcrumbFormatter.Format(breadcrumb);

        line.ShouldStartWith("[FTL]");
        line.ShouldContain("reason=UnhandledException");
        line.ShouldContain("exitCode=134");
        line.ShouldContain("agents=5");
        line.ShouldContain("sessions=12");
        line.ShouldContain("threads=88");
        line.ShouldContain("terminating=true");
        // 1 GiB working set rendered human-readable.
        line.ShouldContain("ws=1.0 GB");
        line.ShouldContain("System.StackOverflowException");
    }

    [Fact]
    public void Format_SadPath_MissingOptionalFields_RendersUnknownPlaceholders()
    {
        var breadcrumb = new FaultBreadcrumb
        {
            Reason = "ProcessExit",
            Detail = null,
            ExitCode = null,
            ActiveAgentCount = null,
            ActiveSessionCount = null,
            ThreadCount = 4,
            WorkingSetBytes = 0,
            IsTerminating = false
        };

        var line = FaultBreadcrumbFormatter.Format(breadcrumb);

        line.ShouldContain("reason=ProcessExit");
        line.ShouldContain("exitCode=unknown");
        line.ShouldContain("agents=unknown");
        line.ShouldContain("sessions=unknown");
        line.ShouldContain("detail=<none>");
        line.ShouldContain("terminating=false");
    }

    [Fact]
    public void Format_NullBreadcrumb_Throws()
    {
        Should.Throw<ArgumentNullException>(() => FaultBreadcrumbFormatter.Format(null!));
    }

    /// <summary>
    /// #3382 AC3 (negative direction): monitoring is keyed on the literal <c>[FTL]</c> text, not on the
    /// log level, so #2633's level downgrade alone left a non-terminating fault still paging as fatal.
    /// A breadcrumb for a process that is not terminating must not carry the marker at all.
    /// </summary>
    [Fact]
    public void Format_NonTerminating_OmitsFtlMarker()
    {
        var breadcrumb = new FaultBreadcrumb
        {
            Reason = "UnobservedTaskException",
            Detail = "System.AggregateException: A Task's exception(s) were not observed",
            ExitCode = 0,
            ActiveAgentCount = 23,
            ActiveSessionCount = null,
            ThreadCount = 64,
            WorkingSetBytes = 2_684_354_560,
            IsTerminating = false
        };

        var line = FaultBreadcrumbFormatter.Format(breadcrumb);

        line.ShouldNotContain("[FTL]");
        line.ShouldStartWith("gateway fault breadcrumb");
        // The record must stay fully greppable by every other field - dropping the marker must not
        // cost the diagnostic its content.
        line.ShouldContain("reason=UnobservedTaskException");
        line.ShouldContain("terminating=false");
        line.ShouldContain("agents=23");
    }

    /// <summary>
    /// #3382 AC3 (positive direction): a genuinely terminating fault still emits the <c>[FTL]</c>
    /// marker. Pinned by name alongside the negative case so the fix cannot degenerate into removing
    /// the marker outright, which would blind the alerting it exists to drive.
    /// </summary>
    [Fact]
    public void Format_Terminating_StillEmitsFtlMarker()
    {
        var breadcrumb = new FaultBreadcrumb
        {
            Reason = "UnhandledException",
            Detail = "System.StackOverflowException: dead",
            ExitCode = 134,
            ActiveAgentCount = 1,
            ActiveSessionCount = 1,
            ThreadCount = 8,
            WorkingSetBytes = 1024,
            IsTerminating = true
        };

        var line = FaultBreadcrumbFormatter.Format(breadcrumb);

        line.ShouldStartWith("[FTL] gateway fault breadcrumb");
        line.ShouldContain("terminating=true");
    }

    [Fact]
    public void Format_CollapsesNewlinesInDetail_ToKeepSingleLineRecord()
    {
        var breadcrumb = new FaultBreadcrumb
        {
            Reason = "UnobservedTaskException",
            Detail = "line1\r\nline2\nline3",
            ExitCode = null,
            ActiveAgentCount = 0,
            ActiveSessionCount = 0,
            ThreadCount = 1,
            WorkingSetBytes = 0,
            IsTerminating = false
        };

        var line = FaultBreadcrumbFormatter.Format(breadcrumb);

        // The FTL record must remain a single log line so log parsers don't split it.
        line.ShouldNotContain("\n");
        line.ShouldContain("line1 line2 line3");
    }
}
