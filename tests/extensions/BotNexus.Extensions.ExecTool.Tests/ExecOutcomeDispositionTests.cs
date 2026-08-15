using System.IO.Abstractions.TestingHelpers;
using BotNexus.Agent.Core.Types;

namespace BotNexus.Extensions.ExecTool.Tests;

/// <summary>
/// Issue #2726: exec failures must say whether the command provably did not run (retry-safe) or
/// may have executed with no authoritative result (NOT retry-safe). Before this, every failure was
/// a flat error string and the natural recovery - rerun it - double-executed non-idempotent commands.
/// </summary>
[Collection(ExecToolBackgroundRegistryCollection.Name)]
public class ExecOutcomeDispositionTests : IDisposable
{
    private static readonly bool IsWindows = OperatingSystem.IsWindows();

    private readonly ExecTool _tool = new(workingDirectory: null, fileSystem: new MockFileSystem());

    public void Dispose() => ExecTool.ClearBackgroundProcesses();

    /// <summary>
    /// Acceptance criterion 4 (non-vacuity target for criterion 6). Collapsing both dispositions
    /// onto one shared guidance string reddens THIS test by name.
    /// </summary>
    [Fact]
    public void Guidance_ForTheTwoDispositions_DoesNotShareRetryPhrasing()
    {
        var notDispatched = ExecOutcomeGuidance.For(ExecOutcomeDisposition.NotDispatched);
        var outcomeUnknown = ExecOutcomeGuidance.For(ExecOutcomeDisposition.OutcomeUnknown);

        notDispatched.ShouldNotBe(outcomeUnknown);

        // The not-dispatched text asserts safety; it must never carry the may-have-executed caveat.
        notDispatched.ShouldContain("did not run");
        notDispatched.ShouldContain("safe to retry");
        notDispatched.ShouldNotContain("may have executed");
        notDispatched.ShouldNotContain("Do NOT rerun");

        // The outcome-unknown text asserts ambiguity; it must never claim retry safety.
        outcomeUnknown.ShouldContain("may have executed");
        outcomeUnknown.ShouldContain("Do NOT rerun");
        outcomeUnknown.ShouldNotContain("safe to retry");
        outcomeUnknown.ShouldNotContain("did not run");
    }

    [Fact]
    public void Guidance_ForCompleted_IsEmptyBecauseAnAuthoritativeResultNeedsNoCaveat()
        => ExecOutcomeGuidance.For(ExecOutcomeDisposition.Completed).ShouldBeEmpty();

    [Theory]
    [InlineData("timeout")]
    [InlineData("no-output-timeout")]
    [InlineData("cancelled")]
    public void Classify_TerminationsThatKilledALiveChild_AreOutcomeUnknown(string termination)
        => ExecOutcomeGuidance.Classify(termination).ShouldBe(ExecOutcomeDisposition.OutcomeUnknown);

    [Fact]
    public void Classify_NormalExit_IsCompleted()
        => ExecOutcomeGuidance.Classify("exit").ShouldBe(ExecOutcomeDisposition.Completed);

    /// <summary>
    /// Acceptance criteria 2 and 5: the no-output-timeout kill is classified outcome-unknown AND the
    /// wording is in the TEXT CONTENT the agent actually receives, not merely on the details record.
    /// </summary>
    [Fact]
    public async Task NoOutputTimeoutKill_TellsTheAgentTheCommandMayHaveExecuted()
    {
        string[] command = IsWindows
            ? ["cmd.exe", "/c", "ping -n 30 127.0.0.1 > nul"]
            : ["/bin/bash", "-c", "sleep 30"];

        var result = await _tool.ExecuteAsync("no-output-disposition", BuildArgs(command, noOutputTimeoutMs: 400));

        var details = result.Details as ExecTool.ExecToolDetails;
        details.ShouldNotBeNull();
        details!.Termination.ShouldBe("no-output-timeout");
        details.Disposition.ShouldBe(ExecOutcomeDisposition.OutcomeUnknown);

        var text = GetResultText(result);
        text.ShouldContain("no output for 400ms");
        text.ShouldContain("may have executed");
        text.ShouldContain("Do NOT rerun");
        text.ShouldNotContain("safe to retry");
    }

    /// <summary>
    /// Acceptance criteria 3 and 5: a process that provably never started is not-dispatched, and the
    /// retry-safe wording reaches the agent's text content rather than surfacing as a bare exception.
    /// </summary>
    [Fact]
    public async Task StartFailure_IsReportedAsNotDispatchedInTheAgentVisibleText()
    {
        var result = await _tool.ExecuteAsync(
            "not-dispatched",
            BuildArgs(["nonexistent_command_2726_abc_xyz"]));

        var details = result.Details as ExecTool.ExecToolDetails;
        details.ShouldNotBeNull();
        details!.Disposition.ShouldBe(ExecOutcomeDisposition.NotDispatched);
        details.Termination.ShouldBe("not-dispatched");

        var text = GetResultText(result);
        text.ShouldContain("Failed to start process");
        text.ShouldContain("did not run");
        text.ShouldContain("safe to retry");
        text.ShouldNotContain("may have executed");
    }

    /// <summary>A successful command carries no retry caveat at all - the result is authoritative.</summary>
    [Fact]
    public async Task SuccessfulCommand_CarriesNoDispositionCaveat()
    {
        string[] command = IsWindows
            ? ["cmd.exe", "/c", "echo disposition_ok"]
            : ["/bin/bash", "-c", "echo disposition_ok"];

        var result = await _tool.ExecuteAsync("disposition-ok", BuildArgs(command));

        var details = result.Details as ExecTool.ExecToolDetails;
        details.ShouldNotBeNull();
        details!.Disposition.ShouldBe(ExecOutcomeDisposition.Completed);

        var text = GetResultText(result);
        text.ShouldContain("disposition_ok");
        text.ShouldNotContain("outcome-unknown");
        text.ShouldNotContain("not-dispatched");
    }

    private static IReadOnlyDictionary<string, object?> BuildArgs(
        IReadOnlyList<string> command,
        int? noOutputTimeoutMs = null)
        => new Dictionary<string, object?>
        {
            ["command"] = command,
            ["timeoutMs"] = 30_000,
            ["noOutputTimeoutMs"] = noOutputTimeoutMs,
            ["input"] = (string?)null,
            ["background"] = false,
            ["env"] = (IReadOnlyDictionary<string, string>?)null,
            ["workingDir"] = (string?)null,
        };

    private static string GetResultText(AgentToolResult result)
    {
        result.Content.ShouldNotBeEmpty();
        return result.Content[0].Value;
    }
}
