using System.Reflection;
using System.Runtime.Loader;
using BotNexus.Core.Abstractions;
using BotNexus.Core.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BotNexus.Core.Extensions;

public static class ExtensionLoaderExtensions
{
    public static IServiceCollection AddBotNexusExtensions(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var botSection = configuration.GetSection(BotNexusConfig.SectionName);
        var botConfig = botSection.Get<BotNexusConfig>() ?? new BotNexusConfig();
        var extensionsPath = Path.GetFullPath(botConfig.ExtensionsPath, Directory.GetCurrentDirectory());

        LogInfo($"Extension loader root: {extensionsPath}");

        var contexts = new List<AssemblyLoadContext>();
        var extensionSpecs = GetConfiguredExtensions(botConfig);

        foreach (var spec in extensionSpecs)
        {
            if (!TryResolveExtensionFolder(extensionsPath, spec.TypeFolder, spec.Key, out var extensionFolder, out var rejectionReason))
            {
                LogWarning($"Rejected extension '{spec.TypeFolder}/{spec.Key}': {rejectionReason}");
                continue;
            }

            LogInfo($"Scanning extension folder: {extensionFolder}");

            if (!Directory.Exists(extensionFolder))
            {
                LogWarning($"Extension folder not found: {extensionFolder}");
                continue;
            }

            var dllFiles = Directory.GetFiles(extensionFolder, "*.dll", SearchOption.TopDirectoryOnly);
            if (dllFiles.Length == 0)
            {
                LogWarning($"No assemblies found in extension folder: {extensionFolder}");
                continue;
            }

            var loadContext = new AssemblyLoadContext($"BotNexus.Extension.{spec.TypeFolder}.{spec.Key}", isCollectible: true);
            loadContext.Resolving += static (_, assemblyName) =>
                AppDomain.CurrentDomain
                    .GetAssemblies()
                    .FirstOrDefault(a => string.Equals(a.GetName().Name, assemblyName.Name, StringComparison.OrdinalIgnoreCase));
            contexts.Add(loadContext);

            var loadedAssemblies = new List<Assembly>();
            foreach (var dll in dllFiles)
            {
                try
                {
                    var assemblyName = AssemblyName.GetAssemblyName(dll);
                    var sharedAssembly = AppDomain.CurrentDomain
                        .GetAssemblies()
                        .FirstOrDefault(a => string.Equals(a.GetName().Name, assemblyName.Name, StringComparison.OrdinalIgnoreCase));

                    if (sharedAssembly is not null)
                    {
                        loadedAssemblies.Add(sharedAssembly);
                        LogInfo($"Reused host assembly: {sharedAssembly.FullName}");
                        continue;
                    }

                    var assembly = loadContext.LoadFromAssemblyPath(Path.GetFullPath(dll));
                    loadedAssemblies.Add(assembly);
                    LogInfo($"Loaded assembly: {assembly.FullName}");
                }
                catch (Exception ex) when (ex is FileLoadException or FileNotFoundException or BadImageFormatException)
                {
                    LogWarning($"Failed to load assembly '{dll}': {ex.Message}");
                }
            }

            if (loadedAssemblies.Count == 0)
            {
                LogWarning($"No valid assemblies loaded from: {extensionFolder}");
                continue;
            }

            var extensionConfigSection = GetExtensionConfigSection(botSection, spec.TypeFolder, spec.Key);
            var registrationCountBefore = CountServiceRegistrations(services, spec.InterfaceType);
            if (TryRegisterWithRegistrar(services, extensionConfigSection, loadedAssemblies))
            {
                RegisterServiceKeyMappings(services, spec, registrationCountBefore);
                LogInfo($"Registrar-based registration completed for '{spec.TypeFolder}/{spec.Key}'");
                continue;
            }

            RegisterByConvention(services, extensionConfigSection, spec.InterfaceType, loadedAssemblies, spec.TypeFolder, spec.Key);
            RegisterServiceKeyMappings(services, spec, registrationCountBefore);
        }

        services.AddSingleton(new ExtensionLoadContextStore(contexts));
        return services;
    }

    private static IConfiguration GetExtensionConfigSection(IConfiguration botSection, string typeFolder, string key)
        => typeFolder switch
        {
            "providers" => botSection.GetSection($"Providers:{key}"),
            "channels" => botSection.GetSection($"Channels:Instances:{key}"),
            "tools" => botSection.GetSection($"Tools:Extensions:{key}"),
            _ => botSection
        };

    private static IEnumerable<ConfiguredExtension> GetConfiguredExtensions(BotNexusConfig config)
    {
        foreach (var key in config.Providers.Keys)
            yield return new ConfiguredExtension("providers", key, typeof(ILlmProvider));

        foreach (var key in config.Channels.Instances.Keys)
            yield return new ConfiguredExtension("channels", key, typeof(IChannel));

        foreach (var key in config.Tools.Extensions.Keys)
            yield return new ConfiguredExtension("tools", key, typeof(ITool));
    }

    private static bool TryRegisterWithRegistrar(IServiceCollection services, IConfiguration extensionConfig, IReadOnlyList<Assembly> assemblies)
    {
        var registrarTypes = assemblies
            .SelectMany(GetLoadableTypes)
            .Where(t => !t.IsAbstract && !t.IsInterface && typeof(IExtensionRegistrar).IsAssignableFrom(t))
            .ToList();

        if (registrarTypes.Count == 0)
            return false;

        foreach (var registrarType in registrarTypes)
        {
            try
            {
                if (Activator.CreateInstance(registrarType) is not IExtensionRegistrar registrar)
                {
                    LogWarning($"Failed to instantiate registrar type '{registrarType.FullName}'");
                    continue;
                }

                registrar.Register(services, extensionConfig);
                LogInfo($"Registrar executed: {registrarType.FullName}");
            }
            catch (Exception ex)
            {
                LogError($"Registrar '{registrarType.FullName}' failed: {ex.Message}", ex);
            }
        }

        return true;
    }

    private static void RegisterByConvention(
        IServiceCollection services,
        IConfiguration extensionConfig,
        Type targetInterface,
        IReadOnlyList<Assembly> assemblies,
        string typeFolder,
        string key)
    {
        var discoveredTypes = assemblies
            .SelectMany(GetLoadableTypes)
            .Where(t => !t.IsAbstract && !t.IsInterface && targetInterface.IsAssignableFrom(t))
            .ToList();

        if (discoveredTypes.Count == 0)
        {
            LogWarning($"No types implementing '{targetInterface.Name}' found in extension '{typeFolder}/{key}'");
            return;
        }

        foreach (var type in discoveredTypes)
        {
            services.AddSingleton(targetInterface, sp => CreateExtensionInstance(sp, type, extensionConfig));
            LogInfo($"Registered {type.FullName} as {targetInterface.Name}");
        }
    }

    private static object CreateExtensionInstance(IServiceProvider serviceProvider, Type implementationType, IConfiguration extensionConfig)
    {
        try
        {
            return ActivatorUtilities.CreateInstance(serviceProvider, implementationType, extensionConfig);
        }
        catch
        {
            return ActivatorUtilities.CreateInstance(serviceProvider, implementationType);
        }
    }

    private static IEnumerable<Type> GetLoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(t => t is not null).Cast<Type>();
        }
        catch (Exception ex)
        {
            LogWarning($"Failed to scan assembly '{assembly.FullName}': {ex.Message}");
            return [];
        }
    }

    private static int CountServiceRegistrations(IServiceCollection services, Type serviceType)
        => services.Count(d => d.ServiceType == serviceType);

    private static void RegisterServiceKeyMappings(
        IServiceCollection services,
        ConfiguredExtension extension,
        int registrationCountBefore)
    {
        var registrationCountAfter = CountServiceRegistrations(services, extension.InterfaceType);
        var added = registrationCountAfter - registrationCountBefore;
        for (var i = 0; i < added; i++)
            services.AddSingleton(new ExtensionServiceRegistration(extension.InterfaceType, extension.Key));
    }

    private static bool TryResolveExtensionFolder(
        string rootPath,
        string typeFolder,
        string key,
        out string extensionFolder,
        out string rejectionReason)
    {
        extensionFolder = string.Empty;
        rejectionReason = string.Empty;

        if (string.IsNullOrWhiteSpace(key))
        {
            rejectionReason = "Extension key is empty";
            return false;
        }

        if (Path.IsPathRooted(key))
        {
            rejectionReason = "Rooted paths are not allowed";
            return false;
        }

        if (key.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
        {
            rejectionReason = "Key contains invalid path characters";
            return false;
        }

        var segments = key.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Any(s => s is "." or ".."))
        {
            rejectionReason = "Path traversal segments are not allowed";
            return false;
        }

        var rootFull = Path.GetFullPath(rootPath);
        var folderFull = Path.GetFullPath(Path.Combine(rootFull, typeFolder, key));
        var rootWithSeparator = rootFull.EndsWith(Path.DirectorySeparatorChar)
            ? rootFull
            : rootFull + Path.DirectorySeparatorChar;

        if (!folderFull.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
        {
            rejectionReason = "Resolved path escapes extensions root";
            return false;
        }

        extensionFolder = folderFull;
        return true;
    }

    private static void LogInfo(string message) => Console.WriteLine($"[Information] {message}");
    private static void LogWarning(string message) => Console.WriteLine($"[Warning] {message}");
    private static void LogError(string message, Exception ex) => Console.WriteLine($"[Error] {message} ({ex.GetType().Name})");

    private sealed record ConfiguredExtension(string TypeFolder, string Key, Type InterfaceType);
}

public sealed class ExtensionLoadContextStore(IReadOnlyList<AssemblyLoadContext> contexts)
{
    public IReadOnlyList<AssemblyLoadContext> Contexts { get; } = contexts;
}

public sealed record ExtensionServiceRegistration(Type ServiceType, string Key);
