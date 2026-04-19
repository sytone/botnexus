using BotNexus.Tools;
using FluentAssertions;
using System.Diagnostics;
using System.Reflection;

namespace BotNexus.CodingAgent.Tests.Tools;

public sealed class ShellToolTests
{
    private readonly ShellTool _tool = new();

    [Fact]
    public async Task ExecuteAsync_RunsSimpleCommand()
    {
        var result = await _tool.ExecuteAsync("test-call", new Dictionary<string, object?>
        {
            ["command"] = "echo hello-shell"
        });

        result.Content.Should().ContainSingle();
        result.Content[0].Value.Should().Contain("hello-shell");
    }

    [Fact]
    public async Task ExecuteAsync_ReportsExitCode()
    {
        var result = await _tool.ExecuteAsync("test-call", new Dictionary<string, object?>
        {
            ["command"] = "exit 7"
        });

        result.Content[0].Value.Should().BeEmpty();
        result.Details.Should().BeOfType<ShellTool.ShellToolDetails>();
        result.Details.As<ShellTool.ShellToolDetails>().IsError.Should().BeTrue();
        result.Details.As<ShellTool.ShellToolDetails>().ExitCode.Should().Be(7);
    }

    [Fact]
    public async Task ExecuteAsync_WhenTimeoutReached_ThrowsTimeoutException()
    {
        var result = await _tool.ExecuteAsync("test-call", new Dictionary<string, object?>
        {
            ["command"] = "sleep 2",
            ["timeout"] = 1
        });

        result.Content[0].Value.Should().Contain("timed out");
        result.Details.Should().BeOfType<ShellTool.ShellToolDetails>();
        result.Details.As<ShellTool.ShellToolDetails>().TimedOut.Should().BeTrue();
    }

    [Fact]
    public async Task ExecuteAsync_WhenCallerCancels_ReturnsCancelledResult()
    {
        using var cancellationSource = new CancellationTokenSource(millisecondsDelay: 200);
        var result = await _tool.ExecuteAsync(
            "test-call",
            new Dictionary<string, object?>
            {
                ["command"] = "sleep 2"
            },
            cancellationSource.Token);

        result.Content[0].Value.Should().Contain("cancelled");
        result.Details.Should().BeOfType<ShellTool.ShellToolDetails>();
        result.Details.As<ShellTool.ShellToolDetails>().ExitCode.Should().Be(-1);
        result.Details.As<ShellTool.ShellToolDetails>().TimedOut.Should().BeFalse();
        result.Details.As<ShellTool.ShellToolDetails>().IsError.Should().BeTrue();
    }

    [Fact]
    public void BuildOutput_WhenOutputExceedsByteLimit_KeepsTailLines()
    {
        var lines = Enumerable.Range(1, 1600)
            .Select(i => (Seq: (long)i, Line: $"line-{i:D4}-{new string('x', 40)}"))
            .ToList();

        var output = InvokeBuildOutput(lines, includeTruncationNotes: true);

        output.Should().Contain("line-1600");
        output.Should().NotContain("line-0001");
    }

    [Fact]
    public void BuildOutput_WhenTruncated_PlacesNoticeOnFirstLine()
    {
        var lines = Enumerable.Range(1, 2500)
            .Select(i => (Seq: (long)i, Line: $"line-{i:D4}"))
            .ToList();

        var output = InvokeBuildOutput(lines, includeTruncationNotes: true);
        var firstLine = output.Split(Environment.NewLine, StringSplitOptions.None)[0];

        firstLine.Should().StartWith("[output truncated — showing last ");
    }

    [Fact]
    public void BuildOutput_WhenWithinLimit_ReturnsUnchangedOutput()
    {
        var lines = new List<(long Seq, string Line)>
        {
            (1, "alpha"),
            (2, "beta"),
            (3, "gamma")
        };

        var output = InvokeBuildOutput(lines, includeTruncationNotes: true);

        output.Should().Be($"alpha{Environment.NewLine}beta{Environment.NewLine}gamma");
    }

    [Fact]
    public async Task ExecuteAsync_WhenNoTimeoutArgument_UsesConfiguredDefaultTimeout()
    {
        var tool = new ShellTool(defaultTimeoutSeconds: 1);
        var result = await tool.ExecuteAsync("test-call", new Dictionary<string, object?>
        {
            ["command"] = "sleep 2"
        });

        result.Details.Should().BeOfType<ShellTool.ShellToolDetails>();
        result.Details.As<ShellTool.ShellToolDetails>().TimedOut.Should().BeTrue();
    }

    [Fact]
    public async Task ExecuteAsync_WhenPerCallTimeoutProvided_OverridesConfiguredTimeout()
    {
        var tool = new ShellTool(defaultTimeoutSeconds: 10);
        var result = await tool.ExecuteAsync("test-call", new Dictionary<string, object?>
        {
            ["command"] = "sleep 2",
            ["timeout"] = 1
        });

        result.Details.Should().BeOfType<ShellTool.ShellToolDetails>();
        result.Details.As<ShellTool.ShellToolDetails>().TimedOut.Should().BeTrue();
    }

    [Fact]
    public async Task ExecuteAsync_WhenConfiguredTimeoutIsNull_DoesNotApplyDefaultTimeout()
    {
        var tool = new ShellTool(defaultTimeoutSeconds: null);
        var result = await tool.ExecuteAsync("test-call", new Dictionary<string, object?>
        {
            ["command"] = "sleep 1; echo done"
        });

        result.Details.Should().BeOfType<ShellTool.ShellToolDetails>();
        result.Details.As<ShellTool.ShellToolDetails>().TimedOut.Should().BeFalse();
        result.Details.As<ShellTool.ShellToolDetails>().ExitCode.Should().Be(0);
        result.Content[0].Value.Should().Contain("done");
    }

    [Fact]
    public async Task ExecuteAsync_WhenCancelled_ReturnsCleanErrorAndKillsProcess()
    {
        var pidFile = Path.Combine(Path.GetTempPath(), $"botnexus-shelltool-pid-{Guid.NewGuid():N}.txt");
        var commandPath = pidFile.Replace("\\", "/", StringComparison.Ordinal);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        var result = await _tool.ExecuteAsync("test-call", new Dictionary<string, object?>
        {
            ["command"] = $"echo $$ > '{commandPath}'; sleep 30"
        }, cts.Token);

        result.Details.Should().BeOfType<ShellTool.ShellToolDetails>();
        result.Details.As<ShellTool.ShellToolDetails>().ExitCode.Should().Be(-1);
        result.Details.As<ShellTool.ShellToolDetails>().IsError.Should().BeTrue();

        if (File.Exists(pidFile))
        {
            var pidText = await File.ReadAllTextAsync(pidFile);
            if (int.TryParse(pidText, out var pid))
            {
                var processIsRunning = true;
                try
                {
                    using var process = Process.GetProcessById(pid);
                    processIsRunning = !process.HasExited;
                }
                catch (ArgumentException)
                {
                    processIsRunning = false;
                }

                processIsRunning.Should().BeFalse();
            }
        }
    }

    [Fact]
    public async Task ExecuteAsync_WhenProcessAlreadyCompleted_BeforeCancellation_ReturnsNormalResult()
    {
        using var cts = new CancellationTokenSource();

        var result = await _tool.ExecuteAsync("test-call", new Dictionary<string, object?>
        {
            ["command"] = "echo done"
        }, cts.Token);
        cts.Cancel();

        result.Details.Should().BeOfType<ShellTool.ShellToolDetails>();
        result.Details.As<ShellTool.ShellToolDetails>().ExitCode.Should().Be(0);
        result.Details.As<ShellTool.ShellToolDetails>().IsError.Should().BeFalse();
        result.Content[0].Value.Should().Contain("done");
    }

    [Fact]
    public void Definition_DescribesBashExecution()
    {
        _tool.Definition.Description.Should().Contain("bash command");
    }

    [Fact]
    public void FindBashExecutable_WhenGitIsInstalledOnWindows_ReturnsPath()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var gitInstalled =
            File.Exists(@"C:\Program Files\Git\bin\bash.exe") ||
            File.Exists(@"C:\Program Files (x86)\Git\bin\bash.exe") ||
            FindOnPath("bash") is not null;
        if (!gitInstalled)
        {
            return;
        }

        var method = typeof(ShellTool).GetMethod("FindBashExecutable", BindingFlags.NonPublic | BindingFlags.Static);
        method.Should().NotBeNull();

        var path = method!.Invoke(null, null) as string;

        path.Should().NotBeNullOrWhiteSpace();
        File.Exists(path).Should().BeTrue();
    }

    [Theory]
    [InlineData(typeof(ShellTool), 51200)]
    [InlineData(typeof(ReadTool), 51200)]
    [InlineData(typeof(ListDirectoryTool), 51200)]
    [InlineData(typeof(GrepTool), 51200)]
    public void MaxOutputBytes_Uses50KiBLimit(Type toolType, int expectedBytes)
    {
        var field = toolType.GetField("MaxOutputBytes", BindingFlags.NonPublic | BindingFlags.Static);

        field.Should().NotBeNull();
        field!.GetRawConstantValue().Should().Be(expectedBytes);
    }

    private static string? FindOnPath(string executable)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "where.exe",
                    Arguments = executable,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            if (!process.Start())
            {
                return null;
            }

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(2000);
            if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(output))
            {
                return null;
            }

            return output.Split([Environment.NewLine], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    private static string InvokeBuildOutput(IReadOnlyList<(long Seq, string Line)> buffer, bool includeTruncationNotes)
    {
        var method = typeof(ShellTool).GetMethod("BuildOutput", BindingFlags.NonPublic | BindingFlags.Static);
        method.Should().NotBeNull();

        return method!.Invoke(null, [buffer, includeTruncationNotes]) as string
            ?? throw new InvalidOperationException("Failed to invoke ShellTool.BuildOutput.");
    }

    [Fact]
    public async Task ExecuteAsync_WithPwshPreference_RunsCommandSuccessfully()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var tool = new ShellTool(shellPreference: ShellPreference.Pwsh);
        var result = await tool.ExecuteAsync("test-call", new Dictionary<string, object?>
        {
            ["command"] = "Write-Output 'hello-pwsh'"
        });

        result.Content.Should().ContainSingle();
        result.Content[0].Value.Should().Contain("hello-pwsh");
        result.Details.As<ShellTool.ShellToolDetails>().ExitCode.Should().Be(0);
    }

    [Fact]
    public void ShellPreference_Pwsh_ChangesToolNameOnWindows()
    {
        var tool = new ShellTool(shellPreference: ShellPreference.Pwsh);

        if (OperatingSystem.IsWindows())
        {
            tool.Name.Should().Be("shell");
            tool.Label.Should().Contain("PowerShell");
        }
        else
        {
            // On non-Windows, pwsh preference is ignored — still bash.
            tool.Name.Should().Be("bash");
        }
    }

    [Fact]
    public void ShellPreference_Auto_KeepsBashName()
    {
        var tool = new ShellTool(shellPreference: ShellPreference.Auto);
        tool.Name.Should().Be("bash");
    }

    [Fact]
    public void ShellPreference_Bash_KeepsBashName()
    {
        var tool = new ShellTool(shellPreference: ShellPreference.Bash);
        tool.Name.Should().Be("bash");
    }
}
