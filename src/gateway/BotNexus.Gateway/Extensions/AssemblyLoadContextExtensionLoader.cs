using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using BotNexus.Gateway.Abstractions.Activity;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Abstractions.Extensions;
using BotNexus.Gateway.Abstractions.Isolation;
using BotNexus.Gateway.Abstractions.Routing;
using BotNexus.Gateway.Abstractions.Security;
using BotNexus.Gateway.Abstractions.Sessions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
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
        typeof(IAgentCommunicator),
        typeof(IActivityBroadcaster)
    ];

    private readonly IServiceCollection _services;
    private readonly ILogger<AssemblyLoadContextExtensionLoader> _logger;
    private readonly Lock _sync = new();
    private readonly Dictionary<string, LoadedExtensionRuntime> _loaded = new(StringComparer.OrdinalIgnoreCase);

    public AssemblyLoadContextExtensionLoader(
        IServiceCollection services,
        ILogger<AssemblyLoadContextExtensionLoader> logger)
    {
        _services = services;
        _logger = logger;
    }

    public Task<IReadOnlyList<ExtensionInfo>> DiscoverAsync(string extensionsPath, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(extensionsPath);
        ct.ThrowIfCancellationRequested();

        var rootPath = Path.GetFullPath(extensionsPath);
        if (!Directory.Exists(rootPath))
        {
            _logger.LogInformation("Extensions directory '{ExtensionsPath}' does not exist. Skipping discovery.", rootPath);
            return Task.FromResult<IReadOnlyList<ExtensionInfo>>([]);
        }

        var discovered = new List<ExtensionInfo>();
        foreach (var extensionDirectory in Directory.GetDirectories(rootPath))
        {
            ct.ThrowIfCancellationRequested();

            var manifestPath = Path.Combine(extensionDirectory, "botnexus-extension.json");
            if (!File.Exists(manifestPath))
            {
                _logger.LogDebug("Skipping '{ExtensionDirectory}' because botnexus-extension.json is missing.", extensionDirectory);
                continue;
            }

            try
            {
                var manifest = ReadAndValidateManifest(manifestPath, extensionDirectory);
                var entryAssemblyPath = ResolveEntryAssemblyPath(extensionDirectory, manifest.EntryAssembly);
                if (!File.Exists(entryAssemblyPath))
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

            var loadContext = new ExtensionAssemblyLoadContext(extension.EntryAssemblyPath);
            var assembly = loadContext.LoadFromAssemblyPath(extension.EntryAssemblyPath);

            var discoveredImplementations = DiscoverImplementations(assembly);
            var registeredServiceNames = RegisterServices(discoveredImplementations);

            var loadedExtension = new LoadedExtension
            {
                ExtensionId = extension.Manifest.Id,
                Name = extension.Manifest.Name,
                Version = extension.Manifest.Version,
                DirectoryPath = extension.DirectoryPath,
                LoadedAtUtc = DateTimeOffset.UtcNow,
                RegisteredServices = registeredServiceNames
            };

            lock (_sync)
            {
                _loaded[extension.Manifest.Id] = new LoadedExtensionRuntime(loadedExtension, loadContext);
            }

            _logger.LogInformation(
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

    private static ExtensionManifest ReadAndValidateManifest(string manifestPath, string extensionDirectory)
    {
        var manifestJson = File.ReadAllText(manifestPath);
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
            "activity-broadcaster"
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

    private static IReadOnlyList<(Type ServiceContract, Type Implementation)> DiscoverImplementations(Assembly assembly)
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
                    implementations.Add((contract, implementationType));
            }
        }

        return implementations;
    }

    private IReadOnlyList<string> RegisterServices(IReadOnlyList<(Type ServiceContract, Type Implementation)> implementations)
    {
        List<string> registered = [];
        foreach (var (contract, implementation) in implementations)
        {
            if (_services.Any(descriptor =>
                    descriptor.ServiceType == contract &&
                    descriptor.ImplementationType == implementation))
            {
                continue;
            }

            if (contract == typeof(IChannelAdapter) || contract == typeof(IIsolationStrategy))
                _services.AddSingleton(contract, implementation);
            else
                _services.TryAddSingleton(contract, implementation);

            registered.Add($"{contract.Name}->{implementation.FullName}");
        }

        return registered;
    }

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

    private sealed record LoadedExtensionRuntime(LoadedExtension LoadedExtension, AssemblyLoadContext LoadContext);
}
