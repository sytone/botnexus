using BotNexus.Gateway.Api.Controllers;
using BotNexus.Gateway.Api.Logging;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace BotNexus.Gateway.Tests;

public sealed class LogControllerTests
{
    [Fact]
    public void GetRecent_WhenClientLogsPosted_ReturnsMostRecentEntries()
    {
        var store = new InMemoryRecentLogStore();
        using var loggerFactory = LoggerFactory.Create(builder =>
            builder.AddProvider(new RecentLogEntryLoggerProvider(store)));
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
