using System.Text.RegularExpressions;
using Shouldly;

namespace BotNexus.Architecture.Tests;

/// <summary>
/// Architecture fitness functions enforcing the PR #501 (Vogen Phase 2) invariant:
/// the Cron module and its triggers no longer accept primitive <c>string</c>
/// identifiers named <c>jobId</c> or <c>runId</c>. They use the typed value
/// objects <c>BotNexus.Domain.Primitives.JobId</c> and
/// <c>BotNexus.Domain.Primitives.RunId</c> instead.
/// </summary>
/// <remarks>
/// Phase 1 of the Vogen rollout (PR #513) typed <c>AgentId</c>,
/// <c>ConversationId</c>, and <c>SessionId</c>. Phase 2 (PR #501) extends the
/// same treatment to scheduled-job identifiers. JSON / REST / SignalR boundaries
/// still take <c>string</c> from the wire (where Vogen runs its validation on
/// <c>From(...)</c>); only public method parameters and field declarations are
/// fenced here.
/// </remarks>
public sealed class JobIdRunIdArchitectureTests
{
    // Cron-domain files that own the wire ↔ typed boundary. They take strings from
    // REST/CLI inputs and convert via JobId.From / RunId.From; the fence would
    // otherwise force them to take the typed value before the conversion has run.
    private static readonly string[] AllowedFiles =
    {
        // REST controller accepts {jobId} as a route segment string and wraps with JobId.From.
        "CronController.cs",
        // CLI commands accept string CLI arguments and wrap them at the boundary.
        "CronCommands.cs",
        // Cron tool accepts JSON-encoded arguments and wraps them at the boundary.
        "CronTool.cs",
    };

    // Folders that must use typed JobId / RunId on every public surface.
    private static readonly string[] ScopedFolders =
    {
        Path.Combine("gateway", "BotNexus.Cron"),
        Path.Combine("gateway", "BotNexus.Gateway.Api", "Triggers"),
    };

    /// <summary>
    /// No public method in the Cron module or its triggers may declare a
    /// parameter or field named <c>jobId</c> / <c>runId</c> of type
    /// <c>string</c>. Use <c>JobId</c> / <c>RunId</c> instead.
    /// </summary>
    [Fact]
    public void NoStringJobIdOrRunId_InCronModule()
    {
        var srcRoot = FindSourceRoot();
        // Matches `string jobId`, `string? jobId`, `string  runId`, etc. — but not
        // `JobId jobId` or `RunId runId` (typed parameters).
        var pattern = new Regex(@"\bstring\??\s+(jobId|runId)\b", RegexOptions.Compiled);

        var offenders = ScopedFolders
            .Select(folder => Path.Combine(srcRoot, folder))
            .Where(Directory.Exists)
            .SelectMany(folder => Directory.EnumerateFiles(folder, "*.cs", SearchOption.AllDirectories))
            .Where(IsProductionSource)
            .Where(path => !AllowedFiles.Contains(Path.GetFileName(path), StringComparer.Ordinal))
            .Where(path => pattern.IsMatch(File.ReadAllText(path)))
            .Select(path => Path.GetRelativePath(srcRoot, path))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToArray();

        offenders.ShouldBeEmpty(
            "Files under src/gateway/BotNexus.Cron/ or src/gateway/BotNexus.Gateway.Api/Triggers/ " +
            "declare a 'string jobId' or 'string runId' parameter / field. PR #501 (Vogen Phase 2) " +
            "introduced the typed JobId and RunId value objects — public API must use them. " +
            "Boundary adapters (REST controllers, CLI commands, tool JSON parsing) are allowlisted " +
            "because they convert the wire string with JobId.From(...) / RunId.From(...).\n" +
            "Offenders:\n  " + string.Join("\n  ", offenders));
    }

    private static bool IsProductionSource(string path)
        => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
        && !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal);

    private static string FindSourceRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "BotNexus.slnx")))
        {
            current = current.Parent;
        }

        current.ShouldNotBeNull("Could not locate repo root from " + AppContext.BaseDirectory);
        var srcRoot = Path.Combine(current.FullName, "src");
        Directory.Exists(srcRoot).ShouldBeTrue("Expected src/ under " + current.FullName);
        return srcRoot;
    }
}
