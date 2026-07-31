using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Api.Controllers;
using BotNexus.Gateway.Channels.Startup;
using BotNexus.Gateway.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace BotNexus.Gateway.Tests;

/// <summary>
/// Clause 4 of #2447: health/status output must distinguish "N adapters configured" from
/// "N adapters started", naming any that failed. Before this, a failed adapter was recorded
/// in-process and logged, but no operator-facing endpoint reported it - the only symptom of a
/// dead channel was silence.
/// </summary>
public sealed class ChannelStartupHealthTests
{
    [Fact]
    public void Health_WhenEveryConfiguredAdapterStarted_ReportsOkWithMatchingCounts()
    {
        var report = new ChannelStartupReport();
        report.Record(
        [
            Outcome("telegram", started: true),
            Outcome("signalr", started: true),
        ]);

        var controller = CreateController(report, "telegram", "signalr");

        var result = controller.Health();

        var ok = result.Result as OkObjectResult;
        ok.ShouldNotBeNull();
        var payload = ok!.Value as ChannelStartupHealthResponse;
        payload.ShouldNotBeNull();
        payload!.Status.ShouldBe("ok");
        payload.Configured.ShouldBe(2);
        payload.Started.ShouldBe(2);
        payload.Failed.ShouldBeEmpty();
    }

    [Fact]
    public void Health_WhenAnAdapterFailedToStart_Returns503AndNamesTheFailedAdapter()
    {
        var report = new ChannelStartupReport();
        report.Record(
        [
            Outcome("telegram", started: false, ChannelFailureKind.Transient, new HttpRequestException("502")),
            Outcome("signalr", started: true),
        ]);

        var controller = CreateController(report, "telegram", "signalr");

        var result = controller.Health();

        var status = result.Result as ObjectResult;
        status.ShouldNotBeNull();
        status!.StatusCode.ShouldBe(StatusCodes.Status503ServiceUnavailable);

        var payload = status.Value as ChannelStartupHealthResponse;
        payload.ShouldNotBeNull();
        payload!.Status.ShouldBe("degraded");

        // The distinction the issue demands: configured != started.
        payload.Configured.ShouldBe(2);
        payload.Started.ShouldBe(1);

        payload.Failed.ShouldHaveSingleItem();
        payload.Failed[0].ChannelType.ShouldBe("telegram");
        payload.Failed[0].FailureKind.ShouldBe(nameof(ChannelFailureKind.Transient));
        payload.Failed[0].Error.ShouldContain("502");
    }

    [Fact]
    public void Health_BeforeStartupPassCompletes_ReportsConfiguredCountAndZeroStarted()
    {
        // An empty report means ExecuteAsync has not finished its startup pass. Reporting the
        // configured count with zero started is honest; folding it to "ok" is exactly the lie
        // "Gateway started with 5 channel adapter(s)" told.
        var controller = CreateController(new ChannelStartupReport(), "telegram", "signalr");

        var result = controller.Health();

        var status = result.Result as ObjectResult;
        status.ShouldNotBeNull();
        status!.StatusCode.ShouldBe(StatusCodes.Status503ServiceUnavailable);

        var payload = status.Value as ChannelStartupHealthResponse;
        payload.ShouldNotBeNull();
        payload!.Configured.ShouldBe(2);
        payload.Started.ShouldBe(0);
        payload.Failed.Select(f => f.ChannelType).ShouldBe(["telegram", "signalr"], ignoreOrder: true);
    }

    [Fact]
    public void DescribeStartup_NamesFailedAdaptersAndSeparatesConfiguredFromStarted()
    {
        var summary = ChannelStartupCoordinator.DescribeStartup(
        [
            Outcome("telegram", started: false, ChannelFailureKind.Terminal, new InvalidOperationException("bad token")),
            Outcome("signalr", started: true),
        ]);

        summary.ShouldContain("telegram");
        summary.ShouldContain("1 of 2");
    }

    private static ChannelStartOutcome Outcome(
        string channelType,
        bool started,
        ChannelFailureKind? kind = null,
        Exception? error = null)
        => new(channelType, channelType, started, 1, kind, error);

    private static ChannelsController CreateController(ChannelStartupReport report, params string[] channelTypes)
    {
        var manager = new Mock<IChannelManager>();
        manager.SetupGet(value => value.Adapters).Returns(channelTypes.Select(CreateAdapter).ToArray());
        return new ChannelsController(manager.Object, report);
    }

    private static IChannelAdapter CreateAdapter(string channelType)
    {
        var adapter = new Mock<IChannelAdapter>();
        adapter.SetupGet(value => value.ChannelType).Returns(channelType);
        adapter.SetupGet(value => value.DisplayName).Returns(channelType);
        adapter.SetupGet(value => value.IsRunning).Returns(false);
        return adapter.Object;
    }
}
