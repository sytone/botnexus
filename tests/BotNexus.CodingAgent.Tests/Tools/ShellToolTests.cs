using BotNexus.CodingAgent.Tools;
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
            ["command"] = "python -c \"import time; time.sleep(2)\"",
            ["timeout"] = 1
        });

        result.Content[0].Value.Should().Contain("timed out");
        result.Details.Should().BeOfType<ShellTool.ShellToolDetails>();
        result.Details.As<ShellTool.ShellToolDetails>().TimedOut.Should().BeTrue();
    }

    [Fact]
    public async Task ExecuteAsync_WhenOutputExceedsLimit_ReturnsTruncatedTailWithPrefix()
    {
        var result = await _tool.ExecuteAsync("test-call", new Dictionary<string, object?>
        {
            ["command"] = "python -c \"print('a' * 52000 + 'TAIL-MARKER')\""
        });

        result.Content[0].Value.Should().Contain("[Output truncated at 51200 bytes]");
        result.Content[0].Value.Should().NotContain("TAIL-MARKER");
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
}
