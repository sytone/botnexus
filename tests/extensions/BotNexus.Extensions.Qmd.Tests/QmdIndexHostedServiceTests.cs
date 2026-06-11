using BotNexus.Extensions.Qmd;
using Microsoft.Extensions.Logging;
using Moq;

namespace BotNexus.Extensions.Qmd.Tests;

public sealed class QmdIndexHostedServiceTests
{
    private readonly Mock<IQmdBackend> _mockBackend = new();
    private readonly Mock<ILogger<QmdIndexHostedService>> _mockLogger = new();

    [Fact]
    public async Task ExecuteAsync_NoAutoUpdateStores_CompletesImmediately()
    {
        var config = new QmdConfig
        {
            Stores = [new QmdStoreConfig { Name = "docs", Path = "/docs", AutoUpdate = false }]
        };

        var service = new QmdIndexHostedService(_mockBackend.Object, config, _mockLogger.Object);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        await service.StartAsync(cts.Token);
        await Task.Delay(500, cts.Token);
        await service.StopAsync(CancellationToken.None);

        _mockBackend.Verify(x => x.UpdateIndexAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task UpdateStoreAsync_Success_UpdatesHealthInfo()
    {
        _mockBackend.Setup(x => x.UpdateIndexAsync("notes", It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        _mockBackend.Setup(x => x.EmbedAsync("notes", It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var config = new QmdConfig
        {
            Stores = [new QmdStoreConfig { Name = "notes", Path = "/notes", AutoUpdate = true }]
        };

        var service = new QmdIndexHostedService(_mockBackend.Object, config, _mockLogger.Object);
        var store = config.Stores[0];

        await service.UpdateStoreAsync(store, CancellationToken.None);

        service.HealthInfo["notes"].IsHealthy.ShouldBeTrue();
        service.HealthInfo["notes"].LastSuccessfulUpdate.ShouldNotBeNull();
        service.HealthInfo["notes"].ConsecutiveFailures.ShouldBe(0);
    }

    [Fact]
    public async Task UpdateStoreAsync_Failure_IncrementsConsecutiveFailures()
    {
        _mockBackend.Setup(x => x.UpdateIndexAsync("bad", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("qmd not found"));

        var config = new QmdConfig
        {
            Stores = [new QmdStoreConfig { Name = "bad", Path = "/bad", AutoUpdate = true }]
        };

        var service = new QmdIndexHostedService(_mockBackend.Object, config, _mockLogger.Object);
        var store = config.Stores[0];

        await service.UpdateStoreAsync(store, CancellationToken.None);

        service.HealthInfo["bad"].ConsecutiveFailures.ShouldBe(1);
        service.HealthInfo["bad"].IsHealthy.ShouldBeTrue(); // < 3 failures
    }

    [Fact]
    public async Task UpdateStoreAsync_ThreeFailures_MarksUnhealthy()
    {
        _mockBackend.Setup(x => x.UpdateIndexAsync("flaky", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("timeout"));

        var config = new QmdConfig
        {
            Stores = [new QmdStoreConfig { Name = "flaky", Path = "/flaky", AutoUpdate = true }]
        };

        var service = new QmdIndexHostedService(_mockBackend.Object, config, _mockLogger.Object);
        var store = config.Stores[0];

        await service.UpdateStoreAsync(store, CancellationToken.None);
        await service.UpdateStoreAsync(store, CancellationToken.None);
        await service.UpdateStoreAsync(store, CancellationToken.None);

        service.HealthInfo["flaky"].ConsecutiveFailures.ShouldBe(3);
        service.HealthInfo["flaky"].IsHealthy.ShouldBeFalse();
    }

    [Fact]
    public async Task UpdateStoreAsync_SuccessAfterFailure_ResetsConsecutiveFailures()
    {
        var callCount = 0;
        _mockBackend.Setup(x => x.UpdateIndexAsync("recover", It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                callCount++;
                if (callCount <= 2) throw new InvalidOperationException("fail");
                return Task.CompletedTask;
            });
        _mockBackend.Setup(x => x.EmbedAsync("recover", It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var config = new QmdConfig
        {
            Stores = [new QmdStoreConfig { Name = "recover", Path = "/recover", AutoUpdate = true }]
        };

        var service = new QmdIndexHostedService(_mockBackend.Object, config, _mockLogger.Object);
        var store = config.Stores[0];

        await service.UpdateStoreAsync(store, CancellationToken.None);
        await service.UpdateStoreAsync(store, CancellationToken.None);
        service.HealthInfo["recover"].ConsecutiveFailures.ShouldBe(2);

        await service.UpdateStoreAsync(store, CancellationToken.None);
        service.HealthInfo["recover"].ConsecutiveFailures.ShouldBe(0);
        service.HealthInfo["recover"].IsHealthy.ShouldBeTrue();
    }

    [Fact]
    public async Task UpdateStoreAsync_CallsUpdateIndexAndEmbed()
    {
        _mockBackend.Setup(x => x.UpdateIndexAsync("docs", It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        _mockBackend.Setup(x => x.EmbedAsync("docs", It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var config = new QmdConfig
        {
            Stores = [new QmdStoreConfig { Name = "docs", Path = "/docs", AutoUpdate = true }]
        };

        var service = new QmdIndexHostedService(_mockBackend.Object, config, _mockLogger.Object);
        await service.UpdateStoreAsync(config.Stores[0], CancellationToken.None);

        _mockBackend.Verify(x => x.UpdateIndexAsync("docs", It.IsAny<CancellationToken>()), Times.Once);
        _mockBackend.Verify(x => x.EmbedAsync("docs", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void HealthInfo_InitializedForAutoUpdateStoresOnly()
    {
        var config = new QmdConfig
        {
            Stores =
            [
                new QmdStoreConfig { Name = "auto", Path = "/auto", AutoUpdate = true },
                new QmdStoreConfig { Name = "manual", Path = "/manual", AutoUpdate = false }
            ]
        };

        var service = new QmdIndexHostedService(_mockBackend.Object, config, _mockLogger.Object);

        service.HealthInfo.ShouldContainKey("auto");
        service.HealthInfo.ShouldNotContainKey("manual");
    }

    [Fact]
    public async Task UpdateStoreAsync_CancellationFromGateway_Propagates()
    {
        _mockBackend.Setup(x => x.UpdateIndexAsync("cancel", It.IsAny<CancellationToken>()))
            .Returns<string?, CancellationToken>((_, ct) => Task.Delay(TimeSpan.FromSeconds(30), ct));

        var config = new QmdConfig
        {
            Stores = [new QmdStoreConfig { Name = "cancel", Path = "/cancel", AutoUpdate = true }]
        };

        var service = new QmdIndexHostedService(_mockBackend.Object, config, _mockLogger.Object);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(
            () => service.UpdateStoreAsync(config.Stores[0], cts.Token));
    }
}
