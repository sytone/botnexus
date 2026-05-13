using BotNexus.Agent.Core.Types;
using BotNexus.Cron.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace BotNexus.Cron.Tests;

public sealed class CronToolTemplateTests
{
    [Fact]
    public async Task ExecuteAsync_Create_WithTemplateOnly_PersistsTemplateFields()
    {
        var store = new Mock<ICronStore>();
        var scheduler = CreateScheduler();
        CronJob? created = null;
        store.Setup(value => value.CreateAsync(It.IsAny<CronJob>(), It.IsAny<CancellationToken>()))
            .Callback<CronJob, CancellationToken>((job, _) => created = job)
            .ReturnsAsync((CronJob job, CancellationToken _) => job);
        var tool = new CronTool(store.Object, scheduler, "agent-a");

        await tool.ExecuteAsync("call-1", new Dictionary<string, object?>
        {
            ["action"] = "create",
            ["name"] = "Template job",
            ["schedule"] = "*/5 * * * *",
            ["templateName"] = "daily-status",
            ["templateParameters"] = new Dictionary<string, object?>
            {
                ["owner"] = "Hermes"
            }
        });

        created.ShouldNotBeNull();
        created!.Message.ShouldBeNull();
        created.TemplateName.ShouldBe("daily-status");
        created.TemplateParameters.ShouldNotBeNull();
        created.TemplateParameters!["owner"].ShouldBe("Hermes");
    }

    [Fact]
    public async Task ExecuteAsync_Create_WithoutMessageOrTemplate_Throws()
    {
        var tool = new CronTool(
            Mock.Of<ICronStore>(),
            CreateScheduler(),
            "agent-a");

        var act = () => tool.ExecuteAsync("call-1", new Dictionary<string, object?>
        {
            ["action"] = "create",
            ["name"] = "Invalid",
            ["schedule"] = "*/5 * * * *"
        });

        var ex = await act.ShouldThrowAsync<ArgumentException>();
        ex.Message.ShouldContain("Either 'message' or 'templateName' is required.");
    }

    private static CronScheduler CreateScheduler()
    {
        var store = new Mock<ICronStore>().Object;
        var scopeFactory = new ServiceCollection()
            .BuildServiceProvider()
            .GetRequiredService<IServiceScopeFactory>();
        var options = new StaticOptionsMonitor<CronOptions>(new CronOptions());
        return new CronScheduler(
            store,
            Array.Empty<ICronAction>(),
            scopeFactory,
            options,
            NullLogger<CronScheduler>.Instance);
    }

    private sealed class StaticOptionsMonitor<T>(T currentValue) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = currentValue;
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
