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
        var extensionsPath = BotNexusHome.ResolvePath(botConfig.ExtensionsPath);
        var extensionLoadingConfig = NormalizeExtensionLoadingConfig(botConfig.Extensions);

        LogInfo($"Extension loader root: {extensionsPath}");
        LogInfo($"Extension loader security config: RequireSignedAssemblies={extensionLoadingConfig.RequireSignedAssemblies}, MaxAssembliesPerExtension={extensionLoadingConfig.MaxAssembliesPerExtension}, DryRun={extensionLoadingConfig.DryRun}");

        var contexts = new List<AssemblyLoadContext>();
        var results = new List<ExtensionLoadResult>();
        var extensionSpecs = GetConfiguredExtensions(botConfig);

        foreach (var spec in extensionSpecs)
        {
            if (!TryResolveExtensionFolder(extensionsPath, spec.TypeFolder, spec.Key, out var extensionFolder, out var rejectionReason))
            {
                LogWarning($"Rejected extension '{spec.TypeFolder}/{spec.Key}': {rejectionReason}");
                results.Add(new ExtensionLoadResult(spec.TypeFolder, spec.Key, false, rejectionReason));
                continue;
            }

            LogInfo($"Scanning extension folder: {extensionFolder}");

            if (!Directory.Exists(extensionFolder))
            {
                LogWarning($"Extension folder not found: {extensionFolder}");
                results.Add(new ExtensionLoadResult(spec.TypeFolder, spec.Key, false, "Extension folder not found", CountsAsFailure: false));
                continue;
            }

            var dllFiles = Directory.GetFiles(extensionFolder, "*.dll", SearchOption.TopDirectoryOnly);
            if (dllFiles.Length == 0)
            {
                LogWarning($"No assemblies found in extension folder: {extensionFolder}");
                results.Add(new ExtensionLoadResult(spec.TypeFolder, spec.Key, false, "No assemblies found", CountsAsFailure: false));
                continue;
            }

            if (dllFiles.Length > extensionLoadingConfig.MaxAssembliesPerExtension)
            {
                LogWarning($"Extension folder '{extensionFolder}' contains {dllFiles.Length} assemblies which exceeds limit {extensionLoadingConfig.MaxAssembliesPerExtension}. Skipping.");
                continue;
            }

            if (extensionLoadingConfig.DryRun)
            {
                LogInfo($"Dry run enabled for extension '{spec.TypeFolder}/{spec.Key}'. No assemblies will be loaded.");
                foreach (var dll in dllFiles)
                {
                    if (!TryValidateAssemblyMetadata(dll, extensionLoadingConfig.RequireSignedAssemblies, out var assemblyName, out var validationFailure))
                    {
                        LogWarning($"Dry run validation failed for assembly '{dll}': {validationFailure}");
                        continue;
                    }

                    LogInfo($"Dry run would load assembly: Path={Path.GetFullPath(dll)}, Version={assemblyName.Version}, Name={assemblyName.Name}");
                }

                continue;
            }

            var loadContext = new ExtensionLoadContext($"BotNexus.Extension.{spec.TypeFolder}.{spec.Key}", extensionFolder);
            contexts.Add(loadContext);

            var loadedAssemblies = new List<Assembly>();
            foreach (var dll in dllFiles)
            {
                try
                {
                    if (!TryValidateAssemblyMetadata(dll, extensionLoadingConfig.RequireSignedAssemblies, out var assemblyName, out var validationFailure))
                    {
                        LogWarning($"Skipping assembly '{dll}': {validationFailure}");
                        continue;
                    }

                    var sharedAssembly = AppDomain.CurrentDomain
                        .GetAssemblies()
                        .FirstOrDefault(a =>
                            IsAllowedSharedAssembly(a.GetName().Name) &&
                            string.Equals(a.GetName().Name, assemblyName.Name, StringComparison.OrdinalIgnoreCase));

                    if (sharedAssembly is not null)
                    {
                        loadedAssemblies.Add(sharedAssembly);
                        LogAssemblyInfo(sharedAssembly, dll, "Reused shared assembly");
                        continue;
                    }

                    var assembly = loadContext.LoadFromAssemblyPath(Path.GetFullPath(dll));
                    loadedAssemblies.Add(assembly);
                    LogAssemblyInfo(assembly, dll, "Loaded assembly");
                }
                catch (Exception ex) when (ex is FileLoadException or FileNotFoundException or BadImageFormatException)
                {
                    LogWarning($"Failed to load assembly '{dll}': {ex.Message}");
                }
            }

            if (loadedAssemblies.Count == 0)
            {
                LogWarning($"No valid assemblies loaded from: {extensionFolder}");
                results.Add(new ExtensionLoadResult(spec.TypeFolder, spec.Key, false, "No valid assemblies loaded"));
                continue;
            }

            var extensionVersion = loadedAssemblies[0].GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? loadedAssemblies[0].GetName().Version?.ToString();

            var extensionConfigSection = GetExtensionConfigSection(botSection, spec.TypeFolder, spec.Key);
            var registrationCountBefore = CountServiceRegistrations(services, spec.InterfaceType);
            if (TryRegisterWithRegistrar(services, extensionConfigSection, loadedAssemblies))
            {
                RegisterServiceKeyMappings(services, spec, registrationCountBefore);
                var registrationCountAfter = CountServiceRegistrations(services, spec.InterfaceType);
                var addedRegistrations = registrationCountAfter - registrationCountBefore;
                var succeeded = addedRegistrations > 0;
                results.Add(new ExtensionLoadResult(
                    spec.TypeFolder,
                    spec.Key,
                    succeeded,
                    succeeded ? $"Registered {addedRegistrations} service(s)" : "Registrar did not register matching services",
                    extensionVersion));
                LogInfo($"Registrar-based registration completed for '{spec.TypeFolder}/{spec.Key}'");
                continue;
            }

            RegisterByConvention(services, extensionConfigSection, spec.InterfaceType, loadedAssemblies, spec.TypeFolder, spec.Key);
            RegisterServiceKeyMappings(services, spec, registrationCountBefore);

            var finalRegistrationCount = CountServiceRegistrations(services, spec.InterfaceType);
            var finalAddedRegistrations = finalRegistrationCount - registrationCountBefore;
            var finalSucceeded = finalAddedRegistrations > 0;
            results.Add(new ExtensionLoadResult(
                spec.TypeFolder,
                spec.Key,
                finalSucceeded,
                finalSucceeded ? $"Registered {finalAddedRegistrations} service(s)" : "No matching services registered",
                extensionVersion));
        }

        var loadedCount = results.Count(r => r.Success);
        var failedCount = results.Count(r => !r.Success && r.CountsAsFailure);
        var warningCount = results.Count(r => !r.Success && !r.CountsAsFailure);
        services.AddSingleton(new ExtensionLoadReport
        {
            Results = results.AsReadOnly(),
            LoadedCount = loadedCount,
            FailedCount = failedCount,
            WarningCount = warningCount,
            Completed = true
        });
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

        foreach (var (key, channelConfig) in config.Channels.Instances)
        {
            if (channelConfig.Enabled)
                yield return new ConfiguredExtension("channels", key, typeof(IChannel));
        }

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

    private static void LogAssemblyInfo(Assembly assembly, string sourcePath, string prefix)
    {
        var discoveredTypes = GetLoadableTypes(assembly).ToList();
        var discoveredTypeNames = discoveredTypes.Count == 0
            ? "<none>"
            : string.Join(", ", discoveredTypes.Select(t => t.FullName).Where(n => !string.IsNullOrWhiteSpace(n)));
        var fullPath = string.IsNullOrWhiteSpace(assembly.Location) ? Path.GetFullPath(sourcePath) : assembly.Location;

        LogInfo($"{prefix}: Path={fullPath}, Version={assembly.GetName().Version}, Name={assembly.GetName().Name}, DiscoveredTypes=[{discoveredTypeNames}]");
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

        if (ContainsEscapingSymbolicLinks(rootFull, folderFull))
        {
            rejectionReason = "Resolved path uses symbolic link or junction that escapes extensions root";
            return false;
        }

        extensionFolder = folderFull;
        return true;
    }

    private static bool ContainsEscapingSymbolicLinks(string rootFull, string folderFull)
    {
        var rootSegments = rootFull
            .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries)
            .Length;
        var current = rootFull;
        var relativeSegments = folderFull[rootFull.Length..]
            .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);

        foreach (var segment in relativeSegments)
        {
            current = Path.Combine(current, segment);
            if (!Directory.Exists(current))
                continue;

            var info = new DirectoryInfo(current);
            if ((info.Attributes & FileAttributes.ReparsePoint) == 0)
                continue;

            var targetInfo = info.ResolveLinkTarget(returnFinalTarget: true);
            if (targetInfo is null)
                return true;

            var targetFull = Path.GetFullPath(targetInfo.FullName);
            if (!IsPathWithinRoot(rootFull, targetFull))
                return true;

            var targetSegments = targetFull.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries).Length;
            if (targetSegments < rootSegments)
                return true;
        }

        return false;
    }

    private static bool IsPathWithinRoot(string rootFull, string candidatePath)
    {
        var normalizedRoot = rootFull.EndsWith(Path.DirectorySeparatorChar)
            ? rootFull
            : rootFull + Path.DirectorySeparatorChar;
        return candidatePath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase)
               || string.Equals(candidatePath, rootFull, StringComparison.OrdinalIgnoreCase);
    }

    private static ExtensionLoadingConfig NormalizeExtensionLoadingConfig(ExtensionLoadingConfig? config)
    {
        var resolved = config ?? new ExtensionLoadingConfig();
        if (resolved.MaxAssembliesPerExtension <= 0)
            resolved.MaxAssembliesPerExtension = 50;
        return resolved;
    }

    private static bool TryValidateAssemblyMetadata(
        string assemblyPath,
        bool requireSignedAssemblies,
        out AssemblyName assemblyName,
        out string failureReason)
    {
        assemblyName = null!;
        failureReason = string.Empty;

        try
        {
            assemblyName = AssemblyName.GetAssemblyName(assemblyPath);
        }
        catch (Exception ex) when (ex is FileLoadException or FileNotFoundException or BadImageFormatException)
        {
            failureReason = $"Not a valid .NET assembly ({ex.Message})";
            return false;
        }

        if (requireSignedAssemblies)
        {
            var token = assemblyName.GetPublicKeyToken();
            if (token is null || token.Length == 0)
            {
                failureReason = "Assembly is not strong-name signed";
                return false;
            }
        }

        return true;
    }

    private static bool IsAllowedSharedAssembly(string? assemblyName)
    {
        if (string.IsNullOrWhiteSpace(assemblyName))
            return false;

        return assemblyName.StartsWith("BotNexus.Core", StringComparison.OrdinalIgnoreCase)
               || assemblyName.StartsWith("Microsoft.Extensions.", StringComparison.OrdinalIgnoreCase);
    }

    private static void LogInfo(string message) => Console.WriteLine($"[Information] {message}");
    private static void LogWarning(string message) => Console.WriteLine($"[Warning] {message}");
    private static void LogError(string message, Exception ex) => Console.WriteLine($"[Error] {message} ({ex.GetType().Name})");

    private sealed record ConfiguredExtension(string TypeFolder, string Key, Type InterfaceType);
}

internal sealed class ExtensionLoadContext(string name, string extensionFolder)
    : AssemblyLoadContext(name, isCollectible: true)
{
    private readonly string _extensionFolder = Path.GetFullPath(extensionFolder);

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (ExtensionLoaderExtensions_IsAllowedSharedAssembly(assemblyName.Name))
        {
            return AppDomain.CurrentDomain
                .GetAssemblies()
                .FirstOrDefault(a => string.Equals(a.GetName().Name, assemblyName.Name, StringComparison.OrdinalIgnoreCase));
        }

        var candidatePath = Path.Combine(_extensionFolder, $"{assemblyName.Name}.dll");
        if (File.Exists(candidatePath))
            return LoadFromAssemblyPath(candidatePath);

        return null;
    }

    private static bool ExtensionLoaderExtensions_IsAllowedSharedAssembly(string? assemblyName)
    {
        if (string.IsNullOrWhiteSpace(assemblyName))
            return false;

        return assemblyName.StartsWith("BotNexus.Core", StringComparison.OrdinalIgnoreCase)
               || assemblyName.StartsWith("Microsoft.Extensions.", StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class ExtensionLoadContextStore(IReadOnlyList<AssemblyLoadContext> contexts)
{
    public IReadOnlyList<AssemblyLoadContext> Contexts { get; } = contexts;
}

public sealed record ExtensionServiceRegistration(Type ServiceType, string Key);
