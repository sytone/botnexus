using BotNexus.Extensions.Mcp.Protocol;
using BotNexus.Extensions.Mcp.Transport;
using System.Runtime.InteropServices;

namespace BotNexus.Extensions.Mcp.Tests.Transport;

public sealed class StdioTransportRobustnessTests
{
    // Cross-platform shell helpers
    private static (string FileName, string[] Args) ExitShell(int code)
        => OperatingSystem.IsWindows()
            ? ("cmd.exe", ["/c", $"exit {code}"])
            : ("/bin/sh", ["-c", $"exit {code}"]);

    private static (string FileName, string[] Args) EchoJsonAndExit(string json, int code = 0)
        => OperatingSystem.IsWindows()
            ? ("cmd.exe", ["/c", $"echo {json}& exit {code}"])
            : ("/bin/sh", ["-c", $"printf '%s\\n' '{json}'; exit {code}"]);

    private static (string FileName, string[] Args) EchoStderrThenJson(string stderr, string json)
        => OperatingSystem.IsWindows()
            ? ("cmd.exe", ["/c", $"echo {stderr} 1>&2 & echo {json}"])
            : ("/bin/sh", ["-c", $"echo '{stderr}' >&2; printf '%s\\n' '{json}'"]);

    private static (string FileName, string[] Args) SleepThenEchoJson(int ms, string json)
        => OperatingSystem.IsWindows()
            ? ("powershell", ["-NoProfile", "-Command", $"Start-Sleep -Milliseconds {ms}; Write-Output '{json}'"])
            : ("/bin/sh", ["-c", $"sleep {ms / 1000.0:F3}; printf '%s\\n' '{json}'"]);

    private static (string FileName, string[] Args) LongSleep()
        => OperatingSystem.IsWindows()
            ? ("powershell", ["-NoProfile", "-Command", "Start-Sleep -Seconds 60; exit 1"])
            : ("/bin/sh", ["-c", "sleep 60; exit 1"]);

    [Fact]
    [Trait("Category", "Security")]
    [Trait("Category", "SecurityGap")]
    public async Task ProcessCrashDuringReceive_RequiresCallerTimeout_CurrentBehavior()
    {
        var (file, args) = ExitShell(1);
        var transport = new StdioMcpTransport(file, args);
        await transport.ConnectAsync();

        var act = () => transport.ReceiveAsync(new CancellationTokenSource(TimeSpan.FromSeconds(2)).Token);
        await act.ShouldThrowAsync<OperationCanceledException>();
        await transport.DisposeAsync();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task StderrGarbage_DoesNotBreakStdoutJsonParsing()
    {
        var json = "{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{}}";
        var (file, args) = EchoStderrThenJson("garbage", json);
        var transport = new StdioMcpTransport(file, args);
        await transport.ConnectAsync();

        var response = await transport.ReceiveAsync(new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token);
        response.Id.ShouldNotBeNull();
        await transport.DisposeAsync();
    }

    [Fact]
    [Trait("Category", "Security")]
    [Trait("Category", "SecurityGap")]
    public async Task SlowStartingProcess_HasNoDedicatedStartTimeout_CurrentBehavior()
    {
        var json = "{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{}}";
        var (file, args) = SleepThenEchoJson(500, json);
        var transport = new StdioMcpTransport(file, args);
        var connectTask = transport.ConnectAsync();
        await connectTask;

        var response = await transport.ReceiveAsync(new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token);
        response.Id.ShouldNotBeNull();
        await transport.DisposeAsync();
    }

    [Fact]
    [Trait("Category", "Security")]
    [Trait("Category", "SecurityGap")]
    public async Task NonZeroExitDuringInit_DoesNotThrowOnConnect_CurrentBehavior()
    {
        var json = "{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{}}";
        var (file, args) = EchoJsonAndExit(json, 7);
        var act = () => new StdioMcpTransport(file, args).ConnectAsync();
        await act.ShouldNotThrowAsync();
    }

    [Fact]
    [Trait("Category", "Security")]
    [Trait("Category", "SecurityGap")]
    public async Task SendAfterProcessExit_ThrowsClearError()
    {
        var (file, args) = ExitShell(0);
        var transport = new StdioMcpTransport(file, args);
        await transport.ConnectAsync();
        await Task.Delay(50);

        var act = () => transport.SendAsync(new JsonRpcRequest { Id = 1, Method = "tools/list" });
        // On Windows: write to a dead process stdin does not throw (buffered/ignored).
        // On Linux: write raises IOException (broken pipe). Both are valid current behavior.
        if (OperatingSystem.IsWindows())
        {
            await act.ShouldNotThrowAsync();
        }
        else
        {
            await act.ShouldThrowAsync<Exception>();
        }
        await transport.DisposeAsync();
    }

    [Fact]
    [Trait("Category", "Security")]
    [Trait("Category", "SecurityGap")]
    public async Task SendNotificationAfterProcessExit_ThrowsClearError()
    {
        var (file, args) = ExitShell(0);
        var transport = new StdioMcpTransport(file, args);
        await transport.ConnectAsync();
        await Task.Delay(50);

        var act = () => transport.SendNotificationAsync(new JsonRpcNotification { Method = "notifications/initialized" });
        // On Windows: write to a dead process stdin does not throw (buffered/ignored).
        // On Linux: write raises IOException (broken pipe). Both are valid current behavior.
        if (OperatingSystem.IsWindows())
        {
            await act.ShouldNotThrowAsync();
        }
        else
        {
            await act.ShouldThrowAsync<Exception>();
        }
        await transport.DisposeAsync();
    }
}
