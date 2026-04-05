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

        result.Content[0].Value.Should().Contain("Exit Code: 7");
    }

    [Fact]
    public async Task ExecuteAsync_WhenTimeoutReached_ThrowsTimeoutException()
    {
        var action = () => _tool.ExecuteAsync("test-call", new Dictionary<string, object?>
        {
            ["command"] = "Start-Sleep -Seconds 2",
            ["timeout"] = 1
        });

        await action.Should().ThrowAsync<TimeoutException>()
            .WithMessage("*timed out*");
    }
}
