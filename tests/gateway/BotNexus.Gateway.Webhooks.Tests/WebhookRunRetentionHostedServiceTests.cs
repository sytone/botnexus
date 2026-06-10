using BotNexus.Gateway.Webhooks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace BotNexus.Gateway.Webhooks.Tests;

public sealed class WebhookRunRetentionHostedServiceTests
{
    [Fact]
    public async Task RunRetentionOnce_InvokesPurgeWithCorrectCutoff()
    {
        var store = Substitute.For<IWebhookRunStore>();
        store.PurgeOlderThanAsync(Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(5);

        var options = Options.Create(new WebhookRunRetentionOptions { RetentionDays = 14 });
        var sut = new WebhookRunRetentionHostedService(
            store, options, NullLogger<WebhookRunRetentionHostedService>.Instance);

        var before = DateTimeOffset.UtcNow.AddDays(-14);
        var purged = await sut.RunRetentionOnceAsync();
        var after = DateTimeOffset.UtcNow.AddDays(-14);

        Assert.Equal(5, purged);
        await store.Received(1).PurgeOlderThanAsync(
            Arg.Is<DateTimeOffset>(d => d >= before && d <= after),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunRetentionOnce_ReturnsZeroWhenRetentionDaysIsZero()
    {
        var store = Substitute.For<IWebhookRunStore>();
        var options = Options.Create(new WebhookRunRetentionOptions { RetentionDays = 0 });
        var sut = new WebhookRunRetentionHostedService(
            store, options, NullLogger<WebhookRunRetentionHostedService>.Instance);

        var purged = await sut.RunRetentionOnceAsync();

        Assert.Equal(0, purged);
        await store.DidNotReceive().PurgeOlderThanAsync(Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunRetentionOnce_ReturnsZeroWhenRetentionDaysIsNegative()
    {
        var store = Substitute.For<IWebhookRunStore>();
        var options = Options.Create(new WebhookRunRetentionOptions { RetentionDays = -1 });
        var sut = new WebhookRunRetentionHostedService(
            store, options, NullLogger<WebhookRunRetentionHostedService>.Instance);

        var purged = await sut.RunRetentionOnceAsync();

        Assert.Equal(0, purged);
        await store.DidNotReceive().PurgeOlderThanAsync(Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
    }
}
