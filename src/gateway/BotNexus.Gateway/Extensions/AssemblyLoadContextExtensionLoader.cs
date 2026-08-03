using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using System.IO.Abstractions;
using BotNexus.Agent.Core.Tools;
using BotNexus.Gateway.Abstractions.Activity;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Abstractions.Extensions;
using BotNexus.Gateway.Abstractions.Hooks;
using BotNexus.Gateway.Abstractions.Isolation;
using BotNexus.Gateway.Abstractions.Media;
using BotNexus.Gateway.Abstractions.Routing;
using BotNexus.Gateway.Abstractions.Security;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Channels.Startup;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BotNexus.Gateway.Extensions;

public sealed class AssemblyLoadContextExtensionLoader : IExtensionLoader
{
    private static readonly JsonSerializerOptions ManifestJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly Type[] DiscoverableServiceContracts =
    [
        typeof(IChannelAdapter),
        typeof(IIsolationStrategy),
        typeof(ISessionStore),
        typeof(IGatewayAuthHandler),
        typeof(IMessageRouter),
        typeof(IAgentRegistry),
        typeof(IAgentSupervisor),
        typeof(IAgentChangeNotifier),
        typeof(IConversationChangeNotifier),
        typeof(IAgentCanvasNotifier),
        typeof(IAgentTodoNotifier),
        typeof(IAgentToolContributor),
        typeof(IActivityBroadcaster),
        typeof(IAgentTool),
        typeof(ICommandContributor),
        typeof(IMediaHandler),
        typeof(IEndpointContributor),
        typeof(IApiContributor),
        typeof(IHostedService)
    ];

    private readonly IServiceCollection _services;
    private readonly IHookDispatcher _hookDispatcher;
    private readonly ILogger<AssemblyLoadContextExtensionLoader> _logger;
    private readonly IFileSystem _fileSystem;
    private readonly Lock _sync = new();
    private readonly Dictionary<string, LoadedExtensionRuntime> _loaded = new(StringComparer.OrdinalIgnoreCase);

    // Every (contract, implementation) pair this loader auto-registers into the host container.
    // Extension services are resolved as sets during startup (e.g. IEnumerable<IAgentTool>,
    // IEnumerable<IAgentToolContributor>); if any one cannot be activated by the container the
    // whole enumeration throws and aborts the host. After all extensions load we probe these
    // against the built container and prune the un-activatable ones. See
    // PruneUnconstructableExtensionServices and issue #2220.
    private readonly List<(Type Contract, Type Implementation)> _registeredExtensionServices = [];

    // #2731: channel-extension hosted services are registered as a concrete self-binding PLUS a
    // barrier factory descriptor. The factory carries no ImplementationType, so the #2220 prune
    // pass cannot find it by (contract, implementation) matching. Remember the exact descriptors
    // so pruning can remove both - leaving the factory behind would make it resolve a type that
    // is no longer registered and abort IEnumerable<IHostedService> resolution at host start.
    private readonly Dictionary<Type, List<ServiceDescriptor>> _channelHostedServiceDescriptors = [];

    public AssemblyLoadContextExtensionLoader(
        IServiceCollection services,
        IHookDispatcher hookDispatcher,
        ILogger<AssemblyLoadContextExtensionLoader> logger,
        IFileSystem fileSystem)
    {
        _services = services;
        _hookDispatcher = hookDispatcher;
        _logger = logger;
        _fileSystem = fileSystem;
    }

    public Task<IReadOnlyList<ExtensionInfo>> DiscoverAsync(string extensionsPath, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(extensionsPath);
        ct.ThrowIfCancellationRequested();

        var rootPath = Path.GetFullPath(extensionsPath);
        if (!_fileSystem.Directory.Exists(rootPath))
        {
            _logger.LogInformation("Extensions directory '{ExtensionsPath}' does not exist. Skipping discovery.", rootPath);
            return Task.FromResult<IReadOnlyList<ExtensionInfo>>([]);
        }

        var discovered = new List<ExtensionInfo>();
        foreach (var extensionDirectory in _fileSystem.Directory.GetDirectories(rootPath))
        {
            ct.ThrowIfCancellationRequested();

            var manifestPath = Path.Combine(extensionDirectory, "botnexus-extension.json");
            if (!_fileSystem.File.Exists(manifestPath))
            {
                _logger.LogDebug("Skipping '{ExtensionDirectory}' because botnexus-extension.json is missing.", extensionDirectory);
                continue;
            }

            try
            {
                var manifest = ReadAndValidateManifest(_fileSystem, manifestPath, extensionDirectory);
                var entryAssemblyPath = ResolveEntryAssemblyPath(extensionDirectory, manifest.EntryAssembly);
                if (!_fileSystem.File.Exists(entryAssemblyPath))
                    throw new InvalidOperationException($"Entry assembly '{manifest.EntryAssembly}' does not exist.");

                discovered.Add(new ExtensionInfo
                {
                    DirectoryPath = extensionDirectory,
                    ManifestPath = manifestPath,
                    EntryAssemblyPath = entryAssemblyPath,
                    Manifest = manifest
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Skipping extension in '{ExtensionDirectory}' due to manifest or assembly validation failure.", extensionDirectory);
            }
        }

        return Task.FromResult<IReadOnlyList<ExtensionInfo>>(discovered);
    }

    public Task<ExtensionLoadResult> LoadAsync(ExtensionInfo extension, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(extension);
        ct.ThrowIfCancellationRequested();

        lock (_sync)
        {
            if (_loaded.ContainsKey(extension.Manifest.Id))
            {
                return Task.FromResult(new ExtensionLoadResult
                {
                    ExtensionId = extension.Manifest.Id,
                    Success = true,
                    RegisteredServices = _loaded[extension.Manifest.Id].LoadedExtension.RegisteredServices
                });
            }
        }

        try
        {
            ValidateDependencies(extension.Manifest);

            var loadContext = new ExtensionAssemblyLoadContext(
                extension.EntryAssemblyPath,
                isCollectible: !RequiresNonCollectible(extension.Manifest));
            var assembly = loadContext.LoadFromAssemblyPath(extension.EntryAssemblyPath);

            var discoveredImplementations = DiscoverImplementations(assembly);
            _logger.LogWarning(
                "Extension '{ExtensionId}' from '{Path}': discovered {Count} implementation(s){Details}",
                extension.Manifest.Id,
                extension.EntryAssemblyPath,
                discoveredImplementations.Count,
                discoveredImplementations.Count > 0
                    ? ": " + string.Join(", ", discoveredImplementations.Select(d => $"{d.ServiceContract.Name}->{d.Implementation.Name}"))
                    : " — no discoverable types found");

            var hookHandlerTypes = DiscoverHookHandlers(assembly);
            var registeredServiceNames = RegisterServices(discoveredImplementations, extension.Manifest);
            RegisterHookHandlers(hookHandlerTypes, registeredServiceNames);
            InvokeServiceContributors(assembly, registeredServiceNames);

            var loadedExtension = new LoadedExtension
            {
                ExtensionId = extension.Manifest.Id,
                Name = extension.Manifest.Name,
                Version = extension.Manifest.Version,
                DirectoryPath = extension.DirectoryPath,
                EntryAssemblyPath = extension.EntryAssemblyPath,
                ExtensionTypes = extension.Manifest.ExtensionTypes,
                LoadedAtUtc = DateTimeOffset.UtcNow,
                RegisteredServices = registeredServiceNames,
                Enabled = extension.Manifest.Enabled,
                ConfigSchema = extension.Manifest.ConfigSchema
            };

            lock (_sync)
            {
                _loaded[extension.Manifest.Id] = new LoadedExtensionRuntime(loadedExtension, loadContext);
            }

            _logger.LogWarning(
                "Loaded extension '{ExtensionId}' ({Name} v{Version}) with {ServiceCount} service registration(s).",
                loadedExtension.ExtensionId,
                loadedExtension.Name,
                loadedExtension.Version,
                registeredServiceNames.Count);

            return Task.FromResult(new ExtensionLoadResult
            {
                ExtensionId = extension.Manifest.Id,
                Success = true,
                RegisteredServices = registeredServiceNames
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load extension '{ExtensionId}' from '{DirectoryPath}'.", extension.Manifest.Id, extension.DirectoryPath);
            return Task.FromResult(new ExtensionLoadResult
            {
                ExtensionId = extension.Manifest.Id,
                Success = false,
                Error = ex.Message
            });
        }
    }

    public Task UnloadAsync(string extensionId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(extensionId);
        ct.ThrowIfCancellationRequested();

        LoadedExtensionRuntime? runtime = null;
        lock (_sync)
        {
            if (_loaded.Remove(extensionId, out var loadedRuntime))
                runtime = loadedRuntime;
        }

        if (runtime is null)
            return Task.CompletedTask;

        runtime.LoadContext.Unload();
        _logger.LogInformation("Unloaded extension '{ExtensionId}'. Service registrations remain until process restart.", extensionId);
        return Task.CompletedTask;
    }

    public IReadOnlyList<LoadedExtension> GetLoaded()
    {
        lock (_sync)
            return _loaded.Values.Select(value => value.LoadedExtension).ToArray();
    }

    /// <summary>
    /// Runs post-build extension phases: endpoint contributors and API contributors.
    /// Call after WebApplication.Build() and before app.Run().
    /// </summary>
    public static void MapExtensionEndpoints(WebApplication app)
    {
        foreach (var contributor in app.Services.GetServices<IEndpointContributor>())
            contributor.MapEndpoints(app);

        foreach (var apiContributor in app.Services.GetServices<IApiContributor>())
        {
            // TODO: determine extension ID for scoped routing
            // For now, use type name as namespace
            var extId = apiContributor.GetType().Assembly.GetName().Name ?? "unknown";
            var group = app.MapGroup($"/api/extensions/{extId}");
            apiContributor.MapApiRoutes(group);
        }
    }

    private static ExtensionManifest ReadAndValidateManifest(IFileSystem fileSystem, string manifestPath, string extensionDirectory)
    {
        var manifestJson = fileSystem.File.ReadAllText(manifestPath);
        var manifest = JsonSerializer.Deserialize<ExtensionManifest>(manifestJson, ManifestJsonOptions)
            ?? throw new InvalidOperationException($"Manifest '{manifestPath}' could not be deserialized.");

        ValidateManifest(manifest, extensionDirectory);
        return manifest;
    }

    private static void ValidateManifest(ExtensionManifest manifest, string extensionDirectory)
    {
        if (string.IsNullOrWhiteSpace(manifest.Id))
            throw new InvalidOperationException($"Manifest in '{extensionDirectory}' must define a non-empty id.");
        if (string.IsNullOrWhiteSpace(manifest.Name))
            throw new InvalidOperationException($"Manifest for '{manifest.Id}' must define name.");
        if (string.IsNullOrWhiteSpace(manifest.Version))
            throw new InvalidOperationException($"Manifest for '{manifest.Id}' must define version.");
        if (string.IsNullOrWhiteSpace(manifest.EntryAssembly))
            throw new InvalidOperationException($"Manifest for '{manifest.Id}' must define entryAssembly.");

        if (manifest.EntryAssembly.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new InvalidOperationException($"Manifest for '{manifest.Id}' has invalid entryAssembly value.");

        if (Path.IsPathRooted(manifest.EntryAssembly))
            throw new InvalidOperationException($"Manifest for '{manifest.Id}' entryAssembly cannot be an absolute path.");

        var extensionTypes = manifest.ExtensionTypes ?? [];
        if (extensionTypes.Count == 0)
            throw new InvalidOperationException($"Manifest for '{manifest.Id}' must define at least one extension type.");

        var allowedTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "channel",
            "isolation",
            "session-store",
            "auth-handler",
            "router",
            "agent-registry",
            "agent-supervisor",
            "agent-communicator",
            "activity-broadcaster",
            "tool",
            "command",
            "hook-handler",
            "media-handler",
            "endpoint-contributor",
            "api-contributor"
        };

        var invalidTypes = extensionTypes
            .Where(extensionType => !allowedTypes.Contains(extensionType))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (invalidTypes.Length > 0)
            throw new InvalidOperationException($"Manifest for '{manifest.Id}' declares unsupported extensionTypes: {string.Join(", ", invalidTypes)}.");
    }

    private static string ResolveEntryAssemblyPath(string extensionDirectory, string entryAssembly)
    {
        var fullPath = Path.GetFullPath(Path.Combine(extensionDirectory, entryAssembly));
        if (!fullPath.StartsWith(Path.GetFullPath(extensionDirectory), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Entry assembly path escapes extension directory.");

        return fullPath;
    }

    private void ValidateDependencies(ExtensionManifest manifest)
    {
        var missingDependencies = (manifest.Dependencies ?? [])
            .Where(dependency => !_loaded.ContainsKey(dependency))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (missingDependencies.Length == 0)
            return;

        throw new InvalidOperationException(
            $"Extension '{manifest.Id}' has unresolved dependencies: {string.Join(", ", missingDependencies)}.");
    }

    /// <summary>
    /// Extensions that contribute endpoints or use web framework types (SignalR hubs, etc.)
    /// must be loaded as non-collectible because ASP.NET uses Reflection.Emit for typed
    /// hub client proxies, which requires non-collectible assemblies.
    /// </summary>
    private static bool RequiresNonCollectible(ExtensionManifest manifest)
        => manifest.ExtensionTypes?.Any(t =>
            t.Equals("endpoint-contributor", StringComparison.OrdinalIgnoreCase) ||
            t.Equals("api-contributor", StringComparison.OrdinalIgnoreCase)) is true;

    private IReadOnlyList<(Type ServiceContract, Type Implementation)> DiscoverImplementations(Assembly assembly)
    {
        var types = GetLoadableTypes(assembly);
        List<(Type ServiceContract, Type Implementation)> implementations = [];

        foreach (var implementationType in types)
        {
            if (!implementationType.IsClass || implementationType.IsAbstract || implementationType.IsGenericTypeDefinition)
                continue;

            foreach (var contract in DiscoverableServiceContracts)
            {
                if (contract.IsAssignableFrom(implementationType))
                {
                    implementations.Add((contract, implementationType));
                }
                else if (implementationType.GetInterfaces().Any(i => i.FullName == contract.FullName))
                {
                    // Type implements an interface with the same name but different assembly identity.
                    // This means the extension loaded its own copy of a shared assembly instead of
                    // using the host's version. Check ExtensionAssemblyLoadContext.HostAssemblies.
                    _logger.LogWarning(
                        "Type '{Implementation}' implements '{ContractName}' from assembly '{ExtAssembly}' "
                        + "but the host expects it from '{HostAssembly}'. Add the shared assembly to "
                        + "ExtensionAssemblyLoadContext.HostAssemblies to fix type identity.",
                        implementationType.FullName,
                        contract.FullName,
                        implementationType.GetInterfaces().First(i => i.FullName == contract.FullName).Assembly.GetName().Name,
                        contract.Assembly.GetName().Name);
                }
            }
        }

        return implementations;
    }

    /// <summary>
    /// Registers the contracts discovered in an extension assembly.
    /// </summary>
    /// <remarks>
    /// #2731: an <see cref="IHostedService"/> contributed by a CHANNEL extension is registered
    /// behind <see cref="ChannelFaultBarrierHostedService"/> instead of with the container's
    /// default hosted-service semantics. A channel is one optional ingress surface, so a missing
    /// Telegram BotToken must cost that channel and nothing else. Hosted services from
    /// NON-channel extensions keep the default <c>StopHost</c> behaviour deliberately - their
    /// failure means the process is not fit to serve and must stop loudly.
    /// </remarks>
    private List<string> RegisterServices(
        IReadOnlyList<(Type ServiceContract, Type Implementation)> implementations,
        ExtensionManifest manifest)
    {
        List<string> registered = [];
        var isChannelExtension = manifest.ExtensionTypes?.Any(
            extensionType => extensionType.Equals("channel", StringComparison.OrdinalIgnoreCase)) is true;
        foreach (var (contract, implementation) in implementations)
        {
            if (contract == typeof(IAgentTool) && !HasAutoResolvableConstructor(implementation, out var skipReason))
            {
                _logger.LogDebug(
                    "Skipping auto-registration for tool implementation '{ImplementationType}' because no DI-compatible constructor was found ({Reason}).",
                    implementation.FullName,
                    skipReason);
                continue;
            }

            if (_services.Any(descriptor =>
                    descriptor.ServiceType == contract &&
                    descriptor.ImplementationType == implementation))
            {
                continue;
            }

            if (contract == typeof(IHostedService) && isChannelExtension)
            {
                _channelHostedServiceDescriptors[implementation] =
                    [.. _services.AddChannelHostedService(implementation, manifest.Id)];
                registered.Add($"{contract.Name}->{implementation.FullName} (channel fault barrier)");
                _registeredExtensionServices.Add((contract, implementation));
                continue;
            }

            if (contract == typeof(IChannelAdapter) || 
                contract == typeof(IIsolationStrategy) ||
                contract == typeof(IAgentChangeNotifier) ||
                contract == typeof(IConversationChangeNotifier) ||
                contract == typeof(IAgentCanvasNotifier) ||
                contract == typeof(IAgentTodoNotifier) ||
                contract == typeof(IAgentToolContributor) ||
                contract == typeof(IAgentTool) ||
                contract == typeof(ICommandContributor) ||
                contract == typeof(IMediaHandler) ||
                contract == typeof(IEndpointContributor) ||
                contract == typeof(IApiContributor) ||
                contract == typeof(IHostedService))
            {
                _services.AddSingleton(contract, implementation);
            }
            else
            {
                _services.TryAddSingleton(contract, implementation);
            }

            registered.Add($"{contract.Name}->{implementation.FullName}");
            _registeredExtensionServices.Add((contract, implementation));
        }

        return registered;
    }

    /// <summary>
    /// Probes every extension service this loader registered against the fully-configured host
    /// container and removes any whose implementation has no constructor the container can satisfy.
    /// This must run after all extensions have loaded (so the whole service graph is present) and
    /// before the host is built. It exists because extension services are resolved as sets during
    /// startup — <c>IEnumerable&lt;IAgentTool&gt;</c>, <c>IEnumerable&lt;IAgentToolContributor&gt;</c>,
    /// hosted services, channel adapters, notifiers — and DI set resolution is all-or-nothing: a
    /// single un-activatable implementation (for example a session-scoped tool whose backend is not
    /// a registered service, or a contributor with a bare <c>string</c> constructor parameter) throws
    /// and aborts host startup, surfacing only as a generic health-check timeout. Pruning turns that
    /// fatal boot failure into a logged warning and a gateway that still starts (issue #2220).
    /// </summary>
    /// <returns>The pruned registrations, for boot-report/diagnostic surfacing.</returns>
    public IReadOnlyList<(Type Contract, Type Implementation, string Reason)> PruneUnconstructableExtensionServices()
    {
        List<(Type Contract, Type Implementation, string Reason)> pruned = [];
        if (_registeredExtensionServices.Count == 0)
            return pruned;

        using var probeProvider = _services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = false,
            ValidateScopes = false
        });

        // IServiceProviderIsService reports registration without constructing anything, so this
        // check is side-effect-free (it does not eagerly build any singleton).
        var isService = probeProvider.GetService<IServiceProviderIsService>();
        if (isService is null)
            return pruned;

        foreach (var (contract, implementation) in _registeredExtensionServices)
        {
            if (HasContainerSatisfiableConstructor(implementation, isService))
                continue;

            _channelHostedServiceDescriptors.TryGetValue(implementation, out var channelDescriptors);

            for (var i = _services.Count - 1; i >= 0; i--)
            {
                var descriptor = _services[i];
                if (descriptor.ServiceType == contract && descriptor.ImplementationType == implementation)
                    _services.RemoveAt(i);
                else if (channelDescriptors?.Contains(descriptor) is true)
                    _services.RemoveAt(i);
            }

            const string reason = "no public constructor whose parameters are all resolvable from the host container";
            pruned.Add((contract, implementation, reason));
            _logger.LogWarning(
                "Pruned extension service registration '{Contract}->{Implementation}' because it cannot be activated by the host container ({Reason}). The gateway will start without it.",
                contract.Name,
                implementation.FullName,
                reason);
        }

        return pruned;
    }

    /// <summary>
    /// Mirrors the constructor selection the DI container performs: the greediest public
    /// constructor whose every parameter is either a registered service, an
    /// <see cref="IServiceProvider"/>, or has a default value. Uses the actual container
    /// registrations (via <paramref name="isService"/>) rather than assuming any interface is
    /// resolvable — the assumption that broke <c>DataStoreTool</c> whose <c>IDataStoreBackend</c>
    /// parameter is a per-session type that is never registered as a host service.
    /// </summary>
    internal static bool HasContainerSatisfiableConstructor(Type implementation, IServiceProviderIsService isService)
    {
        var constructors = implementation.GetConstructors(BindingFlags.Public | BindingFlags.Instance);
        foreach (var constructor in constructors.OrderByDescending(c => c.GetParameters().Length))
        {
            if (constructor.GetParameters().All(parameter => IsContainerResolvableParameter(parameter, isService)))
                return true;
        }

        return false;
    }

    private static bool IsContainerResolvableParameter(ParameterInfo parameter, IServiceProviderIsService isService)
        => parameter.HasDefaultValue
            || parameter.ParameterType == typeof(IServiceProvider)
            || isService.IsService(parameter.ParameterType);

    private static IReadOnlyList<Type> GetLoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(type => type is not null).Cast<Type>().ToArray();
        }
    }

    private static IReadOnlyList<(Type ClosedInterface, Type Implementation)> DiscoverHookHandlers(Assembly assembly)
    {
        var hookHandlerOpenGeneric = typeof(IHookHandler<,>);
        var types = GetLoadableTypes(assembly);
        List<(Type ClosedInterface, Type Implementation)> handlers = [];

        foreach (var type in types)
        {
            if (!type.IsClass || type.IsAbstract || type.IsGenericTypeDefinition)
                continue;

            foreach (var iface in type.GetInterfaces())
            {
                if (iface.IsGenericType && iface.GetGenericTypeDefinition() == hookHandlerOpenGeneric)
                    handlers.Add((iface, type));
            }
        }

        return handlers;
    }

    private void RegisterHookHandlers(
        IReadOnlyList<(Type ClosedInterface, Type Implementation)> hookHandlerTypes,
        List<string> registeredServiceNames)
    {
        // Use reflection to call IHookDispatcher.Register<TEvent, TResult>(handler)
        var registerMethod = typeof(IHookDispatcher).GetMethod(nameof(IHookDispatcher.Register))!;
        using var serviceProvider = _services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = false,
            ValidateScopes = false
        });

        foreach (var (closedInterface, implementation) in hookHandlerTypes)
        {
            var genericArgs = closedInterface.GetGenericArguments(); // [TEvent, TResult]
            var instance = ActivatorUtilities.CreateInstance(serviceProvider, implementation);
            var closed = registerMethod.MakeGenericMethod(genericArgs);
            closed.Invoke(_hookDispatcher, [instance]);
            registeredServiceNames.Add($"IHookHandler<{genericArgs[0].Name},{genericArgs[1].Name}>->{implementation.FullName}");
        }
    }

    /// <summary>
    /// Discovers <see cref="IServiceContributor"/> implementations in the extension assembly and
    /// invokes <see cref="IServiceContributor.ConfigureServices"/> against the host service
    /// collection. This runs while <c>_services</c> is still mutable (before the host is built),
    /// letting extensions register services that contract-based auto-discovery cannot express —
    /// for example authorization policies or framework-default replacements.
    /// </summary>
    private void InvokeServiceContributors(Assembly assembly, List<string> registeredServiceNames)
    {
        var contributorContract = typeof(IServiceContributor);

        foreach (var type in GetLoadableTypes(assembly))
        {
            if (!type.IsClass || type.IsAbstract || type.IsGenericTypeDefinition)
                continue;

            if (!contributorContract.IsAssignableFrom(type))
                continue;

            try
            {
                var contributor = (IServiceContributor)Activator.CreateInstance(type)!;
                contributor.ConfigureServices(_services);
                registeredServiceNames.Add($"{nameof(IServiceContributor)}->{type.FullName}");
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Service contributor '{ContributorType}' threw during ConfigureServices and was skipped.",
                    type.FullName);
            }
        }
    }

    private static bool HasAutoResolvableConstructor(Type implementation, out string reason)
    {
        var constructors = implementation.GetConstructors(BindingFlags.Public | BindingFlags.Instance);
        if (constructors.Length == 0)
        {
            reason = "no public constructors";
            return false;
        }

        foreach (var constructor in constructors)
        {
            var parameters = constructor.GetParameters();
            if (parameters.All(IsAutoResolvableParameter))
            {
                reason = "has DI-compatible constructor";
                return true;
            }
        }

        reason = "constructors require non-service primitive/concrete parameters without defaults";
        return false;
    }

    private static bool IsAutoResolvableParameter(ParameterInfo parameter)
        => parameter.HasDefaultValue
            || parameter.ParameterType == typeof(IServiceProvider)
            || parameter.ParameterType.IsInterface
            || parameter.ParameterType.IsAbstract;

    private sealed record LoadedExtensionRuntime(LoadedExtension LoadedExtension, AssemblyLoadContext LoadContext);
}
