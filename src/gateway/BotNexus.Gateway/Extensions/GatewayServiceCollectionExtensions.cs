using System.Text.Json;
using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Events;
using BotNexus.Gateway.Conversations;
using BotNexus.Gateway.Dispatching;
using BotNexus.Persistence.Sqlite;
using Microsoft.Data.Sqlite;
using BotNexus.Gateway.Abstractions.Activity;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Citizens;
using BotNexus.Gateway.Abstractions.Hooks;
using BotNexus.Gateway.Abstractions.Isolation;
using BotNexus.Gateway.Abstractions.Media;
using BotNexus.Gateway.Abstractions.Routing;
using BotNexus.Gateway.Abstractions.Security;
using BotNexus.Gateway.Abstractions.Services;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Abstractions.Configuration;
using BotNexus.Gateway.Abstractions.Extensions;
using BotNexus.Gateway.Abstractions.Satellites;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Activity;
using BotNexus.Gateway.Agents;
using BotNexus.Gateway.Citizens;
using BotNexus.Gateway.Configuration;
using BotNexus.Gateway.Commands;
using BotNexus.Gateway.Diagnostics;
using BotNexus.Gateway.Hooks;
using BotNexus.Gateway.Isolation;
using BotNexus.Gateway.Media;
using BotNexus.Gateway.Routing;
using BotNexus.Gateway.Ralph;
using BotNexus.Gateway.Services;
using BotNexus.Gateway.Sessions;
using BotNexus.Gateway.Security;
using BotNexus.Gateway.Federation;
using BotNexus.Gateway.Channels;
using BotNexus.Gateway.Contracts.Memory;
using BotNexus.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using BotNexus.Gateway.Configuration.Shadow;
using BotNexus.Gateway.Configuration.Store;
using Microsoft.FeatureManagement;
using System.Globalization;
using System.IO.Abstractions;

namespace BotNexus.Gateway.Extensions;

/// <summary>
/// DI registration extensions for the Gateway runtime services.
/// </summary>
public static class GatewayServiceCollectionExtensions
{
    /// <summary>
    /// Registers the core Gateway services: registry, supervisor, router, broadcaster,
    /// registered isolation strategies, and the Gateway host background service.
    /// </summary>
    /// <remarks>
    /// Registers <see cref="InMemorySessionStore"/> as the default <see cref="ISessionStore"/> via
    /// <see cref="ServiceCollectionDescriptorExtensions.TryAddSingleton(IServiceCollection, Type, Type)"/>.
    /// Consumers can replace it by registering their own <see cref="ISessionStore"/> implementation
    /// before or after calling this method.
    /// </remarks>
    public static IServiceCollection AddBotNexusGateway(
        this IServiceCollection services,
        IConfiguration? config = null,
        Action<GatewayOptions>? configure = null)
    {
        services.AddOptions<GatewayOptions>();
        services.AddOptions<SessionCleanupOptions>();
        services.AddOptions<ConversationRetentionOptions>();
        services.AddOptions<SubAgentWorkspaceSweepOptions>();
        services.AddOptions<SessionWarmupOptions>();
        services.AddOptions<DelayToolOptions>();
        services.AddOptions<FileWatcherToolOptions>();
        services.AddOptions<CompactionOptions>();
        services.AddOptions<SqliteWalCheckpointOptions>();
        services.AddOptions<LivenessWatchdogOptions>();
        services.AddOptions<SessionConsistencyOptions>();
        if (configure is not null)
            services.Configure(configure);
        if (config is not null)
        {
            services.Configure<GatewayOptions>(config.GetSection("gateway"));
            services.Configure<SessionCleanupOptions>(config.GetSection("gateway:sessionCleanup"));
            services.Configure<SessionWarmupOptions>(config.GetSection("gateway:sessionWarmup"));
            services.Configure<SubAgentOptions>(config.GetSection("gateway:subAgents"));
            services.Configure<DelayToolOptions>(config.GetSection("gateway:delayTool"));
            services.Configure<FileWatcherToolOptions>(config.GetSection("gateway:fileWatcherTool"));
            services.Configure<AgentExchangeOptions>(config.GetSection("gateway:agentExchange"));
            services.Configure<AgentExchangeBudgetOptions>(config.GetSection("gateway:agentExchange"));
            services.Configure<ConversationRetentionOptions>(config.GetSection("gateway:conversations"));
            services.Configure<SubAgentWorkspaceSweepOptions>(config.GetSection("gateway:subAgentWorkspace"));
            services.Configure<LivenessWatchdogOptions>(config.GetSection("gateway:livenessWatchdog"));
            services.Configure<SessionConsistencyOptions>(config.GetSection("gateway:sessionConsistency"));
            services.Configure<SqliteWalCheckpointOptions>(o =>
                o.IntervalMinutes = ParseInt(
                    config["gateway:walCheckpointIntervalMinutes"],
                    SqliteWalCheckpointOptions.DefaultIntervalMinutes));
            services.Configure<TranscriptExportOptions>(config.GetSection("gateway:" + TranscriptExportOptions.SectionName));

            var compactionSection = config.GetSection("gateway:compaction");
            if (compactionSection.Exists())
            {
                var configuredCompaction = new CompactionOptions
                {
                    PreservedTurns = ParseInt(compactionSection["preservedTurns"], new CompactionOptions().PreservedTurns),
                    MaxSummaryChars = ParseInt(compactionSection["maxSummaryChars"], new CompactionOptions().MaxSummaryChars),
                    TokenThresholdRatio = ParseDouble(compactionSection["tokenThresholdRatio"], new CompactionOptions().TokenThresholdRatio),
                    ContextWindowTokens = ParseInt(compactionSection["contextWindowTokens"], new CompactionOptions().ContextWindowTokens),
                    LargestEntryBytesThreshold = ParseInt(compactionSection["largestEntryBytesThreshold"], new CompactionOptions().LargestEntryBytesThreshold),
                    SummarizationModel = ParseString(compactionSection["summarizationModel"], new CompactionOptions().SummarizationModel),
                    SummarizationProvider = ParseString(compactionSection["summarizationProvider"], new CompactionOptions().SummarizationProvider),
                    CircuitBreakerCooldownSeconds = ParseInt(compactionSection["circuitBreakerCooldownSeconds"], new CompactionOptions().CircuitBreakerCooldownSeconds),
                    CronLlmIdleTimeoutMs = ParseInt(compactionSection["cronLlmIdleTimeoutMs"], new CompactionOptions().CronLlmIdleTimeoutMs)
                };
                services.AddSingleton<IOptions<CompactionOptions>>(_ => Options.Create(configuredCompaction));
                services.Replace(ServiceDescriptor.Singleton<IOptionsMonitor<CompactionOptions>>(
                    _ => new StaticOptionsMonitor<CompactionOptions>(configuredCompaction)));
            }
        }

        // Core services
        services.TryAddSingleton<IFileSystem, FileSystem>();
        services.TryAddSingleton<BotNexusHome>();
        services.TryAddSingleton<IMemoryStoreFactory>(serviceProvider =>
        {
            var home = serviceProvider.GetRequiredService<BotNexusHome>();
            var fileSystem = serviceProvider.GetRequiredService<IFileSystem>();
            return new MemoryStoreFactory(agentId =>
            {
                var agentDirectory = home.GetAgentDirectory(agentId);
                return Path.Combine(agentDirectory, "data", "memory.sqlite");
            }, fileSystem);
        });
        services.AddSingleton<IAgentWorkspaceManager, FileAgentWorkspaceManager>();
        services.TryAddSingleton<IAgentMemoryFactory, DefaultAgentMemoryFactory>();
         services.AddSingleton<IContextBuilder, WorkspaceContextBuilder>();
         services.AddSingleton<IAgentRegistry, DefaultAgentRegistry>();
         services.AddSingleton<IUserRegistry, DefaultUserRegistry>();
         services.AddSingleton<ICitizenRegistry, DefaultCitizenRegistry>();
         services.TryAddSingleton<IAgentConfigurationWriter, NoOpAgentConfigurationWriter>();
        services.AddSingleton<IAgentSupervisor, DefaultAgentSupervisor>();
        // #2614: the ONE execution-layer tool-audit sink, registered once. Both the streaming
        // delivery path and every blocking PromptAsync caller render their durable tool history
        // through this single implementation, so the audit guarantee no longer depends on which
        // transport the caller picked.
        services.TryAddSingleton<BotNexus.Gateway.Audit.IToolAuditSink>(
            _ => BotNexus.Gateway.Audit.DefaultToolAuditSink.Instance);
        services.AddSingleton<AgentExchangeBudgetTracker>();
        // #1542: the shared turn loop and cross-world federation routing are their own
        // single-responsibility collaborators, injected into AgentExchangeService.
        services.AddSingleton<AgentExchangeTurnEngine>(serviceProvider =>
            new AgentExchangeTurnEngine(
                serviceProvider.GetRequiredService<ISessionStore>(),
                serviceProvider.GetRequiredService<IConversationStore>(),
                serviceProvider.GetRequiredService<ILogger<AgentExchangeTurnEngine>>(),
                serviceProvider.GetService<AgentExchangeBudgetTracker>()));
        services.AddSingleton<ICrossWorldExchangeRouter>(serviceProvider =>
            new CrossWorldExchangeRouter(
                serviceProvider.GetRequiredService<AgentExchangeTurnEngine>(),
                serviceProvider.GetRequiredService<ISessionStore>(),
                serviceProvider.GetRequiredService<IConversationStore>(),
                serviceProvider.GetRequiredService<IOptions<PlatformConfig>>(),
                serviceProvider.GetRequiredService<CrossWorldChannelAdapter>()));
        services.AddSingleton<IAgentExchangeService, AgentExchangeService>();
        services.AddSingleton<CrossWorldInboundAuthService>();
        services.TryAddSingleton<IWorldContext, PlatformWorldContext>();
        services.TryAddSingleton<CrossWorldChannelOptions>();
        services.AddSingleton<CrossWorldChannelAdapter>(serviceProvider =>
            new CrossWorldChannelAdapter(
                serviceProvider.GetRequiredService<ILogger<CrossWorldChannelAdapter>>(),
                serviceProvider.GetService<HttpClient>() ?? new HttpClient(),
                serviceProvider.GetService<CrossWorldChannelOptions>()));
        services.AddSingleton<IChannelAdapter>(serviceProvider => serviceProvider.GetRequiredService<CrossWorldChannelAdapter>());
        services.AddSingleton<ISubAgentManager, DefaultSubAgentManager>();
        services.TryAddSingleton<SessionLifecycleEvents>();
        services.TryAddSingleton<ISessionLifecycleEvents>(serviceProvider =>
            serviceProvider.GetRequiredService<SessionLifecycleEvents>());
        services.TryAddSingleton<SessionWarmupService>();
        // #2818: the ralph loop is a plain subscriber to the turn-end lifecycle event the gateway
        // already publishes, registered as a hosted service so it subscribes at startup.
        services.TryAddSingleton<IRalphIterationRunner, RalphIterationRunner>();
        services.TryAddSingleton<RalphLoopTrigger>();
        services.AddHostedService(serviceProvider => serviceProvider.GetRequiredService<RalphLoopTrigger>());
        services.TryAddSingleton<ISessionWarmupService>(serviceProvider =>
            serviceProvider.GetRequiredService<SessionWarmupService>());
        services.AddSingleton<IMessageRouter, DefaultMessageRouter>();
        services.AddSingleton<IConfigPathResolver, ConfigPathResolver>();
        services.TryAddSingleton<IChannelManager, ChannelManager>();
        // Channel-neutral conversation event seam (#2085). Registered here so gateway code can
        // publish conversation facts today; sinks are supplied by channel extensions as the
        // migration slices land. With zero registered sinks publication is a no-op, so this
        // registration is inert until an extension opts in.
        services.TryAddSingleton<ConversationEventPublisherOptions>();
        services.TryAddSingleton<ConversationEventPublisher>();
        services.TryAddSingleton<IConversationEventPublisher>(serviceProvider =>
            serviceProvider.GetRequiredService<ConversationEventPublisher>());
        services.TryAddSingleton<ISessionRunDrain>(serviceProvider =>
            new SupervisorSessionRunDrain(
                serviceProvider,
                serviceProvider.GetService<ILogger<SupervisorSessionRunDrain>>()));
        services.TryAddSingleton<ISessionStore>(serviceProvider =>
            AttachArchiveDrain(new InMemorySessionStore(), serviceProvider));
        services.TryAddSingleton<ISessionWriteLock, SessionWriteLock>();
        services.TryAddSingleton<IConversationStore, InMemoryConversationStore>();
        services.TryAddSingleton<IConversationSectionStore, InMemoryConversationSectionStore>();
        services.TryAddSingleton<IAgentIdentityResolver, AgentIdentityResolver>();
        services.AddSingleton<IAgentCanvasNotifier, ConversationCanvasNotifier>();
        services.TryAddSingleton<IConversationRouter, DefaultConversationRouter>();
        services.TryAddSingleton<IConversationDispatcher, DefaultConversationDispatcher>();
        services.TryAddSingleton<IAskUserResponseRegistry, AskUserResponseRegistry>();
        services.TryAddSingleton<IAskUserPromptResolver, AskUserPromptResolver>();
        // #2047: durable ask_user checkpoint resolution + restart resume. The resumer dispatches a
        // continuation turn through the router/orchestrator; the checkpoint service is the single
        // source of truth for resolving a response against persisted state; the hosted reconciliation
        // service rehydrates the in-memory interception map from durable checkpoints on startup.
        services.TryAddSingleton<IAskUserCheckpointResumer, AskUserCheckpointResumer>();
        services.TryAddSingleton<IAskUserCheckpointService, AskUserCheckpointService>();
        services.AddHostedService<AskUserCheckpointReconciliationService>();
        services.TryAddSingleton<PendingAskUserInterceptor>();
        services.AddSingleton<InternalChannelAdapter>();
        services.AddSingleton<IChannelAdapter>(serviceProvider => serviceProvider.GetRequiredService<InternalChannelAdapter>());
        services.AddSingleton<ISessionCompactor, LlmSessionCompactor>();
        services.AddSingleton<IPreCompactionMemoryFlusher, PreCompactionMemoryFlusher>();
        services.AddSingleton<ISessionCompactionCoordinator, SessionCompactionCoordinator>();
        services.AddSingleton<ISessionEndMemoryFlusher, SessionEndMemoryFlusher>();
        services.AddSingleton<IConversationResetService, DefaultConversationResetService>();
        services.AddSingleton<IMediaPipeline, MediaPipeline>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ICommandContributor, BuiltInCommandContributor>());
        services.TryAddSingleton<CommandRegistry>();
        services.AddSingleton<IActivityBroadcaster, InMemoryActivityBroadcaster>();
        // Feature flags (#1931 rollout safety): AddFeatureManagement binds onto the same
        // IConfiguration that config.json is loaded into, exposing flags under a
        // "FeatureManagement" section. Registered idempotently so repeated composition is safe.
        services.AddFeatureManagement();
        services.AddSingleton<IGatewayAuthHandler>(sp =>
            new ApiKeyGatewayAuthHandler(
                apiKey: null,
                sp.GetRequiredService<ILogger<ApiKeyGatewayAuthHandler>>(),
                sp.GetService<ISecurityEventSink>(),
                sp.GetService<IFeatureManager>()));
        services.AddSingleton<IModelFilter, ConfigModelFilter>();

        // Hook dispatcher: register as a concrete singleton instance so that
        // LoadConfiguredExtensionsAsync can locate it via ImplementationInstance
        // and register extension-discovered hook handlers on the same instance.
        // Built-in handlers are registered during startup via HookDispatcherInitializer.
        services.TryAddSingleton<IHookDispatcher>(new HookDispatcher());
        services.AddHostedService<HookDispatcherInitializer>();

        // Tool policy
        services.TryAddSingleton<DefaultToolPolicyProvider>();
        services.TryAddSingleton<IToolPolicyProvider>(sp => sp.GetRequiredService<DefaultToolPolicyProvider>());
        services.AddSingleton<ToolPolicyHookHandler>(sp =>
            new ToolPolicyHookHandler(
                sp.GetRequiredService<DefaultToolPolicyProvider>(),
                sp.GetRequiredService<ILogger<ToolPolicyHookHandler>>(),
                sp.GetService<ISecurityEventSink>()));
        services.AddSingleton<AgentsMdPromptHookHandler>();
        services.TryAddSingleton<ISecretRedactor, SecretRedactor>();

        // #2557: opt-in cron failure alerts delivered into a configured conversation. Registered
        // here (not in AddBotNexusCron) because the delivery seam lives in the gateway assembly.
        services.TryAddSingleton<BotNexus.Cron.ICronFailureAlertSink, BotNexus.Gateway.Cron.ConversationCronFailureAlertSink>();

        // Trusted security-event sink (#1532, #1645): captures approval/auth/tool boundary
        // decisions for the future trusted diagnostics surface. Deliberately a separate bounded
        // ring buffer so these never leak onto the public diagnostic stream.
        services.TryAddSingleton<ISecurityEventSink>(_ => new RingBufferSecurityEventSink());

        // Exec/shell approval boundary. Wired to the trusted sink so every allow/deny/ask decision
        // emits one SecurityEvent; emission is best-effort and never breaks the approval path.
        services.TryAddSingleton<IExecApprovalManager>(sp =>
            new ExecApprovalManager(
                sp.GetService<ISecurityEventSink>(),
                sp.GetService<ILogger<ExecApprovalManager>>()));

        // Built-in isolation strategies
        services.AddSingleton<IIsolationStrategy, InProcessIsolationStrategy>();
        services.AddSingleton<IIsolationStrategy, SandboxIsolationStrategy>();
        services.AddSingleton<IIsolationStrategy, ContainerIsolationStrategy>();
        services.AddSingleton<IIsolationStrategy, RemoteIsolationStrategy>();
        services.AddSingleton<IIsolationStrategy, DockerSandboxIsolationStrategy>();
        services.TryAddSingleton<IDockerSandboxRunner, NullDockerSandboxRunner>();

        // SQLite WAL maintenance (#1438 Step 3): shared database registry + network-path detector
        // consumed by the periodic checkpoint hosted service. Stores opt in by registering their
        // resolved on-disk path with the registry as they are wired below.
        services.TryAddSingleton<ISqliteDatabaseRegistry, SqliteDatabaseRegistry>();
        services.TryAddSingleton<INetworkPathDetector>(sp =>
            new NetworkPathDetector(sp.GetRequiredService<IFileSystem>()));

        // Extension state store
        services.TryAddSingleton<IExtensionStateStore>(serviceProvider =>
        {
            var home = serviceProvider.GetRequiredService<BotNexusHome>();
            var dbPath = Path.Combine(home.RootPath, "data", "extension-state.db");
            serviceProvider.GetRequiredService<ISqliteDatabaseRegistry>().Register(dbPath);
            var fs = serviceProvider.GetRequiredService<IFileSystem>();
            var storeLogger = serviceProvider.GetRequiredService<ILogger<SqliteExtensionStateStore>>();
            return new SqliteExtensionStateStore(dbPath, fs, storeLogger);
        });

        // Periodic WAL checkpoint (#1438): PASSIVE on interval, TRUNCATE on shutdown.
        services.AddHostedService<SqliteWalCheckpointHostedService>();

        // Memory pressure diagnostics
        services.AddSingleton<Diagnostics.MemoryPressureMonitor>();
        services.AddHostedService<Diagnostics.MemoryPressureHostedService>();

        // Built-in tools
        services.AddBotNexusTools();

        // Outbound fan-out delivery (#1811): focused collaborator extracted from GatewayHost.
        services.TryAddSingleton<IOutboundResponseDeliverer, OutboundResponseDeliverer>();

        // Live-turn tracker for write-time self-heal of orphaned crash sentinels (#2030).
        // Singleton so GatewayHost shares one view of which sessions have a turn in flight.
        services.TryAddSingleton<Sessions.ISessionTurnTracker, Sessions.SessionTurnTracker>();

        // #2447: startup outcomes are published into a singleton report so the API layer can
        // answer "which configured adapters actually started" without reaching into the host.
        services.TryAddSingleton<BotNexus.Gateway.Channels.Startup.ChannelStartupReport>();

        // Gateway host
        services.TryAddSingleton<GatewayHost>();
        services.TryAddSingleton<IChannelDispatcher>(serviceProvider => serviceProvider.GetRequiredService<GatewayHost>());
        services.TryAddSingleton<IInboundMessageProcessor>(serviceProvider => serviceProvider.GetRequiredService<GatewayHost>());
        services.TryAddSingleton<IInboundMessageOrchestrator>(serviceProvider => serviceProvider.GetRequiredService<GatewayHost>().Orchestrator);
        services.AddSingleton<IHostedService>(serviceProvider => serviceProvider.GetRequiredService<GatewayHost>());
        services.AddSingleton<IHostedService>(serviceProvider =>
            serviceProvider.GetRequiredService<SessionWarmupService>());
        services.AddHostedService(sp => new InterruptedTurnNotificationService(
            sp.GetRequiredService<ISessionStore>(),
            sp.GetRequiredService<IAgentRegistry>(),
            sp.GetRequiredService<IActivityBroadcaster>(),
            sp.GetRequiredService<IChannelManager>(),
            sp.GetRequiredService<ILogger<InterruptedTurnNotificationService>>(),
            sp.GetService<IInboundMessageOrchestrator>(),
            sp.GetService<IOptions<GatewayOptions>>()));
        services.AddHostedService<SessionCleanupService>();
        // Session/conversation consistency monitor + safe auto-heal path (#2046).
        services.TryAddSingleton<Sessions.SessionConsistencyChecker>();
        services.AddHostedService<SessionConsistencyHostedService>();
        services.TryAddSingleton<IConversationChangeNotifier, NullConversationChangeNotifier>();
        services.AddHostedService<ConversationRetentionHostedService>();
        services.AddHostedService<SubAgentWorkspaceSweepHostedService>();
        services.AddHostedService<MemoryIndexer>();

        // #2956: converge memory rows left behind by sessions deleted while the gateway was down
        // (or before the delete path existed). Fails closed on a session-corpus scan error.
        services.TryAddSingleton<MemorySessionReconciler>();
        services.AddHostedService<MemorySessionReconciliationService>();

        // Liveness watchdog: monitors gateway activity and logs warnings on stalls
        services.AddSingleton<IActivityTracker, ActivityTracker>();
        services.AddSingleton<IThreadPoolProbe, ThreadPoolProbe>();
        services.AddHostedService<LivenessWatchdogService>();

        // Satellite registry and stale detection
        services.AddSingleton<ISatelliteRegistry, Satellites.InMemorySatelliteRegistry>();
        services.AddHostedService<Satellites.SatelliteStaleDetectionService>();

        // Auto-update: register once as singleton, expose as interface and hosted service.
        services.AddSingleton<Updates.UpdateCheckService>();
        services.AddSingleton<Updates.IUpdateCheckService>(sp =>
            sp.GetRequiredService<Updates.UpdateCheckService>());
        services.AddHostedService(sp =>
            sp.GetRequiredService<Updates.UpdateCheckService>());

        return services;
    }

    /// <summary>
    /// Loads platform configuration from <c>~/.botnexus/config.json</c> and maps supported settings
    /// into Gateway service registration.
    /// </summary>
    /// <param name="services">Service collection.</param>
    /// <param name="configPath">Optional explicit path to platform config.</param>
    public static IServiceCollection AddPlatformConfiguration(this IServiceCollection services, string? configPath = null, IConfiguration? configuration = null)
    {
        var fileSystem = new FileSystem();
        var resolvedConfigPath = string.IsNullOrWhiteSpace(configPath)
            ? PlatformConfigLoader.GetDefaultConfigPath(fileSystem)
            : Path.GetFullPath(configPath);
        var configDirectory = Path.GetDirectoryName(resolvedConfigPath) ?? PlatformConfigLoader.GetDefaultConfigDirectory(fileSystem);

        PlatformConfigLoader.EnsureConfigDirectory(configDirectory, fileSystem);
        var config = LoadConfigForRegistration(configuration, resolvedConfigPath, fileSystem);

        if (configuration is not null)
        {
            // Bind PlatformConfig from the host IConfiguration root (config.json is already in the pipeline).
            // IOptionsMonitor hot-reload comes free from reloadOnChange: true in Program.cs.
            services.AddOptions<PlatformConfig>().Bind(configuration);
            services.AddSingleton<IPostConfigureOptions<PlatformConfig>>(sp =>
                new PlatformConfigPostConfigure(sp.GetRequiredService<IConfiguration>(), resolvedConfigPath));
            services.AddSingleton<IValidateOptions<PlatformConfig>, PlatformConfigOptionsValidator>();
        }
        else
        {
            // Fallback when IConfiguration is not threaded in (e.g. tests or CLI-only usage).
            // Use a manual load + PostConfigure without hot reload.
            services.AddOptions<PlatformConfig>()
                .Configure(options =>
                {
                    var freshConfig = PlatformConfigLoader.Load(resolvedConfigPath, fileSystem: fileSystem);
                    ApplyPlatformConfig(options, freshConfig);
                });
        }

        services.TryAddSingleton<GatewayAuthManager>();
        services.TryAddSingleton<ILocationResolver>(serviceProvider =>
            new DefaultLocationResolver(
                serviceProvider.GetRequiredService<IOptionsMonitor<PlatformConfig>>(),
                serviceProvider.GetService<IAgentRegistry>(),
                serviceProvider.GetServices<IIsolationStrategy>()));
        services.Replace(ServiceDescriptor.Singleton<IGatewayAuthHandler>(serviceProvider =>
            new ApiKeyGatewayAuthHandler(
                serviceProvider.GetRequiredService<IOptionsMonitor<PlatformConfig>>(),
                serviceProvider.GetRequiredService<ILogger<ApiKeyGatewayAuthHandler>>(),
                serviceProvider.GetService<ISecurityEventSink>(),
                serviceProvider.GetService<IFeatureManager>())));

        var defaultAgentId = config.Gateway?.DefaultAgentId;
        if (!string.IsNullOrWhiteSpace(defaultAgentId))
        {
            services.PostConfigure<GatewayOptions>(options => options.DefaultAgentId = defaultAgentId);
        }
        if (config.Gateway?.Compaction is { } compaction)
        {
            services.AddSingleton<IOptions<CompactionOptions>>(_ => Options.Create(compaction));
            services.Replace(ServiceDescriptor.Singleton<IOptionsMonitor<CompactionOptions>>(
                _ => new StaticOptionsMonitor<CompactionOptions>(compaction)));
        }

        ConfigureSessionStore(services, config, configDirectory);
        ConfigureConversationStore(services, config, configDirectory);

        services.AddSingleton<IAgentConfigurationSource>(serviceProvider =>
            new PlatformConfigAgentSource(
                serviceProvider.GetRequiredService<IOptionsMonitor<PlatformConfig>>(),
                configDirectory,
                serviceProvider.GetRequiredService<ILogger<PlatformConfigAgentSource>>(),
                serviceProvider.GetRequiredService<ILocationResolver>(),
                serviceProvider.GetService<BotNexus.Agent.Providers.Core.Registry.ModelRegistry>(),
                serviceProvider.GetService<BotNexus.Gateway.Telemetry.IMetrics>()));
        services.Replace(ServiceDescriptor.Singleton(serviceProvider =>
            CreatePlatformConfigWriter(
                resolvedConfigPath,
                serviceProvider.GetRequiredService<IFileSystem>())));

        // #2834: the world identity is resolved EXACTLY ONCE, here, against the same resolved config
        // path the writer uses. Both the injected value and the bootstrap write below consume this
        // single resolution - nothing re-derives it - so a broken resolver cannot produce two
        // independently-wrong answers that agree with each other.
        var worldId = WorldIdResolver.Resolve(resolvedConfigPath, fileSystem, out var worldIdGenerated);
        services.Replace(ServiceDescriptor.Singleton(worldId));
        services.Replace(ServiceDescriptor.Singleton(new WorldIdOrigin(worldIdGenerated)));
        services.AddHostedService<WorldIdPersistenceService>();

        // #2833: hand that SAME resolved value to the SQLite connection seam, so every store this
        // process opens is stamped with - and verified against - this world. Installed here rather
        // than from a hosted service because stores are opened during registration and early
        // startup; a guard that arrives after the first open has already missed the failure it
        // exists to catch. The logger is attached later (see SetLogger) since the logging pipeline
        // does not exist yet at this point.
        SqliteStoreIdentityGuard.Configure(
            new SqliteStoreIdentity(worldId.Value, configDirectory));
        services.Replace(ServiceDescriptor.Singleton<IAgentConfigurationWriter>(serviceProvider =>
        {
            var home = serviceProvider.GetRequiredService<BotNexusHome>();
            var writer = serviceProvider.GetRequiredService<PlatformConfigWriter>();
            return new PlatformConfigAgentWriter(writer, home);
        }));
        // Config hydration — populate missing keys with defaults on startup
        services.AddSingleton<IConfigSchemaContributor, GatewaySchemaContributor>();
        services.AddSingleton<IConfigSchemaContributor, CompactionSchemaContributor>();
        services.AddSingleton<IConfigSchemaContributor, AuxiliarySchemaContributor>();
        services.AddSingleton<IConfigSchemaContributor, AutoUpdateSchemaContributor>();
        services.AddSingleton<IConfigSchemaContributor, CronSchemaContributor>();
        services.AddSingleton<IConfigSchemaContributor, SessionStoreSchemaContributor>();
        services.AddSingleton<IConfigSchemaContributor, RateLimitSchemaContributor>();
        services.AddHostedService<ConfigHydrationService>();

        // #2646 PBI 2 / #2766: the configuration shadow migration. Placed AFTER
        // ConfigHydrationService because hydration writes defaults into config.json, and a shadow pass
        // that ran first would be comparing against a document the platform is about to modify.
        //
        // The whole path is inert until ConfigStoreShadowMigration is enabled, and the flag defaults
        // off: with it off the service returns before it reads anything at all.
        //
        // Nothing here can change which configuration the gateway serves. The store is written to and
        // read back purely to be diffed; ConfigStoreAuthoritative - the flag that would put it in the
        // read path - is not consumed by any of these registrations.
        services.TryAddSingleton<IConfigShadowReportSink, ConfigShadowReportSink>();
        services.TryAddSingleton<IConfigShadowGate, FeatureManagerConfigShadowGate>();
        services.TryAddSingleton<IConfigShadowSource>(sp =>
            new FileConfigShadowSource(sp.GetRequiredService<IFileSystem>()));
        services.TryAddSingleton<IConfigStore>(sp =>
        {
            // Sits beside config.json rather than in a separate location, so "delete the store to roll
            // back" is an obvious operation for whoever needs it at 3am.
            var fs = sp.GetRequiredService<IFileSystem>();
            var directory = PlatformConfigLoader.GetDefaultConfigDirectory(fs);
            return new SqliteConfigStore($"Data Source={Path.Combine(directory, "config.db")}");
        });
        services.TryAddSingleton<IConfigStoreEntryRoundTrip>(sp =>
            new ConfigStoreRoundTrip(sp.GetRequiredService<IConfigStore>()));
        // The document-shaped seam is unused once the entry-shaped one is registered, but the hosted
        // service takes it as a required constructor argument, so a no-op keeps the graph resolvable
        // without inventing a second real implementation.
        services.TryAddSingleton<IConfigStoreRoundTrip, NoOpConfigStoreRoundTrip>();
        services.AddHostedService<ConfigShadowMigrationHostedService>();

        // #2646 PBI 3: the store-backed read path. Registered but NOT yet consumed by
        // PlatformConfigLoader - this PBI builds and proves the seam; replacing the loader's own read
        // is a separate change with a far larger blast radius, and doing both at once would make a
        // regression impossible to attribute to either.
        //
        // ConfigStoreAuthoritative gates it and defaults off, so with the flag unset this resolves to a
        // source that reads the file and never opens the store.
        services.TryAddSingleton<IConfigStoreAuthoritativeGate, FeatureManagerConfigStoreAuthoritativeGate>();
        services.TryAddSingleton<IConfigDocumentSource, StoreBackedConfigDocumentSource>();

        // #2635: additively reconcile the bundled agent catalog into config.json. Registered
        // HERE, ahead of AgentConfigurationHostedService below, so an entry inserted on this
        // startup is visible to the config agent source in the same startup rather than only
        // after the next restart. Hosted services start in registration order, so this ordering
        // is the mechanism, not a comment about one.
        services.AddHostedService(serviceProvider => PlatformAgentReconciliationService.Create(
            serviceProvider.GetRequiredService<BotNexusHome>(),
            serviceProvider.GetRequiredService<IFileSystem>(),
            serviceProvider.GetRequiredService<ILogger<PlatformAgentReconciliationService>>()));

        // #2136: the six worker archetypes (researcher, coder, planner, reviewer, writer, analyst)
        // are no longer registered as named conversational agents. They are resolved at spawn time
        // from BuiltInArchetypes, cloning the parent descriptor and applying the archetype tool set.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, AgentConfigurationHostedService>());

        return services;
    }

    private static string ResolveConfiguredPath(string configDirectory, string configuredPath)
        => Path.IsPathRooted(configuredPath)
            ? Path.GetFullPath(configuredPath)
            : Path.GetFullPath(Path.Combine(configDirectory, configuredPath));

    private static void ApplyPlatformConfig(PlatformConfig target, PlatformConfig source)
    {
        target.Gateway = source.Gateway;
        target.Agents = source.Agents;
        target.Providers = source.Providers;
        target.Channels = source.Channels;
        target.ApiKey = source.ApiKey;
        target.Cron = source.Cron;
    }

    private static PlatformConfig LoadConfigForRegistration(IConfiguration? configuration, string resolvedConfigPath, IFileSystem fileSystem)
    {
        if (configuration is null)
            return PlatformConfigLoader.Load(resolvedConfigPath, fileSystem: fileSystem);

        var config = new PlatformConfig();
        configuration.Bind(config);
        var rawJson = TryReadConfigFile(resolvedConfigPath, fileSystem);
        if (!string.IsNullOrWhiteSpace(rawJson))
        {
            // A malformed config.json must not abort registration. Program.cs already guards the
            // IConfiguration pipeline, but the legacy-migration and agent-defaults extraction below
            // re-parse the raw file directly and would throw on invalid JSON. Fall back to the
            // already-bound config (defaults + any valid IConfiguration sources) on parse failure.
            try
            {
                PlatformConfigLoader.MigrateLegacyGatewaySettings(config, rawJson);
                PlatformConfigLoader.ExtractAgentDefaults(config, rawJson);
            }
            catch (JsonException)
            {
                // Invalid JSON — keep the bound config and let the gateway start on defaults.
            }
        }

        return config;
    }

    private static string? TryReadConfigFile(string path, IFileSystem fileSystem)
    {
        try
        {
            return fileSystem.File.Exists(path)
                ? fileSystem.File.ReadAllText(path)
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static PlatformConfigWriter CreatePlatformConfigWriter(string configPath, IFileSystem fileSystem)
    {
        var directory = Path.GetDirectoryName(configPath) ?? PlatformConfigLoader.GetDefaultConfigDirectory(fileSystem);
        var backup = new ConfigBackupService(Path.Combine(directory, "backups"), fileSystem);
        return new PlatformConfigWriter(configPath, fileSystem, backup);
    }

    /// <summary>
    /// Extracts the on-disk data-source path from a SQLite connection string and registers it with
    /// the shared <see cref="ISqliteDatabaseRegistry"/> so the periodic WAL checkpoint service (#1438)
    /// includes it in each sweep. Best-effort: an unparseable connection string is skipped silently.
    /// </summary>
    private static void RegisterSqliteDatabasePath(IServiceProvider serviceProvider, string connectionString)
    {
        try
        {
            var dataSource = new SqliteConnectionStringBuilder(connectionString).DataSource;
            if (!string.IsNullOrWhiteSpace(dataSource))
            {
                serviceProvider.GetRequiredService<ISqliteDatabaseRegistry>().Register(dataSource);
            }
        }
        catch
        {
            // Non-fatal: a connection string we cannot parse simply is not checkpointed.
        }
    }

    private static int ParseInt(string? value, int defaultValue)
        => int.TryParse(value, out var parsed) ? parsed : defaultValue;

    private static double ParseDouble(string? value, double defaultValue)
        => double.TryParse(value, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : defaultValue;

    private static string? ParseString(string? value, string? defaultValue)
        => string.IsNullOrWhiteSpace(value) ? defaultValue : value;

    private sealed class StaticOptionsMonitor<TOptions>(TOptions currentValue) : IOptionsMonitor<TOptions>
    {
        public TOptions CurrentValue { get; } = currentValue;

        public TOptions Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<TOptions, string?> listener) => null;
    }

    private static void ConfigureSessionStore(IServiceCollection services, PlatformConfig config, string configDirectory)
    {
        var sessionStore = config.Gateway?.SessionStore;
        var explicitType = sessionStore?.Type?.Trim();
        var sessionsDirectory = config.Gateway?.SessionsDirectory;
        var resolvedType = !string.IsNullOrWhiteSpace(explicitType)
            ? explicitType
            : !string.IsNullOrWhiteSpace(sessionsDirectory)
                ? "File"
                : "Sqlite"; // Default to SQLite — InMemory loses all data on restart

        // Writable runtime-state directory. Honors BOTNEXUS_DATA_DIR (set in the container image)
        // so the SQLite/File session store lands on a writable volume even when the config
        // directory is mounted read-only. Falls back to the config directory for local installs
        // where the two are the same.
        var dataDirectory = BotNexusHome.ResolveDataPath() ?? configDirectory;

        if (resolvedType.Equals("InMemory", StringComparison.OrdinalIgnoreCase))
        {
            // Phase 9 / P9-B (#615): thread the conversation store so save-time legacy
            // backfill applies in InMemory test/dev deployments too.
            services.Replace(ServiceDescriptor.Singleton<ISessionStore>(serviceProvider =>
                AttachArchiveDrain(
                    new InMemorySessionStore(
                        redactor: serviceProvider.GetService<ISecretRedactor>(),
                        conversationStore: serviceProvider.GetService<IConversationStore>(),
                        logger: serviceProvider.GetService<ILogger<InMemorySessionStore>>()),
                    serviceProvider)));
            return;
        }

        if (resolvedType.Equals("File", StringComparison.OrdinalIgnoreCase))
        {
            var configuredPath = sessionStore?.FilePath ?? sessionsDirectory;
            if (string.IsNullOrWhiteSpace(configuredPath))
                throw new OptionsValidationException(nameof(PlatformConfig), typeof(PlatformConfig), ["gateway.sessionStore.filePath is required when gateway.sessionStore.type is 'File'."]);

            // Resolve relative paths against the writable data directory so the default lands on
            // a writable volume; absolute user-configured paths are honored as-is.
            var sessionsPath = ResolveConfiguredPath(dataDirectory, configuredPath);
            services.Replace(ServiceDescriptor.Singleton<ISessionStore>(serviceProvider =>
            {
                var fs = serviceProvider.GetRequiredService<IFileSystem>();
                fs.Directory.CreateDirectory(sessionsPath);
                return AttachArchiveDrain(
                    new FileSessionStore(
                        sessionsPath,
                        serviceProvider.GetRequiredService<ILogger<FileSessionStore>>(),
                        fs,
                        conversationStore: serviceProvider.GetRequiredService<IConversationStore>(),
                        redactor: serviceProvider.GetService<ISecretRedactor>()),
                    serviceProvider);
            }));
            return;
        }

        if (resolvedType.Equals("Sqlite", StringComparison.OrdinalIgnoreCase))
        {
            // Use explicit connection string, or default to sessions.sqlite in the writable data
            // directory (BOTNEXUS_DATA_DIR) so it works even when the config dir is read-only.
            var connectionString = !string.IsNullOrWhiteSpace(sessionStore?.ConnectionString)
                ? sessionStore!.ConnectionString!
                : $"Data Source={Path.Combine(dataDirectory, "sessions.sqlite")}";

            services.Replace(ServiceDescriptor.Singleton<ISessionStore>(serviceProvider =>
            {
                RegisterSqliteDatabasePath(serviceProvider, connectionString);
                return AttachArchiveDrain(
                    new SqliteSessionStore(
                        connectionString,
                        serviceProvider.GetRequiredService<ILogger<SqliteSessionStore>>(),
                        serviceProvider.GetRequiredService<IConversationStore>()),
                    serviceProvider);
            }));
            return;
        }

        throw new OptionsValidationException(nameof(PlatformConfig), typeof(PlatformConfig), ["gateway.sessionStore.type must be either 'InMemory', 'File', or 'Sqlite'."]);
    }

    /// <summary>
    /// Wires the archive run-fence (#2903) onto a freshly constructed store. Done here, at the
    /// composition root, rather than through the store constructors: the fence needs
    /// <c>IAgentSupervisor</c>, which needs <c>ISessionStore</c>, so a constructor parameter would
    /// be an unresolvable cycle. <see cref="SupervisorSessionRunDrain"/> resolves the supervisor
    /// lazily at drain time, so attaching it here costs nothing until an archive actually runs.
    /// </summary>
    private static TStore AttachArchiveDrain<TStore>(TStore store, IServiceProvider serviceProvider)
        where TStore : SessionStoreBase
    {
        store.ConfigureArchiveDrain(serviceProvider.GetService<ISessionRunDrain>());
        return store;
    }

    private static void ConfigureConversationStore(IServiceCollection services, PlatformConfig config, string configDirectory)
    {
        var sessionStore = config.Gateway?.SessionStore;
        var explicitType = sessionStore?.Type?.Trim();
        var sessionsDirectory = config.Gateway?.SessionsDirectory;
        var resolvedType = !string.IsNullOrWhiteSpace(explicitType)
            ? explicitType
            : !string.IsNullOrWhiteSpace(sessionsDirectory)
                ? "File"
                : "Sqlite"; // Default to SQLite — InMemory loses all data on restart

        // Writable runtime-state directory (BOTNEXUS_DATA_DIR), mirroring ConfigureSessionStore,
        // so conversation persistence survives a read-only config mount.
        var dataDirectory = BotNexusHome.ResolveDataPath() ?? configDirectory;

        if (resolvedType.Equals("InMemory", StringComparison.OrdinalIgnoreCase))
        {
            services.Replace(ServiceDescriptor.Singleton<IConversationStore, InMemoryConversationStore>());
            services.Replace(ServiceDescriptor.Singleton<IConversationSectionStore, InMemoryConversationSectionStore>());
            return;
        }

        if (resolvedType.Equals("File", StringComparison.OrdinalIgnoreCase))
        {
            var configuredPath = sessionStore?.FilePath ?? sessionsDirectory;
            if (string.IsNullOrWhiteSpace(configuredPath))
                throw new OptionsValidationException(nameof(PlatformConfig), typeof(PlatformConfig), ["gateway.sessionStore.filePath is required when gateway.sessionStore.type is 'File'."]);

            var conversationsPath = Path.Combine(ResolveConfiguredPath(dataDirectory, configuredPath), "conversations");
            services.Replace(ServiceDescriptor.Singleton<IConversationStore>(serviceProvider =>
            {
                var fs = serviceProvider.GetRequiredService<IFileSystem>();
                fs.Directory.CreateDirectory(conversationsPath);
                return new FileConversationStore(
                    conversationsPath,
                    serviceProvider.GetRequiredService<ILogger<FileConversationStore>>(),
                    fs,
                    serviceProvider.GetService<IWorldContext>());
            }));
            services.Replace(ServiceDescriptor.Singleton<IConversationSectionStore>(serviceProvider =>
                new SqliteConversationSectionStore(
                    $"Data Source={Path.Combine(dataDirectory, "sections.sqlite")}",
                    serviceProvider.GetRequiredService<ILogger<SqliteConversationSectionStore>>(),
                    serviceProvider.GetService<IWorldContext>())));
            return;
        }

        if (resolvedType.Equals("Sqlite", StringComparison.OrdinalIgnoreCase))
        {
            var connectionString = !string.IsNullOrWhiteSpace(sessionStore?.ConnectionString)
                ? sessionStore!.ConnectionString!
                : $"Data Source={Path.Combine(dataDirectory, "sessions.sqlite")}";

            services.Replace(ServiceDescriptor.Singleton<IConversationStore>(serviceProvider =>
            {
                RegisterSqliteDatabasePath(serviceProvider, connectionString);
                return new SqliteConversationStore(
                    connectionString,
                    serviceProvider.GetRequiredService<ILogger<SqliteConversationStore>>(),
                    serviceProvider.GetService<IWorldContext>());
            }));

            services.AddSingleton<IConversationAuditLog>(
                new SqliteConversationAuditLog(connectionString));
            services.Replace(ServiceDescriptor.Singleton<IConversationSectionStore>(serviceProvider =>
                new SqliteConversationSectionStore(
                    connectionString,
                    serviceProvider.GetRequiredService<ILogger<SqliteConversationSectionStore>>(),
                    serviceProvider.GetService<IWorldContext>())));
            return;
        }

        throw new OptionsValidationException(nameof(PlatformConfig), typeof(PlatformConfig), ["gateway.sessionStore.type must be either 'InMemory', 'File', or 'Sqlite'."]);
    }

    /// <summary>
    /// Sets the default routed agent through options configuration.
    /// </summary>
    /// <param name="services">Service collection.</param>
    /// <param name="agentId">Default agent ID to route to.</param>
    public static IServiceCollection SetDefaultAgent(this IServiceCollection services, string agentId)
    {
        ArgumentNullException.ThrowIfNull(agentId);
        services.PostConfigure<GatewayOptions>(options => options.DefaultAgentId = agentId);
        return services;
    }

    /// <summary>
    /// Registers an agent configuration source and ensures configuration-driven agent loading is hosted.
    /// </summary>
    /// <typeparam name="T">The configuration source type.</typeparam>
    /// <param name="services">Service collection.</param>
    public static IServiceCollection AddAgentConfigurationSource<T>(this IServiceCollection services)
        where T : class, IAgentConfigurationSource
    {
        services.AddSingleton<IAgentConfigurationSource, T>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, AgentConfigurationHostedService>());
        return services;
    }

}
