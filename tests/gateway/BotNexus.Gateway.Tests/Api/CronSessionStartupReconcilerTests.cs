using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Api.Triggers;
using BotNexus.Gateway.Sessions;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotNexus.Gateway.Tests.Api;

public sealed class CronSessionStartupReconcilerTests
{
    [Fact]
    public async Task StartingAsync_SealsOnlyPersistedActiveCronSessions_AndIsIdempotent()
    {
        var store = new InMemorySessionStore();
        var staleCron = await store.GetOrCreateAsync(SessionId.From("cron:interrupted"), AgentId.From("agent-a"));
        staleCron.ChannelType = ChannelKey.From("cron");
        await store.SaveAsync(staleCron);

        var activeHuman = await store.GetOrCreateAsync(SessionId.From("signalr:live"), AgentId.From("agent-a"));
        activeHuman.ChannelType = ChannelKey.From("signalr");
        await store.SaveAsync(activeHuman);

        var sealedCron = await store.GetOrCreateAsync(SessionId.From("cron:complete"), AgentId.From("agent-a"));
        sealedCron.ChannelType = ChannelKey.From("cron");
        sealedCron.Status = SessionStatus.Sealed;
        await store.SaveAsync(sealedCron);

        var reconciler = new CronSessionStartupReconciler(store, NullLogger<CronSessionStartupReconciler>.Instance);
        await reconciler.StartingAsync(CancellationToken.None);
        var firstUpdatedAt = (await store.GetAsync(staleCron.SessionId))!.UpdatedAt;
        await reconciler.StartingAsync(CancellationToken.None);

        (await store.GetAsync(staleCron.SessionId))!.Status.ShouldBe(SessionStatus.Sealed);
        (await store.GetAsync(staleCron.SessionId))!.UpdatedAt.ShouldBe(firstUpdatedAt);
        (await store.GetAsync(activeHuman.SessionId))!.Status.ShouldBe(SessionStatus.Active);
        (await store.GetAsync(sealedCron.SessionId))!.Status.ShouldBe(SessionStatus.Sealed);
    }
}
