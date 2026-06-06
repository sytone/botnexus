using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Conversations;
using BotNexus.Gateway.Dispatching;
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
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Activity;
using BotNexus.Gateway.Agents;
using BotNexus.Gateway.Citizens;
using BotNexus.Gateway.Configuration;
using BotNexus.Gateway.Commands;
using BotNexus.Gateway.Hooks;
using BotNexus.Gateway.Isolation;
using BotNexus.Gateway.Media;
using BotNexus.Gateway.Routing;
using BotNexus.Gateway.Services;
using BotNexus.Gateway.Sessions;
using BotNexus.Gateway.Security;
using BotNexus.Gateway.Federation;
using BotNexus.Gateway.Channels;
using BotNexus.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
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
        services.AddOptions<SessionWarmupOptions>();
        services.AddOptions<DelayToolOptions>();
        services.AddOptions<FileWatcherToolOptions>();
        services.AddOptions<CompactionOptions>();
        if (configure is not null)
            services.Configure(configure);
        if (config is not null)
        {
            services.Configure<GatewayOptions>(config.GetSection("gateway"));
            services.Configure<SessionWarmupOptions>(config.GetSection("gateway:sessionWarmup"));
            services.Configure<SubAgentOptions>(config.GetSection("gateway:subAgents"));
            services.Configure<DelayToolOptions>(config.GetSection("gateway:delayTool"));
            services.Configure<FileWatcherToolOptions>(config.GetSection("gateway:fileWatcherTool"));
            services.Configure<ConversationRetentionOptions>(config.GetSection("gateway:conversations"));

            var compactionSection = config.GetSection("gateway:compaction");
            if (compactionSection.Exists())
            {
                var configuredCompaction = new CompactionOptions
                {
                    PreservedTurns = ParseInt(compactionSection["preservedTurns"], new CompactionOptions().PreservedTurns),
                    MaxSummaryChars = ParseInt(compactionSection["maxSummaryChars"], new CompactionOptions().MaxSummaryChars),
                    TokenThresholdRatio = ParseDouble(compactionSection["tokenThresholdRatio"], new CompactionOptions().TokenThresholdRatio),
                    ContextWindowTokens = ParseInt(compactionSection["contextWindowTokens"], new CompactionOptions().ContextWindowTokens),
                    SummarizationModel = ParseString(compactionSection["summarizationModel"], new CompactionOptions().SummarizationModel),
                    SummarizationProvider = ParseString(compactionSection["summarizationProvider"], new CompactionOptions().SummarizationProvider)
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
            return new MemoryStoreFactory(agentId =>
            {
                var agentDirectory = home.GetAgentDirectory(agentId);
                return Path.Combine(agentDirectory, "data", "memory.sqlite");
            });
        });
        services.AddSingleton<IAgentWorkspaceManager, FileAgentWorkspaceManager>();
         services.AddSingleton<IContextBuilder, WorkspaceContextBuilder>();
         services.AddSingleton<IAgentRegistry, DefaultAgentRegistry>();
         services.AddSingleton<IUserRegistry, DefaultUserRegistry>();
         services.AddSingleton<ICitizenRegistry, DefaultCitizenRegistry>();
         services.TryAddSingleton<IAgentConfigurationWriter, NoOpAgentConfigurationWriter>();
        services.AddSingleton<IAgentSupervisor, DefaultAgentSupervisor>();
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
        services.TryAddSingleton<ISessionWarmupService>(serviceProvider =>
            serviceProvider.GetRequiredService<SessionWarmupService>());
        services.AddSingleton<IMessageRouter, DefaultMessageRouter>();
        services.AddSingleton<IConfigPathResolver, ConfigPathResolver>();
        services.TryAddSingleton<IChannelManager, ChannelManager>();
        services.TryAddSingleton<ISessionStore, InMemorySessionStore>();
        services.TryAddSingleton<ISessionWriteLock, SessionWriteLock>();
        services.TryAddSingleton<IConversationStore, InMemoryConversationStore>();
        services.TryAddSingleton<IAgentIdentityResolver, AgentIdentityResolver>();
        services.AddSingleton<IAgentCanvasNotifier, ConversationCanvasNotifier>();
        services.TryAddSingleton<IConversationRouter, DefaultConversationRouter>();
        services.TryAddSingleton<IConversationDispatcher, DefaultConversationDispatcher>();
        services.TryAddSingleton<IAskUserResponseRegistry, AskUserResponseRegistry>();
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
        services.AddSingleton<IGatewayAuthHandler, ApiKeyGatewayAuthHandler>();
        services.AddSingleton<IModelFilter, ConfigModelFilter>();

        // Hook dispatcher with built-in handler registration
        services.TryAddSingleton<IHookDispatcher>(sp =>
        {
            var dispatcher = new HookDispatcher();
            dispatcher.Register<BeforeToolCallEvent, BeforeToolCallResult>(
                sp.GetRequiredService<ToolPolicyHookHandler>());
            dispatcher.Register<BeforePromptBuildEvent, BeforePromptBuildResult>(
                sp.GetRequiredService<AgentsMdPromptHookHandler>());
            return dispatcher;
        });

        // Tool policy
        services.TryAddSingleton<DefaultToolPolicyProvider>();
        services.TryAddSingleton<IToolPolicyProvider>(sp => sp.GetRequiredService<DefaultToolPolicyProvider>());
        services.AddSingleton<ToolPolicyHookHandler>();
        services.AddSingleton<AgentsMdPromptHookHandler>();
        services.TryAddSingleton<ISecretRedactor, SecretRedactor>();

        // Built-in isolation strategies
        services.AddSingleton<IIsolationStrategy, InProcessIsolationStrategy>();
        services.AddSingleton<IIsolationStrategy, SandboxIsolationStrategy>();
        services.AddSingleton<IIsolationStrategy, ContainerIsolationStrategy>();
        services.AddSingleton<IIsolationStrategy, RemoteIsolationStrategy>();

        // Built-in tools
        services.AddBotNexusTools();

        // Gateway host
        services.TryAddSingleton<GatewayHost>();
        services.TryAddSingleton<IChannelDispatcher>(serviceProvider => serviceProvider.GetRequiredService<GatewayHost>());
        services.TryAddSingleton<IInboundMessageProcessor>(serviceProvider => serviceProvider.GetRequiredService<GatewayHost>());
        services.TryAddSingleton<IInboundMessageOrchestrator>(serviceProvider => serviceProvider.GetRequiredService<GatewayHost>().Orchestrator);
        services.AddSingleton<IHostedService>(serviceProvider => serviceProvider.GetRequiredService<GatewayHost>());
        services.AddSingleton<IHostedService>(serviceProvider =>
            serviceProvider.GetRequiredService<SessionWarmupService>());
        services.AddHostedService<InterruptedTurnNotificationService>();
        services.AddHostedService<SessionCleanupService>();
        services.AddHostedService<ConversationRetentionHostedService>();
        services.AddHostedService<MemoryIndexer>();

        // Auto-update: register once as singleton, expose as interface and hosted service.
        services.AddSingleton<Updates.UpdateCheckService>();
        services.AddSingleton<Updates.IUpdateCheckService>(sp =>
            sp.GetRequiredService<Updates.UpdateCheckService>());
        services.AddHostedService(sp =>
            sp.GetRequiredService<Updates.UpdateCheckService>());

        // Default agent configuration from BotNexusHome (~/.botnexus/agents/)
        // This ensures agents created via the API are always persisted to disk.
        // Platform config can override this with an explicit agentsDirectory.
        if (services.All(descriptor => descriptor.ServiceType != typeof(IAgentConfigurationSource)))
        {
            services.AddSingleton<IAgentConfigurationSource>(serviceProvider =>
            {
                var home = serviceProvider.GetRequiredService<BotNexusHome>();
                home.Initialize();
                return new FileAgentConfigurationSource(
                    home.AgentsPath,
                    serviceProvider.GetRequiredService<ILogger<FileAgentConfigurationSource>>(),
                    serviceProvider.GetRequiredService<IFileSystem>());
            });
            services.Replace(ServiceDescriptor.Singleton<IAgentConfigurationWriter>(serviceProvider =>
            {
                var home = serviceProvider.GetRequiredService<BotNexusHome>();
                var defaultConfigPath = Path.Combine(home.RootPath, "config.json");
                var fileSystem = serviceProvider.GetRequiredService<IFileSystem>();
                var writer = CreatePlatformConfigWriter(defaultConfigPath, fileSystem);
                return new PlatformConfigAgentWriter(writer, home);
            }));
            services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, AgentConfigurationHostedService>());
        }

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
                serviceProvider.GetRequiredService<ILogger<ApiKeyGatewayAuthHandler>>())));

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

        var agentsDirectory = config.Gateway?.AgentsDirectory;
        if (!string.IsNullOrWhiteSpace(agentsDirectory))
        {
            var agentsPath = ResolveConfiguredPath(configDirectory, agentsDirectory);
            services.RemoveAll<IAgentConfigurationSource>();
            services.AddFileAgentConfiguration(agentsPath);
        }

        services.AddSingleton<IAgentConfigurationSource>(serviceProvider =>
            new PlatformConfigAgentSource(
                serviceProvider.GetRequiredService<IOptionsMonitor<PlatformConfig>>(),
                configDirectory,
                serviceProvider.GetRequiredService<ILogger<PlatformConfigAgentSource>>(),
                serviceProvider.GetRequiredService<ILocationResolver>()));
        services.Replace(ServiceDescriptor.Singleton(serviceProvider =>
            CreatePlatformConfigWriter(
                resolvedConfigPath,
                serviceProvider.GetRequiredService<IFileSystem>())));
        services.Replace(ServiceDescriptor.Singleton<IAgentConfigurationWriter>(serviceProvider =>
        {
            var home = serviceProvider.GetRequiredService<BotNexusHome>();
            var writer = serviceProvider.GetRequiredService<PlatformConfigWriter>();
            return new PlatformConfigAgentWriter(writer, home);
        }));
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
            PlatformConfigLoader.MigrateLegacyGatewaySettings(config, rawJson);
            PlatformConfigLoader.ExtractAgentDefaults(config, rawJson);
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

        if (resolvedType.Equals("InMemory", StringComparison.OrdinalIgnoreCase))
        {
            // Phase 9 / P9-B (#615): thread the conversation store so save-time legacy
            // backfill applies in InMemory test/dev deployments too.
            services.Replace(ServiceDescriptor.Singleton<ISessionStore>(serviceProvider =>
                new InMemorySessionStore(
                    redactor: serviceProvider.GetService<ISecretRedactor>(),
                    conversationStore: serviceProvider.GetService<IConversationStore>(),
                    logger: serviceProvider.GetService<ILogger<InMemorySessionStore>>())));
            return;
        }

        if (resolvedType.Equals("File", StringComparison.OrdinalIgnoreCase))
        {
            var configuredPath = sessionStore?.FilePath ?? sessionsDirectory;
            if (string.IsNullOrWhiteSpace(configuredPath))
                throw new OptionsValidationException(nameof(PlatformConfig), typeof(PlatformConfig), ["gateway.sessionStore.filePath is required when gateway.sessionStore.type is 'File'."]);

            var sessionsPath = ResolveConfiguredPath(configDirectory, configuredPath);
            services.Replace(ServiceDescriptor.Singleton<ISessionStore>(serviceProvider =>
            {
                var fs = serviceProvider.GetRequiredService<IFileSystem>();
                fs.Directory.CreateDirectory(sessionsPath);
                return new FileSessionStore(
                    sessionsPath,
                    serviceProvider.GetRequiredService<ILogger<FileSessionStore>>(),
                    fs,
                    conversationStore: serviceProvider.GetRequiredService<IConversationStore>(),
                    redactor: serviceProvider.GetService<ISecretRedactor>());
            }));
            return;
        }

        if (resolvedType.Equals("Sqlite", StringComparison.OrdinalIgnoreCase))
        {
            // Use explicit connection string, or default to sessions.sqlite in the config directory
            var connectionString = !string.IsNullOrWhiteSpace(sessionStore?.ConnectionString)
                ? sessionStore!.ConnectionString!
                : $"Data Source={Path.Combine(configDirectory, "sessions.sqlite")}";

            services.Replace(ServiceDescriptor.Singleton<ISessionStore>(serviceProvider =>
                new SqliteSessionStore(
                    connectionString,
                    serviceProvider.GetRequiredService<ILogger<SqliteSessionStore>>(),
                    serviceProvider.GetRequiredService<IConversationStore>())));
            return;
        }

        throw new OptionsValidationException(nameof(PlatformConfig), typeof(PlatformConfig), ["gateway.sessionStore.type must be either 'InMemory', 'File', or 'Sqlite'."]);
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

        if (resolvedType.Equals("InMemory", StringComparison.OrdinalIgnoreCase))
        {
            services.Replace(ServiceDescriptor.Singleton<IConversationStore, InMemoryConversationStore>());
            return;
        }

        if (resolvedType.Equals("File", StringComparison.OrdinalIgnoreCase))
        {
            var configuredPath = sessionStore?.FilePath ?? sessionsDirectory;
            if (string.IsNullOrWhiteSpace(configuredPath))
                throw new OptionsValidationException(nameof(PlatformConfig), typeof(PlatformConfig), ["gateway.sessionStore.filePath is required when gateway.sessionStore.type is 'File'."]);

            var conversationsPath = Path.Combine(ResolveConfiguredPath(configDirectory, configuredPath), "conversations");
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
            return;
        }

        if (resolvedType.Equals("Sqlite", StringComparison.OrdinalIgnoreCase))
        {
            var connectionString = !string.IsNullOrWhiteSpace(sessionStore?.ConnectionString)
                ? sessionStore!.ConnectionString!
                : $"Data Source={Path.Combine(configDirectory, "sessions.sqlite")}";

            services.Replace(ServiceDescriptor.Singleton<IConversationStore>(serviceProvider =>
                new SqliteConversationStore(
                    connectionString,
                    serviceProvider.GetRequiredService<ILogger<SqliteConversationStore>>(),
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

    /// <summary>
    /// Registers a file-based agent configuration source.
    /// </summary>
    /// <param name="services">Service collection.</param>
    /// <param name="path">Directory containing agent configuration files.</param>
    public static IServiceCollection AddFileAgentConfiguration(this IServiceCollection services, string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        string ResolvePath(IServiceProvider serviceProvider)
        {
            var hostEnvironment = serviceProvider.GetService<IHostEnvironment>();
            return Path.IsPathRooted(path)
                ? path
                : Path.GetFullPath(Path.Combine(hostEnvironment?.ContentRootPath ?? AppContext.BaseDirectory, path));
        }

        services.AddSingleton<IAgentConfigurationSource>(serviceProvider =>
        {
            return new FileAgentConfigurationSource(
                ResolvePath(serviceProvider),
                serviceProvider.GetRequiredService<ILogger<FileAgentConfigurationSource>>(),
                serviceProvider.GetRequiredService<IFileSystem>());
        });
        services.Replace(ServiceDescriptor.Singleton<IAgentConfigurationWriter>(serviceProvider =>
            new FileAgentConfigurationWriter(
                ResolvePath(serviceProvider),
                serviceProvider.GetRequiredService<BotNexusHome>(),
                serviceProvider.GetRequiredService<IFileSystem>())));

        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, AgentConfigurationHostedService>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, ConfigNormalisationHostedService>());
        return services;
    }
}
