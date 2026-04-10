using BotNexus.Extensions.Mcp.Protocol;
using BotNexus.Extensions.Mcp.Transport;
using FluentAssertions;

namespace BotNexus.Extensions.Mcp.Tests.Transport;

public sealed class StdioTransportRobustnessTests
{
    [Fact]
    [Trait("Category", "Security")]
    [Trait("Category", "SecurityGap")]
    public async Task ProcessCrashDuringReceive_RequiresCallerTimeout_CurrentBehavior()
    {
        var transport = new StdioMcpTransport("powershell", ["-NoProfile", "-Command", "exit 1"]);
        await transport.ConnectAsync();

        var act = () => transport.ReceiveAsync(new CancellationTokenSource(TimeSpan.FromSeconds(2)).Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
        await transport.DisposeAsync();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task StderrGarbage_DoesNotBreakStdoutJsonParsing()
    {
        var command = "Write-Error 'garbage'; Write-Output '{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{}}'; Start-Sleep -Milliseconds 10";
        var transport = new StdioMcpTransport("powershell", ["-NoProfile", "-Command", command]);
        await transport.ConnectAsync();

        var response = await transport.ReceiveAsync(new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token);
        response.Id.Should().NotBeNull();
        await transport.DisposeAsync();
    }

    [Fact]
    [Trait("Category", "Security")]
    [Trait("Category", "SecurityGap")]
    public async Task SlowStartingProcess_HasNoDedicatedStartTimeout_CurrentBehavior()
    {
        var transport = new StdioMcpTransport("powershell", ["-NoProfile", "-Command", "Start-Sleep -Milliseconds 500; Write-Output '{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{}}'"]);
        var connectTask = transport.ConnectAsync();
        await connectTask;

        var response = await transport.ReceiveAsync(new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token);
        response.Id.Should().NotBeNull();
        await transport.DisposeAsync();
    }

    [Fact]
    [Trait("Category", "Security")]
    [Trait("Category", "SecurityGap")]
    public async Task NonZeroExitDuringInit_DoesNotThrowOnConnect_CurrentBehavior()
    {
        var transport = new StdioMcpTransport("powershell", ["-NoProfile", "-Command", "Write-Output '{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{}}'; exit 7"]);
        var act = () => transport.ConnectAsync();
        await act.Should().NotThrowAsync();
        await transport.DisposeAsync();
    }

    [Fact]
    [Trait("Category", "Security")]
    [Trait("Category", "SecurityGap")]
    public async Task SendAfterProcessExit_ThrowsClearError()
    {
        var transport = new StdioMcpTransport("powershell", ["-NoProfile", "-Command", "exit 0"]);
        await transport.ConnectAsync();
        await Task.Delay(50);

        var act = () => transport.SendAsync(new JsonRpcRequest { Id = 1, Method = "tools/list" });
        await act.Should().NotThrowAsync();
        await transport.DisposeAsync();
    }

    [Fact]
    [Trait("Category", "Security")]
    [Trait("Category", "SecurityGap")]
    public async Task SendNotificationAfterProcessExit_ThrowsClearError()
    {
        var transport = new StdioMcpTransport("powershell", ["-NoProfile", "-Command", "exit 0"]);
        await transport.ConnectAsync();
        await Task.Delay(50);

        var act = () => transport.SendNotificationAsync(new JsonRpcNotification { Method = "notifications/initialized" });
        await act.Should().NotThrowAsync();
        await transport.DisposeAsync();
    }
}
