using System.Globalization;
using System.Text;

namespace BotNexus.Gateway.Diagnostics;

/// <summary>
/// Immutable point-in-time snapshot of a fatal fault, captured by the last-chance fault
/// handler just before the process dies. Optional fields are nullable because a hard exit
/// may occur when the DI container, agent registry, or session store is not reachable.
/// </summary>
public sealed class FaultBreadcrumb
{
    /// <summary>Short machine-readable fault reason (e.g. <c>UnhandledException</c>, <c>ProcessExit</c>).</summary>
    public required string Reason { get; init; }

    /// <summary>Human-readable detail such as the exception type and message. May be null.</summary>
    public string? Detail { get; init; }

    /// <summary>Process exit code if known at capture time.</summary>
    public int? ExitCode { get; init; }

    /// <summary>Count of registered/active agents if the registry was reachable.</summary>
    public int? ActiveAgentCount { get; init; }

    /// <summary>Count of active sessions if the session store was reachable.</summary>
    public int? ActiveSessionCount { get; init; }

    /// <summary>Managed thread count at capture time.</summary>
    public required int ThreadCount { get; init; }

    /// <summary>Process working set (RSS) in bytes at capture time.</summary>
    public required long WorkingSetBytes { get; init; }

    /// <summary>Whether the runtime reported the process is terminating.</summary>
    public required bool IsTerminating { get; init; }
}

/// <summary>
/// Renders a <see cref="FaultBreadcrumb"/> into a single-line, grep-friendly <c>[FTL]</c> record.
/// Kept as a pure function so the exact breadcrumb wording is unit-testable without provoking a
/// real crash, and so the last-chance handler stays a thin adapter that only gathers values.
/// </summary>
public static class FaultBreadcrumbFormatter
{
    /// <summary>
    /// Formats the fatal breadcrumb as a single log line. Newlines in <see cref="FaultBreadcrumb.Detail"/>
    /// are collapsed to spaces so the record cannot be split across lines by log processors.
    /// </summary>
    /// <param name="breadcrumb">The captured fault snapshot.</param>
    /// <returns>A single-line <c>[FTL] ...</c> diagnostic record.</returns>
    public static string Format(FaultBreadcrumb breadcrumb)
    {
        ArgumentNullException.ThrowIfNull(breadcrumb);

        var sb = new StringBuilder();
        sb.Append("[FTL] gateway fault breadcrumb");
        sb.Append(" reason=").Append(breadcrumb.Reason);
        sb.Append(" exitCode=").Append(Render(breadcrumb.ExitCode));
        sb.Append(" agents=").Append(Render(breadcrumb.ActiveAgentCount));
        sb.Append(" sessions=").Append(Render(breadcrumb.ActiveSessionCount));
        sb.Append(" threads=").Append(breadcrumb.ThreadCount.ToString(CultureInfo.InvariantCulture));
        sb.Append(" ws=").Append(FormatBytes(breadcrumb.WorkingSetBytes));
        sb.Append(" terminating=").Append(breadcrumb.IsTerminating ? "true" : "false");
        sb.Append(" detail=").Append(CollapseWhitespace(breadcrumb.Detail));
        return sb.ToString();
    }

    private static string Render(int? value) =>
        value?.ToString(CultureInfo.InvariantCulture) ?? "unknown";

    private static string CollapseWhitespace(string? detail)
    {
        if (string.IsNullOrWhiteSpace(detail))
            return "<none>";

        // Collapse CR/LF/tab runs to single spaces to keep the record on one line.
        var chars = detail.Select(c => c is '\r' or '\n' or '\t' ? ' ' : c).ToArray();
        var collapsed = new string(chars);
        while (collapsed.Contains("  ", StringComparison.Ordinal))
            collapsed = collapsed.Replace("  ", " ", StringComparison.Ordinal);
        return collapsed.Trim();
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes <= 0)
            return "0 B";

        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return string.Create(CultureInfo.InvariantCulture, $"{value:0.0} {units[unit]}");
    }
}
