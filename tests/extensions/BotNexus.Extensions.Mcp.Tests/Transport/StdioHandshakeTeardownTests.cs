using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using BotNexus.Extensions.Mcp.Protocol;
using BotNexus.Extensions.Mcp.Transport;

namespace BotNexus.Extensions.Mcp.Tests.Transport;

/// <summary>
/// Covers issue #2723: the spawned MCP stdio server process must be terminated when the
/// <c>initialize</c> handshake fails, rather than leaking until <c>DisposeAsync</c> happens to run.
/// </summary>
public sealed class StdioHandshakeTeardownTests
{
    private const string InitializeErrorJson =
        "{\"jsonrpc\":\"2.0\",\"id\":1,\"error\":{\"code\":-32001,\"message\":\"handshake rejected by stub\"}}";

    /// <summary>
    /// A stub stdio server that accepts the connection, answers the first request with a JSON-RPC
    /// error, and then stays alive indefinitely - i.e. it will only go away if it is killed.
    /// </summary>
    private static (string FileName, string[] Args) ErrorThenLinger(string json)
        => OperatingSystem.IsWindows()
            ? ("powershell", [
                "-NoProfile",
                "-Command",
                $"$null = [Console]::In.ReadLine(); Write-Output '{json}'; Start-Sleep -Seconds 120"
            ])
            : ("/bin/sh", ["-c", $"read _line; printf '%s\\n' '{json}'; sleep 120"]);

    /// <summary>
    /// A stub child that ignores stdin close entirely: it never reads stdin and simply sleeps.
    /// Closing its stdin cannot make it exit, so only a force-kill after the grace window ends it.
    /// </summary>
    private static (string FileName, string[] Args) IgnoresStdinClose()
        => OperatingSystem.IsWindows()
            ? ("powershell", ["-NoProfile", "-Command", "Start-Sleep -Seconds 120"])
            : ("/bin/sh", ["-c", "sleep 120"]);

    private static Process GetProcess(StdioMcpTransport transport)
    {
        var field = typeof(StdioMcpTransport).GetField("_process", BindingFlags.NonPublic | BindingFlags.Instance);
        field.ShouldNotBeNull();
        return field.GetValue(transport).ShouldBeOfType<Process>();
    }

    private static async Task AssertExitedAsync(Process process, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            // fall through to the assertion so the failure message is meaningful
        }

        process.HasExited.ShouldBeTrue(
            "the spawned MCP server process should have been terminated, not leaked");
    }

    // AC2: handshake returns a JSON-RPC error -> child process must not be left running.
    [Fact]
    [Trait("Category", "Security")]
    public async Task InitializeAsync_JsonRpcErrorResponse_KillsSpawnedProcess()
    {
        var (file, args) = ErrorThenLinger(InitializeErrorJson);
        var transport = new StdioMcpTransport(file, args);
        var client = new McpClient(transport, "stub");

        try
        {
            var act = () => client.InitializeAsync(new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token);
            var ex = await act.ShouldThrowAsync<McpException>();

            // AC4: the original handshake exception type and message are preserved.
            ex.Message.ShouldContain("handshake rejected by stub");

            await AssertExitedAsync(GetProcess(transport), TimeSpan.FromSeconds(15));
        }
        finally
        {
            await transport.DisposeAsync();
        }
    }

    // AC1: cancellation after ConnectAsync succeeded -> child process must not be left running.
    [Fact]
    [Trait("Category", "Security")]
    public async Task InitializeAsync_Cancelled_KillsSpawnedProcess()
    {
        var (file, args) = IgnoresStdinClose();
        var transport = new StdioMcpTransport(file, args);
        var client = new McpClient(transport, "stub");

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
            var act = () => client.InitializeAsync(cts.Token);
            await act.ShouldThrowAsync<OperationCanceledException>();

            await AssertExitedAsync(GetProcess(transport), TimeSpan.FromSeconds(15));
        }
        finally
        {
            await transport.DisposeAsync();
        }
    }

    // AC3: a child that ignores stdin close is force-killed after the bounded grace window.
    [Fact]
    [Trait("Category", "Security")]
    public async Task TerminateProcessAsync_ChildIgnoringStdinClose_IsForceKilledAfterGrace()
    {
        var (file, args) = IgnoresStdinClose();
        var transport = new StdioMcpTransport(file, args);
        try
        {
            await transport.ConnectAsync();
            var process = GetProcess(transport);
            process.HasExited.ShouldBeFalse("the stub child should be running before teardown");

            var sw = Stopwatch.StartNew();
            await transport.TerminateProcessAsync(TimeSpan.FromMilliseconds(300));
            sw.Stop();

            process.HasExited.ShouldBeTrue("a child that ignores stdin close must be force-killed");
            sw.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(15),
                "termination must be bounded by the grace window, not block indefinitely");
        }
        finally
        {
            await transport.DisposeAsync();
        }
    }

    // AC3 (via the public seam): DisconnectAsync also force-kills a child that ignores stdin close.
    [Fact]
    [Trait("Category", "Security")]
    public async Task DisconnectAsync_ChildIgnoringStdinClose_IsForceKilled()
    {
        var (file, args) = IgnoresStdinClose();
        var transport = new StdioMcpTransport(file, args);
        try
        {
            await transport.ConnectAsync();
            var process = GetProcess(transport);
            process.HasExited.ShouldBeFalse();

            await transport.DisconnectAsync();

            process.HasExited.ShouldBeTrue();
        }
        finally
        {
            await transport.DisposeAsync();
        }
    }

    // AC4: a teardown that itself throws must never mask the original handshake exception.
    [Fact]
    [Trait("Category", "Security")]
    public async Task InitializeAsync_TeardownThrows_PreservesOriginalHandshakeException()
    {
        var transport = new ThrowingTeardownTransport();
        transport.EnqueueError(-32001, "handshake rejected by stub");
        var client = new McpClient(transport, "stub");

        var act = () => client.InitializeAsync(new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token);
        var ex = await act.ShouldThrowAsync<McpException>();

        ex.Message.ShouldContain("handshake rejected by stub");
        transport.DisconnectAttempts.ShouldBeGreaterThan(0, "teardown must still be attempted");
    }

    // AC6: a successful initialize must not be torn down.
    [Fact]
    public async Task InitializeAsync_Success_DoesNotTearDownTransport()
    {
        var transport = new ThrowingTeardownTransport();
        transport.EnqueueSuccess();
        var client = new McpClient(transport, "stub");

        await client.InitializeAsync(new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token);

        transport.DisconnectAttempts.ShouldBe(0);
    }

    /// <summary>Transport whose teardown always throws, used to prove teardown cannot mask errors.</summary>
    private sealed class ThrowingTeardownTransport : IMcpTransport
    {
        private readonly Queue<JsonRpcResponse> _responses = new();

        public int DisconnectAttempts { get; private set; }

        public void EnqueueError(int code, string message)
            => _responses.Enqueue(new JsonRpcResponse
            {
                Id = 1,
                Error = new JsonRpcError { Code = code, Message = message },
            });

        public void EnqueueSuccess()
            => _responses.Enqueue(new JsonRpcResponse
            {
                Id = 1,
                Result = JsonSerializer.SerializeToElement(new Dictionary<string, string>()),
            });

        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task SendAsync(JsonRpcRequest message, CancellationToken ct = default) => Task.CompletedTask;

        public Task SendNotificationAsync(JsonRpcNotification message, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<JsonRpcResponse> ReceiveAsync(CancellationToken ct = default)
            => Task.FromResult(_responses.Dequeue());

        public Task DisconnectAsync(CancellationToken ct = default)
        {
            DisconnectAttempts++;
            throw new InvalidOperationException("teardown blew up and must not be seen by the caller");
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
