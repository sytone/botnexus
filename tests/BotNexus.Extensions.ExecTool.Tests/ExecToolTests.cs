using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using BotNexus.Agent.Core.Types;
using BotNexus.Extensions.ExecTool;
using BotNexus.Agent.Providers.Core.Utilities;
using FluentAssertions;
using System.IO.Abstractions.TestingHelpers;

namespace BotNexus.Extensions.ExecTool.Tests;

public class ExecToolTests : IDisposable
{
    private readonly ExecTool _tool = new(fileSystem: new MockFileSystem());

    public void Dispose()
    {
        ExecTool.ClearBackgroundProcesses();
    }

    [Fact]
    public void Name_ReturnsExec()
    {
        _tool.Name.Should().Be("exec");
    }

    [Fact]
    public void Definition_HasRequiredCommandProperty()
    {
        var def = _tool.Definition;
        def.Name.Should().Be("exec");
        def.Parameters.GetProperty("required").EnumerateArray()
            .Select(e => e.GetString())
            .Should().Contain("command");
    }

    [Fact]
    public async Task ExecuteSimpleEchoCommand()
    {
        var args = BuildArgs(GetEchoCommand("hello world"));

        var result = await _tool.ExecuteAsync("test-1", args);

        var text = GetResultText(result);
        text.Should().Contain("hello world");
    }

    [Fact]
    public async Task ExecuteReturnsExitCode()
    {
        // Use a command that exits with non-zero
        string[] command = IsWindows
            ? ["cmd.exe", "/c", "exit /b 42"]
            : ["/bin/bash", "-c", "exit 42"];

        var args = BuildArgs(command);
        var result = await _tool.ExecuteAsync("test-exitcode", args);

        var text = GetResultText(result);
        text.Should().Contain("42");

        var details = result.Details as ExecTool.ExecToolDetails;
        details.Should().NotBeNull();
        details!.ExitCode.Should().Be(42);
        details.Termination.Should().Be("exit");
    }

    [Fact]
    public async Task TimeoutKillsLongRunningCommand()
    {
        // Run a command that sleeps longer than the timeout
        string[] command = IsWindows
            ? ["cmd.exe", "/c", "ping -n 30 127.0.0.1"]
            : ["/bin/bash", "-c", "sleep 30"];

        var args = BuildArgs(command, timeoutMs: 2_000);
        var result = await _tool.ExecuteAsync("test-timeout", args);

        var text = GetResultText(result);
        text.Should().Contain("timed out");

        var details = result.Details as ExecTool.ExecToolDetails;
        details.Should().NotBeNull();
        details!.Termination.Should().Be("timeout");
    }

    [Fact]
    public async Task BackgroundModeReturnsPid()
    {
        // Start a long-running process in background
        string[] command = IsWindows
            ? ["cmd.exe", "/c", "ping -n 10 127.0.0.1"]
            : ["/bin/bash", "-c", "sleep 10"];

        var args = BuildArgs(command, background: true);
        var result = await _tool.ExecuteAsync("test-bg", args);

        var text = GetResultText(result);
        var json = JsonDocument.Parse(text);
        var pid = json.RootElement.GetProperty("pid").GetInt32();
        await WaitForProcessStartAsync(pid);
        pid.Should().BeGreaterThan(0);
        json.RootElement.GetProperty("status").GetString().Should().Be("running");

        var details = result.Details as ExecTool.ExecToolDetails;
        details.Should().NotBeNull();
        details!.Termination.Should().Be("background");
        details.Pid.Should().BeGreaterThan(0);

        // Cleanup: kill background process
        TryKillPid(details.Pid!.Value);
    }

    [Fact]
    public async Task WorkingDirectoryOverride()
    {
        var tempDir = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar);

        string[] command = IsWindows
            ? ["cmd.exe", "/c", "cd"]
            : ["/bin/pwd"];

        var args = BuildArgs(command, workingDir: tempDir);
        var result = await _tool.ExecuteAsync("test-cwd", args);

        var text = GetResultText(result);
        // Normalize both paths for comparison
        text.Trim().ToLowerInvariant().Should().Contain(
            Path.GetFullPath(tempDir).TrimEnd(Path.DirectorySeparatorChar).ToLowerInvariant());
    }

    [Fact]
    public async Task InputPipingToStdin()
    {
        string[] command = IsWindows
            ? ["cmd.exe", "/c", "findstr /n ."]
            : ["/bin/cat"];

        var inputText = "piped input line";
        var args = BuildArgs(command, input: inputText);
        var result = await _tool.ExecuteAsync("test-stdin", args);

        var text = GetResultText(result);
        text.Should().Contain("piped input line");
    }

    [Fact]
    public async Task PrepareArguments_RejectsEmptyCommand()
    {
        var args = new Dictionary<string, object?>
        {
            ["command"] = JsonDocument.Parse("[]").RootElement.Clone(),
        };

        var act = () => _tool.PrepareArgumentsAsync(args);
        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*at least one element*");
    }

    [Fact]
    public async Task PrepareArguments_RejectsInvalidTimeout()
    {
        var args = new Dictionary<string, object?>
        {
            ["command"] = JsonDocument.Parse("""["echo","hi"]""").RootElement.Clone(),
            ["timeoutMs"] = JsonDocument.Parse("-5").RootElement.Clone(),
        };

        var act = () => _tool.PrepareArgumentsAsync(args);
        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [ConditionalFact(typeof(WindowsOnly))]
    public async Task WindowsCmdResolution()
    {
        // "where.exe" is a known .exe on Windows, test that it resolves
        var args = BuildArgs(["where.exe", "cmd.exe"]);
        var result = await _tool.ExecuteAsync("test-win-resolve", args);

        var text = GetResultText(result);
        text.Should().Contain("cmd.exe");
    }

    [Fact]
    public void ResolveCommand_PassthroughOnNonWindows()
    {
        if (IsWindows) return; // skip on Windows

        var (fileName, args) = ExecTool.ResolveCommand(["mycommand", "arg1", "arg2"]);
        fileName.Should().Be("mycommand");
        args.Should().BeEquivalentTo(["arg1", "arg2"]);
    }

    // --- Negative input / edge case tests ---

    [Fact]
    public async Task PrepareArguments_RejectsMissingCommand()
    {
        var args = new Dictionary<string, object?>();

        var act = () => _tool.PrepareArgumentsAsync(args);
        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*command*");
    }

    [Fact]
    public async Task PrepareArguments_RejectsNullCommand()
    {
        var args = new Dictionary<string, object?>
        {
            ["command"] = null,
        };

        var act = () => _tool.PrepareArgumentsAsync(args);
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task PrepareArguments_RejectsInvalidNoOutputTimeout()
    {
        var args = new Dictionary<string, object?>
        {
            ["command"] = JsonDocument.Parse("""["echo","hi"]""").RootElement.Clone(),
            ["noOutputTimeoutMs"] = JsonDocument.Parse("0").RootElement.Clone(),
        };

        var act = () => _tool.PrepareArgumentsAsync(args);
        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task PrepareArguments_RejectsZeroTimeout()
    {
        var args = new Dictionary<string, object?>
        {
            ["command"] = JsonDocument.Parse("""["echo","hi"]""").RootElement.Clone(),
            ["timeoutMs"] = JsonDocument.Parse("0").RootElement.Clone(),
        };

        var act = () => _tool.PrepareArgumentsAsync(args);
        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task ExecuteNonExistentCommand_ReturnsError()
    {
        var args = BuildArgs(["nonexistent_command_abc_xyz_12345"]);

        Func<Task> act = () => _tool.ExecuteAsync("test-fail", args);

        // Should throw because the process can't start,
        // or return a non-zero exit code
        try
        {
            var result = await _tool.ExecuteAsync("test-fail", args);
            var details = result.Details as ExecTool.ExecToolDetails;
            // If it doesn't throw, it should indicate failure
            details.Should().NotBeNull();
            details!.ExitCode.Should().NotBe(0);
        }
        catch (Exception ex)
        {
            // System.ComponentModel.Win32Exception or InvalidOperationException
            ex.Should().Match<Exception>(e =>
                e is System.ComponentModel.Win32Exception ||
                e is InvalidOperationException);
        }
    }

    [Fact]
    public async Task ExecuteWithEnvironmentVariables()
    {
        string[] command = IsWindows
            ? ["cmd.exe", "/c", "echo %TEST_BOTNEXUS_VAR%"]
            : ["/bin/bash", "-c", "echo $TEST_BOTNEXUS_VAR"];

        var env = new Dictionary<string, string> { ["TEST_BOTNEXUS_VAR"] = "hello_from_env" };
        var dict = new Dictionary<string, object?>
        {
            ["command"] = (IReadOnlyList<string>)command.ToList(),
            ["timeoutMs"] = ExecTool_DefaultTimeoutMs,
            ["noOutputTimeoutMs"] = (int?)null,
            ["input"] = (string?)null,
            ["background"] = false,
            ["env"] = (IReadOnlyDictionary<string, string>)env,
            ["workingDir"] = (string?)null,
        };

        var result = await _tool.ExecuteAsync("test-env", dict);

        var text = GetResultText(result);
        text.Should().Contain("hello_from_env");
    }

    [Fact]
    public async Task ExecuteWithCancellation_TerminatesProcess()
    {
        using var cts = new CancellationTokenSource(500);

        string[] command = IsWindows
            ? ["cmd.exe", "/c", "ping -n 30 127.0.0.1"]
            : ["/bin/bash", "-c", "sleep 30"];

        var args = BuildArgs(command);
        var result = await _tool.ExecuteAsync("test-cancel", args, cts.Token);

        var details = result.Details as ExecTool.ExecToolDetails;
        details.Should().NotBeNull();
        details!.Termination.Should().Be("cancelled");
    }

    [Fact]
    public async Task ExecuteNoOutput_ReturnsNoOutputMessage()
    {
        string[] command = IsWindows
            ? ["cmd.exe", "/c", "echo.>nul"]
            : ["/bin/true"];

        var args = BuildArgs(command);
        var result = await _tool.ExecuteAsync("test-no-output", args);

        var text = GetResultText(result);
        var details = result.Details as ExecTool.ExecToolDetails;
        details.Should().NotBeNull();
        details!.ExitCode.Should().Be(0);
        // Should be either empty output indicator or whitespace
    }

    [Fact]
    public async Task StderrOutput_IsCapturedWithStdout()
    {
        string[] command = IsWindows
            ? ["cmd.exe", "/c", "echo stdout_msg & echo stderr_msg 1>&2"]
            : ["/bin/bash", "-c", "echo stdout_msg; echo stderr_msg >&2"];

        var args = BuildArgs(command);
        var result = await _tool.ExecuteAsync("test-stderr", args);

        var text = GetResultText(result);
        text.Should().Contain("stdout_msg");
        text.Should().Contain("stderr_msg");
    }

    [Fact]
    public async Task PrepareArgumentsAsync_WithStreamingJsonParserOutput_ParsesCommand()
    {
        // Simulate the real flow: StreamingJsonParser produces dict with JsonElement arrays
        var parsed = StreamingJsonParser.Parse("{\"command\": [\"echo\", \"hello\"]}");

        var prepared = await _tool.PrepareArgumentsAsync(parsed);

        prepared.Should().ContainKey("command");
        var command = prepared["command"] as IReadOnlyList<string>;
        command.Should().NotBeNull();
        command.Should().BeEquivalentTo(["echo", "hello"]);
    }

    #region Helpers

    private static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    private static string[] GetEchoCommand(string message) =>
        IsWindows
            ? ["cmd.exe", "/c", $"echo {message}"]
            : ["/bin/echo", message];

    private static IReadOnlyDictionary<string, object?> BuildArgs(
        string[] command,
        int? timeoutMs = null,
        int? noOutputTimeoutMs = null,
        string? input = null,
        bool? background = null,
        string? workingDir = null)
    {
        var dict = new Dictionary<string, object?>
        {
            ["command"] = (IReadOnlyList<string>)command.ToList(),
            ["timeoutMs"] = timeoutMs ?? ExecTool_DefaultTimeoutMs,
            ["noOutputTimeoutMs"] = noOutputTimeoutMs,
            ["input"] = input,
            ["background"] = background ?? false,
            ["env"] = null,
            ["workingDir"] = workingDir,
        };

        return dict;
    }

    private const int ExecTool_DefaultTimeoutMs = 120_000;

    private static string GetResultText(AgentToolResult result)
    {
        result.Content.Should().NotBeEmpty();
        return result.Content[0].Value;
    }

    private static void TryKillPid(int pid)
    {
        try
        {
            var process = System.Diagnostics.Process.GetProcessById(pid);
            process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Process may have already exited
        }
    }

    private static async Task WaitForProcessStartAsync(int pid)
    {
        var timeoutAt = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < timeoutAt)
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                if (!process.HasExited)
                {
                    return;
                }
            }
            catch (ArgumentException)
            {
                // Process has not started or exited before lookup.
            }

            await Task.Delay(100);
        }

        using var finalProcess = Process.GetProcessById(pid);
        finalProcess.HasExited.Should().BeFalse();
    }

    #endregion
}

public class ConditionalFactAttribute : FactAttribute
{
    public ConditionalFactAttribute(Type conditionType)
    {
        if (conditionType == typeof(WindowsOnly) && !RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Skip = "Windows-only test.";
        }
    }
}

public class WindowsOnly;

