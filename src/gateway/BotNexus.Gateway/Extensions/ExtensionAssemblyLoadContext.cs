using System.Reflection;
using System.Runtime.Loader;

namespace BotNexus.Gateway.Extensions;

internal sealed class ExtensionAssemblyLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver;

    // Assemblies that must come from the host to preserve type identity.
    private static readonly HashSet<string> HostAssemblies = new(StringComparer.OrdinalIgnoreCase)
    {
        "BotNexus.Agent.Core",
        "BotNexus.Domain",
        "BotNexus.Agent.Providers.Core",
        "BotNexus.Gateway.Abstractions",
        "BotNexus.Gateway.Contracts",
        "Microsoft.Extensions.DependencyInjection.Abstractions",
        "Microsoft.Extensions.Logging.Abstractions",
        "Microsoft.Extensions.Logging",
        "Microsoft.Extensions.Options",
    };

    public ExtensionAssemblyLoadContext(string mainAssemblyPath, bool isCollectible = true)
        : base($"BotNexus.Extension::{Path.GetFileNameWithoutExtension(mainAssemblyPath)}::{Guid.NewGuid():N}", isCollectible)
    {
        _resolver = new AssemblyDependencyResolver(mainAssemblyPath);
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        // Shared host assemblies must come from the default context
        // to preserve type identity for interfaces like IAgentTool.
        if (assemblyName.Name is not null && HostAssemblies.Contains(assemblyName.Name))
            return null;

        var assemblyPath = _resolver.ResolveAssemblyToPath(assemblyName);
        if (string.IsNullOrWhiteSpace(assemblyPath))
            return null;

        return LoadFromAssemblyPath(assemblyPath);
    }
}
