using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Agents;
using BotNexus.Gateway.Configuration;
using BotNexus.Gateway.Conversations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;

namespace BotNexus.Gateway.Tests;

public sealed class ConversationRetentionHostedServiceTests
{
    // ── Helpers ──────────────────────────────────────────────────────────────────

    private static ConversationRetentionHostedService CreateService(
        IConversationStore store,
        IAgentRegistry registry,
        ConversationRetentionOptions options,
        IConversationChangeNotifier? notifier = null)
    {
        notifier ??= Substitute.For<IConversationChangeNotifier>();
        return new ConversationRetentionHostedService(
            store,
            registry,
            Options.Create(options),
            NullLogger<ConversationRetentionHostedService>.Instance,
            notifier);
    }

    private static AgentDescriptor CreateDescriptor(
        string agentId,
        AgentConversationRetentionConfig? retention = null)
        => new()
        {
            AgentId = AgentId.From(agentId),
            DisplayName = agentId,
            ModelId = "test-model",
            ApiProvider = "test",
            ConversationRetention = retention
        };

    private static Conversation CreateConversation(
        string conversationId,
        string agentId,
        double inactiveDays,
        ConversationStatus status = ConversationStatus.Active)
        => new()
        {
            ConversationId = ConversationId.From(conversationId),
            AgentId = AgentId.From(agentId),
            UpdatedAt = DateTimeOffset.UtcNow.AddDays(-inactiveDays),
            Status = status
        };

    // ── World-level disabled (opt-in gate) ───────────────────────────────────────

    [Fact]
    public async Task RunRetentionOnceAsync_WhenWorldDisabled_ArchivesNothing()
    {
        var store = new InMemoryConversationStore();
        var registry = new DefaultAgentRegistry(NullLogger<DefaultAgentRegistry>.Instance);
        await store.CreateAsync(CreateConversation("c-1", "a-1", inactiveDays: 40));
        var options = new ConversationRetentionOptions { AutoArchiveEnabled = false, AutoArchiveAfterDays = 30 };

        var svc = CreateService(store, registry, options);
        var archived = await svc.RunRetentionOnceAsync();

        archived.ShouldBe(0);
        var conv = await store.GetAsync(ConversationId.From("c-1"));
        conv!.Status.ShouldBe(ConversationStatus.Active);
    }

    // ── World default: archive inactive conversations ────────────────────────────

    [Fact]
    public async Task RunRetentionOnceAsync_WhenWorldEnabled_ArchivesExpired()
    {
        var store = new InMemoryConversationStore();
        var registry = new DefaultAgentRegistry(NullLogger<DefaultAgentRegistry>.Instance);
        var conv = CreateConversation("c-1", "a-1", inactiveDays: 31);
        await store.CreateAsync(conv);
        var options = new ConversationRetentionOptions { AutoArchiveEnabled = true, AutoArchiveAfterDays = 30 };

        var svc = CreateService(store, registry, options);
        var archived = await svc.RunRetentionOnceAsync();

        archived.ShouldBe(1);
        var updated = await store.GetAsync(ConversationId.From("c-1"));
        updated!.Status.ShouldBe(ConversationStatus.Archived);
    }

    [Fact]
    public async Task RunRetentionOnceAsync_WhenRecentlyActive_DoesNotArchive()
    {
        var store = new InMemoryConversationStore();
        var registry = new DefaultAgentRegistry(NullLogger<DefaultAgentRegistry>.Instance);
        await store.CreateAsync(CreateConversation("c-1", "a-1", inactiveDays: 5));
        var options = new ConversationRetentionOptions { AutoArchiveEnabled = true, AutoArchiveAfterDays = 30 };

        var svc = CreateService(store, registry, options);
        var archived = await svc.RunRetentionOnceAsync();

        archived.ShouldBe(0);
        var conv = await store.GetAsync(ConversationId.From("c-1"));
        conv!.Status.ShouldBe(ConversationStatus.Active);
    }

    // ── Per-agent override: disabled flag wins ───────────────────────────────────

    [Fact]
    public async Task RunRetentionOnceAsync_AgentDisabled_SkipsAgentConversations()
    {
        var store = new InMemoryConversationStore();
        var registry = new DefaultAgentRegistry(NullLogger<DefaultAgentRegistry>.Instance);
        registry.Register(CreateDescriptor("a-1", new AgentConversationRetentionConfig { AutoArchiveEnabled = false }));
        await store.CreateAsync(CreateConversation("c-1", "a-1", inactiveDays: 60));
        var options = new ConversationRetentionOptions { AutoArchiveEnabled = true, AutoArchiveAfterDays = 30 };

        var svc = CreateService(store, registry, options);
        var archived = await svc.RunRetentionOnceAsync();

        archived.ShouldBe(0);
        var conv = await store.GetAsync(ConversationId.From("c-1"));
        conv!.Status.ShouldBe(ConversationStatus.Active);
    }

    // ── Per-agent override: shorter window ───────────────────────────────────────

    [Fact]
    public async Task RunRetentionOnceAsync_AgentOverridesThreshold_UsesAgentThreshold()
    {
        var store = new InMemoryConversationStore();
        var registry = new DefaultAgentRegistry(NullLogger<DefaultAgentRegistry>.Instance);
        registry.Register(CreateDescriptor("a-1", new AgentConversationRetentionConfig { AutoArchiveAfterDays = 7 }));
        // inactive 10 days: world threshold=30 would NOT archive, agent threshold=7 SHOULD archive
        await store.CreateAsync(CreateConversation("c-1", "a-1", inactiveDays: 10));
        var options = new ConversationRetentionOptions { AutoArchiveEnabled = true, AutoArchiveAfterDays = 30 };

        var svc = CreateService(store, registry, options);
        var archived = await svc.RunRetentionOnceAsync();

        archived.ShouldBe(1);
        var conv = await store.GetAsync(ConversationId.From("c-1"));
        conv!.Status.ShouldBe(ConversationStatus.Archived);
    }

    // ── Already-archived conversations are skipped ────────────────────────────────

    [Fact]
    public async Task RunRetentionOnceAsync_AlreadyArchived_IsSkipped()
    {
        var store = new InMemoryConversationStore();
        var registry = new DefaultAgentRegistry(NullLogger<DefaultAgentRegistry>.Instance);
        // Create active, then archive manually so we have a real Archived row
        await store.CreateAsync(CreateConversation("c-1", "a-1", inactiveDays: 60));
        await store.ArchiveAsync(ConversationId.From("c-1"));
        var options = new ConversationRetentionOptions { AutoArchiveEnabled = true, AutoArchiveAfterDays = 30 };

        var notifier = Substitute.For<IConversationChangeNotifier>();
        var svc = CreateService(store, registry, options, notifier);
        var archived = await svc.RunRetentionOnceAsync();

        archived.ShouldBe(0);
        // Notifier must NOT have been called for already-archived row
        await notifier.DidNotReceive().NotifyConversationChangedAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // ── Null notifier (not registered) ─────────────────────────────────────────────

    [Fact]
    public async Task RunRetentionOnceAsync_WhenNotifierIsNull_ArchivesWithoutThrowing()
    {
        var store = new InMemoryConversationStore();
        var registry = new DefaultAgentRegistry(NullLogger<DefaultAgentRegistry>.Instance);
        await store.CreateAsync(CreateConversation("c-1", "a-1", inactiveDays: 31));
        var options = new ConversationRetentionOptions { AutoArchiveEnabled = true, AutoArchiveAfterDays = 30 };

        var svc = new ConversationRetentionHostedService(
            store,
            registry,
            Options.Create(options),
            NullLogger<ConversationRetentionHostedService>.Instance,
            changeNotifier: null);
        var archived = await svc.RunRetentionOnceAsync();

        archived.ShouldBe(1);
        var conv = await store.GetAsync(ConversationId.From("c-1"));
        conv!.Status.ShouldBe(ConversationStatus.Archived);
    }

    // ── SignalR push fires on archive ─────────────────────────────────────────────

    [Fact]
    public async Task RunRetentionOnceAsync_WhenArchived_FiresSignalRPush()
    {
        var store = new InMemoryConversationStore();
        var registry = new DefaultAgentRegistry(NullLogger<DefaultAgentRegistry>.Instance);
        await store.CreateAsync(CreateConversation("c-1", "a-1", inactiveDays: 31));
        var options = new ConversationRetentionOptions { AutoArchiveEnabled = true, AutoArchiveAfterDays = 30 };

        var notifier = Substitute.For<IConversationChangeNotifier>();
        var svc = CreateService(store, registry, options, notifier);
        await svc.RunRetentionOnceAsync();

        await notifier.Received(1).NotifyConversationChangedAsync(
            "archived", "a-1", "c-1", Arg.Any<CancellationToken>());
    }

    // ── DI activation regression (#1360 / #1282 / #1284) ──────────────────────────
    // The change-notifier is registered only by the SignalR channel extension. In a
    // minimal / non-SignalR deployment (the documented Docker config) it is absent, so the
    // hosted service MUST still activate. Microsoft.Extensions.DependencyInjection ignores the
    // C# nullable annotation for single-service ctor params, hence the `= null` default. These
    // tests build a real container with ValidateOnBuild + ValidateScopes to reproduce the exact
    // startup-crash path #1284 left unguarded.

    private static ServiceCollection BuildMinimalServices(bool registerNotifier)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOptions<ConversationRetentionOptions>();
        services.AddSingleton<IConversationStore>(new InMemoryConversationStore());
        services.AddSingleton<IAgentRegistry>(
            new DefaultAgentRegistry(NullLogger<DefaultAgentRegistry>.Instance));
        if (registerNotifier)
            services.AddSingleton(Substitute.For<IConversationChangeNotifier>());
        services.AddHostedService<ConversationRetentionHostedService>();
        return services;
    }

    [Fact]
    public void HostedService_ActivatesWithoutChangeNotifier_WhenValidatingOnBuild()
    {
        var services = BuildMinimalServices(registerNotifier: false);

        // Reproduces the host startup path: ValidateOnBuild eagerly activates every IHostedService.
        using var provider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

        var hosted = provider.GetServices<IHostedService>().ToList();
        hosted.ShouldContain(s => s is ConversationRetentionHostedService);
    }

    [Fact]
    public void HostedService_ActivatesWithChangeNotifier_WhenRegistered()
    {
        var services = BuildMinimalServices(registerNotifier: true);

        using var provider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

        var hosted = provider.GetServices<IHostedService>().ToList();
        hosted.ShouldContain(s => s is ConversationRetentionHostedService);
    }

    [Fact]
    public void ConstructingDirectly_WithoutNotifier_DoesNotThrow()
    {
        var store = new InMemoryConversationStore();
        var registry = new DefaultAgentRegistry(NullLogger<DefaultAgentRegistry>.Instance);
        var options = Options.Create(new ConversationRetentionOptions());

        var act = () => new ConversationRetentionHostedService(
            store,
            registry,
            options,
            NullLogger<ConversationRetentionHostedService>.Instance);

        Should.NotThrow(act);
    }

    [Fact]
    public async Task RunRetentionOnceAsync_WithoutNotifier_DoesNotThrowOnArchive()
    {
        var store = new InMemoryConversationStore();
        var registry = new DefaultAgentRegistry(NullLogger<DefaultAgentRegistry>.Instance);
        await store.CreateAsync(CreateConversation("c-1", "a-1", inactiveDays: 31));
        var options = new ConversationRetentionOptions { AutoArchiveEnabled = true, AutoArchiveAfterDays = 30 };

        // No notifier supplied -> NotifyBestEffortAsync must short-circuit, not NRE.
        var svc = new ConversationRetentionHostedService(
            store,
            registry,
            Options.Create(options),
            NullLogger<ConversationRetentionHostedService>.Instance);

        var archived = await svc.RunRetentionOnceAsync();

        archived.ShouldBe(1);
        var conv = await store.GetAsync(ConversationId.From("c-1"));
        conv!.Status.ShouldBe(ConversationStatus.Archived);
    }
}
