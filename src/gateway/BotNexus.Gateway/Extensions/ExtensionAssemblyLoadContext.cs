using System.Reflection;
using System.Runtime.Loader;

namespace BotNexus.Gateway.Extensions;

internal sealed class ExtensionAssemblyLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver;

    public ExtensionAssemblyLoadContext(string mainAssemblyPath)
        : base($"BotNexus.Extension::{Path.GetFileNameWithoutExtension(mainAssemblyPath)}::{Guid.NewGuid():N}", isCollectible: true)
    {
        _resolver = new AssemblyDependencyResolver(mainAssemblyPath);
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        var assemblyPath = _resolver.ResolveAssemblyToPath(assemblyName);
        if (string.IsNullOrWhiteSpace(assemblyPath))
            return null;

        return LoadFromAssemblyPath(assemblyPath);
    }
}
