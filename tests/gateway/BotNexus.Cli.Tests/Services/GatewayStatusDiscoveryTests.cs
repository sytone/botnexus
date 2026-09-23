using System.Net;
using BotNexus.Cli.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace BotNexus.Cli.Tests.Services;

public sealed class GatewayStatusDiscoveryTests : IDisposable
{
    private readonly string _home = Path.Combine(Path.GetTempPath(), $"bn-4161-{Guid.NewGuid():N}");

    public GatewayStatusDiscoveryTests() => Directory.CreateDirectory(_home);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_home))
                Directory.Delete(_home, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task GetStatusAsync_DiscoversLiveGatewayWithoutPidFile_AndProbesConfiguredUrl()
    {
        var gatewayDll = Path.Combine(_home, "bin", "BotNexus.Gateway.Api.dll");
        var gateway = new FakeProcessHandle(4242, gatewayDll);
        var handler = new RecordingHttpHandler(HttpStatusCode.OK);
        var manager = CreateManager(handler, gateway);

        var status = await manager.GetStatusAsync(
            _home,
            gatewayDll,
            "http://192.0.2.10:7000/health",
            CancellationToken.None);

        status.State.ShouldBe(GatewayState.Running);
        status.Pid.ShouldBe(4242);
        status.Uptime.ShouldBeNull();
        status.ProbeResult.ShouldBe(GatewayProbeResult.Healthy);
        handler.RequestUri.ShouldBe(new Uri("http://192.0.2.10:7000/health"));
    }

    [Fact]
    public async Task GetStatusAsync_WhenPidIsStale_PreservesPidFile()
    {
        var pidFile = Path.Combine(_home, "gateway.pid");
        await File.WriteAllTextAsync(pidFile, "99999");
        var manager = CreateManager(new RecordingHttpHandler(HttpStatusCode.OK));

        var status = await manager.GetStatusAsync(
            _home,
            gatewayBinaryPath: null,
            healthUrl: "http://localhost:5005/health",
            CancellationToken.None);

        status.State.ShouldBe(GatewayState.NotRunning);
        File.Exists(pidFile).ShouldBeTrue("status is a read-only diagnostic and must not delete stale evidence");
    }

    [Fact]
    public async Task GetStatusAsync_WhenPidFileIsInvalid_PreservesPidFile()
    {
        var pidFile = Path.Combine(_home, "gateway.pid");
        await File.WriteAllTextAsync(pidFile, "not-a-pid-record");
        var manager = CreateManager(new RecordingHttpHandler(HttpStatusCode.OK));

        var status = await manager.GetStatusAsync(
            _home,
            gatewayBinaryPath: null,
            healthUrl: "http://localhost:5005/health",
            CancellationToken.None);

        status.State.ShouldBe(GatewayState.NotRunning);
        File.Exists(pidFile).ShouldBeTrue("status must not mutate an invalid PID file");
    }

    private static GatewayProcessManager CreateManager(
        DelegatingHandler handler,
        params IGatewayProcessHandle[] processes) =>
        new(
            Substitute.For<IHealthChecker>(),
            NullLogger<GatewayProcessManager>.Instance,
            probeClient: new HttpClient(handler),
            processEnumerator: () => processes);

    private sealed class FakeProcessHandle(int id, string executablePath) : IGatewayProcessHandle
    {
        public int Id { get; } = id;
        public string? ExecutablePath { get; } = executablePath;
        public void Kill() => throw new InvalidOperationException("status must not signal processes");
        public bool WaitForExit(int milliseconds) => throw new InvalidOperationException("status must not wait for exit");
    }

    private sealed class RecordingHttpHandler(HttpStatusCode statusCode) : DelegatingHandler
    {
        public Uri? RequestUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(statusCode));
        }
    }
}
