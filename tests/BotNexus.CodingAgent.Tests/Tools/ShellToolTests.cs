using BotNexus.CodingAgent.Tools;
using FluentAssertions;

namespace BotNexus.CodingAgent.Tests.Tools;

public sealed class ShellToolTests
{
    private readonly ShellTool _tool = new();

    [Fact]
    public async Task ExecuteAsync_RunsSimpleCommand()
    {
        var result = await _tool.ExecuteAsync("test-call", new Dictionary<string, object?>
        {
            ["command"] = "Write-Output hello-shell"
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
            ["command"] = "Start-Sleep -Seconds 2",
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
            ["command"] = "$payload = ('a' * 52000) + 'TAIL-MARKER'; Write-Output $payload"
        });

        result.Content[0].Value.Should().Contain("[Output truncated at 50000 bytes]");
        result.Content[0].Value.Should().NotContain("TAIL-MARKER");
    }
}
