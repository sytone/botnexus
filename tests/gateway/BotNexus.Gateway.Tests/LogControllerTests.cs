using BotNexus.Gateway.Api.Controllers;
using BotNexus.Gateway.Api.Logging;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Serilog;

namespace BotNexus.Gateway.Tests;

public sealed class LogControllerTests
{
    [Fact]
    public void GetRecent_WhenClientLogsPosted_ReturnsMostRecentEntries()
    {
        var store = new InMemoryRecentLogStore();
        using var serilogLogger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Sink(new RecentLogStoreSink(store))
            .CreateLogger();
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddSerilog(serilogLogger));
        var controller = new LogController(loggerFactory.CreateLogger<LogController>(), store);

        controller.Post(new ClientLogEntry("info", "first", "{}", "1", null));
        controller.Post(new ClientLogEntry("error", "second", "{}", "1", null));

        var result = controller.GetRecent(limit: 1);

        var entries = (result.Result as OkObjectResult)?.Value as IReadOnlyList<RecentLogEntry>;
        entries.ShouldNotBeNull();
        entries!.ShouldHaveSingleItem();
        entries[0].Message.ShouldContain("second");
    }

    [Fact]
    public void Post_WhenClientFieldsContainControls_RecentLogCannotContainForgedLines()
    {
        var store = new InMemoryRecentLogStore();
        using var serilogLogger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Sink(new LogTextSanitizingSink(new RecentLogStoreSink(store)))
            .CreateLogger();
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddSerilog(serilogLogger));
        var controller = new LogController(loggerFactory.CreateLogger<LogController>(), store);

        controller.Post(new ClientLogEntry(
            "info",
            "message\r\nFORGED-MESSAGE",
            "data\u0085FORGED-DATA",
            "version\nFORGED-VERSION",
            null));

        var entry = store.GetRecent(1).ShouldHaveSingleItem();
        entry.Message.ShouldNotContain('\r');
        entry.Message.ShouldNotContain('\n');
        entry.Message.ShouldNotContain('\u0085');
        entry.Message.ShouldContain("message\\r\\nFORGED-MESSAGE");
        entry.Message.ShouldContain("data\\u0085FORGED-DATA");
        entry.Message.ShouldContain("version\\nFORGED-VERSION");
    }

    [Theory]
    [InlineData("version")]
    [InlineData("message")]
    [InlineData("data")]
    public void Post_WhenClientFieldExceedsItsBound_ReturnsBadRequest(string field)
    {
        using var loggerFactory = LoggerFactory.Create(_ => { });
        var controller = new LogController(
            loggerFactory.CreateLogger<LogController>(),
            new InMemoryRecentLogStore());
        var oversized = new string('x', 16_385);
        var entry = field switch
        {
            "version" => new ClientLogEntry("info", "message", "data", oversized, null),
            "message" => new ClientLogEntry("info", oversized, "data", "1", null),
            _ => new ClientLogEntry("info", "message", oversized, "1", null)
        };

        controller.Post(entry).ShouldBeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public void GetRecent_WhenLimitExceedsMax_ClampsToFiveHundred()
    {
        var store = new InMemoryRecentLogStore(capacity: 1200);
        for (var index = 0; index < 700; index++)
        {
            store.Add(new RecentLogEntry(
                DateTimeOffset.UtcNow,
                "tests",
                "Information",
                $"entry-{index}",
                null,
                new Dictionary<string, object?>()));
        }

        using var loggerFactory = LoggerFactory.Create(_ => { });
        var controller = new LogController(loggerFactory.CreateLogger<LogController>(), store);
        var result = controller.GetRecent(limit: 1000);

        var entries = (result.Result as OkObjectResult)?.Value as IReadOnlyList<RecentLogEntry>;
        entries.ShouldNotBeNull();
        entries!.Count.ShouldBe(500);
    }
}
