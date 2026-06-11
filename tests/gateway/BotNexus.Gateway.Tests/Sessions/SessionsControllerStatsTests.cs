using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Api.Controllers;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace BotNexus.Gateway.Tests.Sessions;

public sealed class SessionsControllerStatsTests
{
    private readonly ISessionStore _sessionStore = Substitute.For<ISessionStore>();

    private SessionsController CreateSut() => new(_sessionStore);

    [Fact]
    public async Task GetStats_NoFilter_ReturnsStats()
    {
        var stats = new SessionStats
        {
            TotalSessions = 10,
            ByStatus = new Dictionary<string, int> { ["Active"] = 5, ["Sealed"] = 5 },
            ByAgent = new List<AgentSessionCount> { new("farnsworth", 7), new("nova", 3) },
            Compaction = new CompactionStats(3, 7),
            GeneratedAt = DateTimeOffset.UtcNow
        };
        _sessionStore.GetStatsAsync(null, Arg.Any<CancellationToken>()).Returns(stats);
        var sut = CreateSut();

        var result = await sut.GetStats(null);

        var okResult = Assert.IsType<OkObjectResult>(result);
        var returnedStats = Assert.IsType<SessionStats>(okResult.Value);
        Assert.Equal(10, returnedStats.TotalSessions);
        Assert.Equal(2, returnedStats.ByStatus.Count);
        Assert.Equal(2, returnedStats.ByAgent.Count);
    }

    [Fact]
    public async Task GetStats_WithAgentFilter_PassesAgentId()
    {
        var stats = new SessionStats
        {
            TotalSessions = 5,
            ByStatus = new Dictionary<string, int> { ["Active"] = 5 },
            ByAgent = new List<AgentSessionCount> { new("farnsworth", 5) },
            Compaction = new CompactionStats(0, 5),
            GeneratedAt = DateTimeOffset.UtcNow
        };
        _sessionStore.GetStatsAsync(Arg.Is<AgentId?>(a => a!.Value == "farnsworth"), Arg.Any<CancellationToken>())
            .Returns(stats);
        var sut = CreateSut();

        var result = await sut.GetStats("farnsworth");

        var okResult = Assert.IsType<OkObjectResult>(result);
        var returnedStats = Assert.IsType<SessionStats>(okResult.Value);
        Assert.Equal(5, returnedStats.TotalSessions);
    }

    [Fact]
    public async Task GetStats_StoreReturnsNull_Returns404()
    {
        _sessionStore.GetStatsAsync(null, Arg.Any<CancellationToken>()).Returns((SessionStats?)null);
        var sut = CreateSut();

        var result = await sut.GetStats(null);

        Assert.IsType<NotFoundObjectResult>(result);
    }

    [Fact]
    public async Task GetStats_EmptyStore_ReturnsZeroStats()
    {
        var stats = new SessionStats
        {
            TotalSessions = 0,
            ByStatus = new Dictionary<string, int>(),
            ByAgent = new List<AgentSessionCount>(),
            Compaction = new CompactionStats(0, 0),
            GeneratedAt = DateTimeOffset.UtcNow
        };
        _sessionStore.GetStatsAsync(null, Arg.Any<CancellationToken>()).Returns(stats);
        var sut = CreateSut();

        var result = await sut.GetStats(null);

        var okResult = Assert.IsType<OkObjectResult>(result);
        var returnedStats = Assert.IsType<SessionStats>(okResult.Value);
        Assert.Equal(0, returnedStats.TotalSessions);
    }
}
