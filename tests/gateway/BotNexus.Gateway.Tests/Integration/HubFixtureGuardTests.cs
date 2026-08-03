using System.Diagnostics;
using BotNexus.Gateway.Tests.Helpers;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;

namespace BotNexus.Gateway.Tests.Integration;

/// <summary>
/// Regression guard for issue #2628: a hung or unreachable SignalR hub fixture must surface as a
/// FAILING TEST naming the fixture and test, not as a cancelled CI job with orphaned dotnet
/// processes. Asserting only that the integration suite "passes" is insufficient — it passed
/// locally throughout the incident — so these tests inject a hang deliberately and assert the
/// harness converts it into a bounded, diagnosable failure.
/// </summary>
[Trait("Category", "Integration")]
public sealed class HubFixtureGuardTests
{
    private const string FixtureName = "SignalRIntegrationTestsFixture";
    private static readonly TimeSpan ShortBound = TimeSpan.FromSeconds(2);

    [Fact]
    public async Task RunGuardedAsync_OperationHangsIgnoringToken_ThrowsWithinBound()
    {
        var stopwatch = Stopwatch.StartNew();

        // A hang that ignores its cancellation token entirely — exactly the CI failure mode.
        var ex = await Should.ThrowAsync<HubFixtureTimeoutException>(async () =>
            await HubFixtureGuard.RunGuardedAsync(
                _ => Task.Delay(Timeout.InfiniteTimeSpan, CancellationToken.None),
                "StartAsync",
                FixtureName,
                nameof(RunGuardedAsync_OperationHangsIgnoringToken_ThrowsWithinBound),
                ShortBound));

        stopwatch.Stop();
        stopwatch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(20));
        ex.FixtureName.ShouldBe(FixtureName);
        ex.OperationName.ShouldBe("StartAsync");
    }

    [Fact]
    public async Task RunGuardedAsync_OperationHangs_MessageNamesFixtureAndTest()
    {
        var ex = await Should.ThrowAsync<HubFixtureTimeoutException>(async () =>
            await HubFixtureGuard.RunGuardedAsync(
                ct => Task.Delay(Timeout.InfiniteTimeSpan, ct),
                "StartAsync",
                FixtureName,
                nameof(RunGuardedAsync_OperationHangs_MessageNamesFixtureAndTest),
                ShortBound));

        ex.Message.ShouldContain(FixtureName);
        ex.Message.ShouldContain(nameof(RunGuardedAsync_OperationHangs_MessageNamesFixtureAndTest));
        ex.Message.ShouldContain("StartAsync");
        ex.Timeout.ShouldBe(ShortBound);
    }

    [Fact]
    public async Task RunGuardedAsync_OperationCompletes_DoesNotThrow()
    {
        var ran = false;

        await HubFixtureGuard.RunGuardedAsync(
            _ =>
            {
                ran = true;
                return Task.CompletedTask;
            },
            "StartAsync",
            FixtureName,
            nameof(RunGuardedAsync_OperationCompletes_DoesNotThrow),
            ShortBound);

        ran.ShouldBeTrue();
    }

    [Fact]
    public async Task RunGuardedAsync_OperationFaults_PropagatesOriginalException()
    {
        var ex = await Should.ThrowAsync<InvalidOperationException>(async () =>
            await HubFixtureGuard.RunGuardedAsync(
                _ => Task.FromException(new InvalidOperationException("hub refused")),
                "StartAsync",
                FixtureName,
                nameof(RunGuardedAsync_OperationFaults_PropagatesOriginalException),
                ShortBound));

        ex.Message.ShouldBe("hub refused");
    }

    [Fact]
    public async Task RunGuardedAsync_CallerCancels_PropagatesCancellationNotTimeout()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(async () =>
            await HubFixtureGuard.RunGuardedAsync(
                ct => Task.FromCanceled(ct),
                "StartAsync",
                FixtureName,
                nameof(RunGuardedAsync_CallerCancels_PropagatesCancellationNotTimeout),
                TimeSpan.FromMinutes(5),
                cts.Token));
    }

    [Fact]
    public async Task StartGuardedAsync_HubUnreachable_FailsTestInsteadOfHanging()
    {
        var stopwatch = Stopwatch.StartNew();

        // A handler that never responds models the CI symptom: the transport neither connects
        // nor errors, and HubConnection.StartAsync never returns.
        await using var connection = new HubConnectionBuilder()
            .WithUrl("http://localhost/hub/gateway", options =>
            {
                options.HttpMessageHandlerFactory = _ => new NeverRespondingHandler();
                options.Transports = HttpTransportType.LongPolling;
            })
            .Build();

        var ex = await Should.ThrowAsync<HubFixtureTimeoutException>(async () =>
            await HubFixtureGuard.StartGuardedAsync(connection, FixtureName, CancellationToken.None, ShortBound));

        stopwatch.Stop();
        stopwatch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(20));
        ex.Message.ShouldContain(FixtureName);
        ex.Message.ShouldContain(nameof(StartGuardedAsync_HubUnreachable_FailsTestInsteadOfHanging));
    }

    [Fact]
    public async Task DisposeGuardedAsync_ConnectionNeverStarted_CompletesWithinBound()
    {
        var connection = new HubConnectionBuilder()
            .WithUrl("http://localhost/hub/gateway", options =>
            {
                options.HttpMessageHandlerFactory = _ => new NeverRespondingHandler();
                options.Transports = HttpTransportType.LongPolling;
            })
            .Build();

        var stopwatch = Stopwatch.StartNew();
        await HubFixtureGuard.DisposeGuardedAsync(connection, FixtureName, ShortBound);
        stopwatch.Stop();

        stopwatch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(20));
    }

    private sealed class NeverRespondingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            throw new UnreachableException();
        }
    }
}
