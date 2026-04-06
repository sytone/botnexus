using BotNexus.Gateway.Abstractions.Extensions;
using BotNexus.Gateway.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotNexus.Gateway.Extensions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddExtensionLoading(this IServiceCollection services)
    {
        services.TryAddSingleton<IExtensionLoader>(serviceProvider =>
            new AssemblyLoadContextExtensionLoader(
                services,
                serviceProvider.GetRequiredService<ILogger<AssemblyLoadContextExtensionLoader>>()));
        return services;
    }

    public static async Task<IReadOnlyList<ExtensionLoadResult>> LoadConfiguredExtensionsAsync(
        this IServiceCollection services,
        PlatformConfig platformConfig,
        ILoggerFactory? loggerFactory = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(platformConfig);

        var logger = loggerFactory?.CreateLogger<AssemblyLoadContextExtensionLoader>()
            ?? NullLogger<AssemblyLoadContextExtensionLoader>.Instance;
        var loader = new AssemblyLoadContextExtensionLoader(services, logger);

        var extensionsConfig = platformConfig.GetExtensions();
        if (extensionsConfig?.Enabled is false)
            return [];

        var extensionsPath = ResolveExtensionsPath(platformConfig, extensionsConfig);
        var discovered = await loader.DiscoverAsync(extensionsPath, ct);

        IReadOnlyList<ExtensionInfo> ordered;
        try
        {
            ordered = TopologicallySort(discovered);
        }
        catch (Exception ex)
        {
            loggerFactory?.CreateLogger("BotNexus.Gateway.Extensions")
                .LogWarning(ex, "Extension dependency graph could not be fully ordered. Falling back to discovery order.");
            ordered = discovered;
        }

        List<ExtensionLoadResult> results = [];
        foreach (var extension in ordered)
        {
            ct.ThrowIfCancellationRequested();
            var result = await loader.LoadAsync(extension, ct);
            results.Add(result);
        }

        services.Replace(ServiceDescriptor.Singleton<IExtensionLoader>(loader));
        return results;
    }

    private static string ResolveExtensionsPath(PlatformConfig config, ExtensionsConfig? extensionConfig)
    {
        if (!string.IsNullOrWhiteSpace(extensionConfig?.Path))
            return Path.GetFullPath(extensionConfig.Path);

        return Path.Combine(new BotNexusHome().RootPath, "extensions");
    }

    private static IReadOnlyList<ExtensionInfo> TopologicallySort(IReadOnlyList<ExtensionInfo> discovered)
    {
        var byId = discovered.ToDictionary(info => info.Manifest.Id, StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        List<ExtensionInfo> ordered = [];

        foreach (var extension in discovered)
            Visit(extension.Manifest.Id);

        return ordered;

        void Visit(string id)
        {
            if (visited.Contains(id))
                return;
            if (visiting.Contains(id))
                throw new InvalidOperationException($"Circular extension dependency detected for '{id}'.");

            if (!byId.TryGetValue(id, out var extension))
                return;

            visiting.Add(id);
            foreach (var dependency in extension.Manifest.Dependencies ?? [])
                Visit(dependency);

            visiting.Remove(id);
            visited.Add(id);
            ordered.Add(extension);
        }
    }
}
