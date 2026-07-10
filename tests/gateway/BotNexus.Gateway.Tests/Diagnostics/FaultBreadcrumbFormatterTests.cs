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
