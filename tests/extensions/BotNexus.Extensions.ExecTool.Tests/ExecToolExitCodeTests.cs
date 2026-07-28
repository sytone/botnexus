using System.Diagnostics;
using System.Runtime.InteropServices;
using BotNexus.Extensions.ExecTool;
using Shouldly;

namespace BotNexus.Extensions.ExecTool.Tests;

/// <summary>
/// Covers issue #2394: the exec tool used to collapse every non-<c>exit</c> termination to the
/// sentinel <c>-1</c>, destroying the operating system's real status - including the POSIX
/// <c>128 + signum</c> codes .NET surfaces on Linux for signal deaths. These tests pin the two
/// invariants that fix relies on: a status the OS gave us is preserved verbatim, and the reason a
/// run ended stays a separate field rather than being encoded into the number.
/// </summary>
public class ExecToolExitCodeTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(42)]
    [InlineData(255)]
    public void ResolveExitCode_OrdinaryStatus_PassesThroughUnchanged(int osExitCode)
    {
        ExecTool.ResolveExitCode(osExitCode).ShouldBe(osExitCode);
    }

    [Theory]
    [InlineData(137, 9)]   // 128 + SIGKILL - the OOM killer
    [InlineData(139, 11)]  // 128 + SIGSEGV
    [InlineData(143, 15)]  // 128 + SIGTERM
    [InlineData(130, 2)]   // 128 + SIGINT
    public void ResolveExitCode_SignalDeath_SurvivesAsOneTwentyEightPlusSignum(int osExitCode, int signalNumber)
    {
        var resolved = ExecTool.ResolveExitCode(osExitCode);

        resolved.ShouldBe(128 + signalNumber);
        resolved.ShouldBe(osExitCode);
        resolved.ShouldNotBe(ExecTool.UnknownExitCode);
    }

    [Fact]
    public void ResolveExitCode_NoStatusAvailable_ReportsUnknownSentinel()
    {
        ExecTool.ResolveExitCode(null).ShouldBe(ExecTool.UnknownExitCode);
        ExecTool.UnknownExitCode.ShouldBe(-1);
    }

    /// <summary>
    /// The termination reason must remain a first-class field. Constructing details for each
    /// non-<c>exit</c> reason while carrying a real signal status proves the two are independent:
    /// the reason is readable, and the number was not overwritten by it.
    /// </summary>
    [Theory]
    [InlineData("timeout")]
    [InlineData("no-output-timeout")]
    [InlineData("cancelled")]
    [InlineData("exit")]
    public void ExecToolDetails_NonExitTermination_KeepsReasonAndRealStatusIndependent(string termination)
    {
        const int SigkillStatus = 137;

        var details = new ExecTool.ExecToolDetails(ExecTool.ResolveExitCode(SigkillStatus), termination);

        details.Termination.ShouldBe(termination);
        details.ExitCode.ShouldBe(SigkillStatus);
        details.ExitCode.ShouldNotBe(ExecTool.UnknownExitCode);
    }

    [Fact]
    public void TryGetProcessExitCode_ProcessNeverStarted_ReturnsNullInsteadOfThrowing()
    {
        using var process = new Process();
        process.StartInfo.FileName = "definitely-not-a-real-executable-2394";

        // Reading Process.ExitCode here throws InvalidOperationException; the helper must absorb it.
        var code = ExecTool.TryGetProcessExitCode(process);

        code.ShouldBeNull();
        ExecTool.ResolveExitCode(code).ShouldBe(ExecTool.UnknownExitCode);
    }

    [Fact]
    public void TryGetProcessExitCode_DisposedProcess_ReturnsNullInsteadOfThrowing()
    {
        var process = new Process();
        process.StartInfo.FileName = "definitely-not-a-real-executable-2394";
        process.Dispose();

        ExecTool.TryGetProcessExitCode(process).ShouldBeNull();
    }

    [Fact]
    public async Task TryGetProcessExitCode_ExitedProcess_ReturnsRealOperatingSystemStatus()
    {
        var isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        var startInfo = new ProcessStartInfo
        {
            FileName = isWindows ? "cmd.exe" : "/bin/sh",
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        if (isWindows)
        {
            startInfo.ArgumentList.Add("/c");
            startInfo.ArgumentList.Add("exit 42");
        }
        else
        {
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add("exit 42");
        }

        using var process = Process.Start(startInfo);
        process.ShouldNotBeNull();
        await process!.WaitForExitAsync();

        ExecTool.TryGetProcessExitCode(process).ShouldBe(42);
        ExecTool.ResolveExitCode(ExecTool.TryGetProcessExitCode(process)).ShouldBe(42);
    }
}
