using System.IO.Abstractions.TestingHelpers;
using BotNexus.Cli.Commands;
using BotNexus.Cli.Commands.Doctor;
using Shouldly;
using Spectre.Console;

namespace BotNexus.Cli.Tests.Commands;

/// <summary>
/// Tests for the aggregate <c>botnexus doctor</c> suite (issue #2041): the bare command must run
/// every registered check in a deterministic order, keep running after a finding, print a final
/// summary, and return a script-friendly aggregate exit code. A non-interactive IAnsiConsole is
/// injected so the suite never blocks on an interactive prompt (regression guard for #2196).
/// </summary>
[Collection("AnsiConsole")]
public sealed class DoctorAggregateCommandTests : IDisposable
{
    private readonly IAnsiConsole _originalConsole;
    private readonly StringWriter _consoleOutput;

    public DoctorAggregateCommandTests()
    {
        _originalConsole = AnsiConsole.Console;
        _consoleOutput = new StringWriter();
        AnsiConsole.Console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Out = new AnsiConsoleOutput(_consoleOutput),
            Interactive = InteractionSupport.No
        });
    }

    public void Dispose()
    {
        AnsiConsole.Console = _originalConsole;
        _consoleOutput.Dispose();
    }

    private static DoctorCheckContext Context()
        => new("/tmp/does-not-matter/config.json", "/tmp/does-not-matter", Verbose: false);

    /// <summary>A trivial in-memory check so the aggregate runner can be tested in isolation.</summary>
    private sealed class StubCheck(string id, DoctorOutcome outcome) : IDoctorCheck
    {
        public string Id => id;
        public string Title => $"Stub {id}";
        public bool Ran { get; private set; }

        public Task<DoctorCheckResult> RunAsync(DoctorCheckContext context, CancellationToken cancellationToken)
        {
            Ran = true;
            return Task.FromResult(new DoctorCheckResult(outcome, $"{id} says {outcome}", []));
        }
    }

    private sealed class ThrowingCheck(string id) : IDoctorCheck
    {
        public string Id => id;
        public string Title => $"Throwing {id}";
        public Task<DoctorCheckResult> RunAsync(DoctorCheckContext context, CancellationToken cancellationToken)
            => throw new InvalidOperationException("boom");
    }

    [Fact]
    public async Task Aggregate_AllHealthy_ReturnsZero_AndPrintsSection_PerCheck()
    {
        var checks = new IDoctorCheck[]
        {
            new StubCheck("a", DoctorOutcome.Healthy),
            new StubCheck("b", DoctorOutcome.Healthy),
        };

        var exit = await DoctorCommand.RunAggregateAsync(checks, Context(), CancellationToken.None);

        exit.ShouldBe(0);
        var output = _consoleOutput.ToString();
        output.ShouldContain("Stub a");
        output.ShouldContain("Stub b");
        output.ShouldContain("2 healthy");
        output.ShouldContain("0 error");
    }

    [Fact]
    public async Task Aggregate_Warning_ReturnsOne_AndRunsRemainingChecks()
    {
        var warn = new StubCheck("warn", DoctorOutcome.Warning);
        var after = new StubCheck("after", DoctorOutcome.Healthy);

        var exit = await DoctorCommand.RunAggregateAsync([warn, after], Context(), CancellationToken.None);

        exit.ShouldBe(1);
        // A finding must not short-circuit: the later independent check still runs.
        after.Ran.ShouldBeTrue();
        var output = _consoleOutput.ToString();
        output.ShouldContain("1 warning");
    }

    [Fact]
    public async Task Aggregate_Error_ReturnsTwo_AndErrorDominatesWarning()
    {
        var warn = new StubCheck("warn", DoctorOutcome.Warning);
        var err = new StubCheck("err", DoctorOutcome.Error);
        var after = new StubCheck("after", DoctorOutcome.Healthy);

        var exit = await DoctorCommand.RunAggregateAsync([warn, err, after], Context(), CancellationToken.None);

        exit.ShouldBe(2);
        after.Ran.ShouldBeTrue();
        var output = _consoleOutput.ToString();
        output.ShouldContain("1 warning");
        output.ShouldContain("1 error");
    }

    [Fact]
    public async Task Aggregate_ThrowingCheck_IsContained_AndRemainingChecksStillRun()
    {
        var boom = new ThrowingCheck("boom");
        var after = new StubCheck("after", DoctorOutcome.Healthy);

        var exit = await DoctorCommand.RunAggregateAsync([boom, after], Context(), CancellationToken.None);

        // An unexpected throw is contained and surfaced as an error section (exit 2), and the
        // remaining independent check still runs.
        exit.ShouldBe(2);
        after.Ran.ShouldBeTrue();
    }

    [Fact]
    public async Task Aggregate_RegisteredCheck_IsIncludedAutomatically()
    {
        // Extensibility contract: a check added to the registry is included in the aggregate suite
        // without any change to the runner. Assert the default registry surfaces each known check id
        // and that the runner reports one section per registered check.
        var registry = DoctorCheckRegistry.CreateDefault();
        registry.Select(c => c.Id).ShouldContain("config");
        registry.Select(c => c.Id).ShouldContain("locations");
        registry.Select(c => c.Id).ShouldContain("agent-folders");
        registry.Select(c => c.Id).ShouldContain("subagent-workspaces");

        // Prove that adding an arbitrary new check flows through the runner untouched.
        var extra = new StubCheck("future-check", DoctorOutcome.Healthy);
        var withExtra = registry.Append(extra).ToArray();

        var exit = await DoctorCommand.RunAggregateAsync(withExtra, Context(), CancellationToken.None);

        // Registry checks may warn/error against the throwaway context, but the extra check ran and
        // its section is present - demonstrating automatic inclusion.
        extra.Ran.ShouldBeTrue();
        _consoleOutput.ToString().ShouldContain("Stub future-check");
        // Exit code is deterministic (0/1/2) regardless of environment.
        exit.ShouldBeInRange(0, 2);
    }

    [Fact]
    public async Task SubAgentWorkspaceCheck_NoWorkspaces_IsHealthy()
    {
        // Reaper semantics (#1942): an empty/absent workspace root is healthy - nothing to reconcile.
        var fileSystem = new MockFileSystem();
        var check = new SubAgentWorkspaceCheck(fileSystem);

        var result = await check.RunAsync(Context(), CancellationToken.None);

        result.Outcome.ShouldBe(DoctorOutcome.Healthy);
    }

    [Fact]
    public async Task SubAgentWorkspaceCheck_OrphanWorkspace_ReportsWarning()
    {
        // A directory with no persisted session record is an orphan - reclaimable, so the check warns
        // and points the operator at the prune command.
        var fileSystem = new MockFileSystem();
        var root = fileSystem.Path.Combine(
            fileSystem.Path.GetTempPath(),
            SubAgentCommand.SubAgentWorkspaceDirectoryName);
        fileSystem.AddDirectory(fileSystem.Path.Combine(root, "orphan-agent"));

        var check = new SubAgentWorkspaceCheck(fileSystem);
        var result = await check.RunAsync(Context(), CancellationToken.None);

        result.Outcome.ShouldBe(DoctorOutcome.Warning);
        result.Summary.ShouldContain("reclaimable");
    }
}
