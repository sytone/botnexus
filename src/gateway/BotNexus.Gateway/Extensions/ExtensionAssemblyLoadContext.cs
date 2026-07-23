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
        "BotNexus.Gateway.Dispatching",
        "BotNexus.Gateway.Contracts",
        "Microsoft.Extensions.DependencyInjection.Abstractions",
        "Microsoft.Extensions.Logging.Abstractions",
        "Microsoft.Extensions.Logging",
        "Microsoft.Extensions.Options",
        // Configuration must be shared so a dynamically-loaded extension receives the host's
        // IConfiguration type identity. Without this, an extension that ships its own copy of
        // these assemblies (e.g. via CopyLocalLockFileAssemblies) gets a distinct IConfiguration
        // type; DI then cannot satisfy an IConfiguration constructor parameter and silently
        // injects null, breaking config self-binding fallbacks (see Service Bus channel adapter).
        "Microsoft.Extensions.Configuration.Abstractions",
        "Microsoft.Extensions.Configuration.Binder",
        // System.IO.Abstractions must be shared so an extension endpoint/tool receives the host's
        // IFileSystem type identity. The gateway registers IFileSystem -> FileSystem in DI; a
        // minimal-API handler parameter typed as IFileSystem is only bound as a service when its
        // type identity matches that registration. An extension that ships its own copy of these
        // assemblies (e.g. via CopyLocalLockFileAssemblies, #2193) otherwise gets a distinct
        // IFileSystem type; ASP.NET no longer sees it as a service and infers it as a request body,
        // which is illegal on GET/DELETE and aborts host startup (regression: Skills endpoints, #2184).
        "Testably.Abstractions.FileSystem.Interface",
        "TestableIO.System.IO.Abstractions.Wrappers",
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
        if (assemblyName.Name is not null && IsHostAssembly(assemblyName.Name))
            return null;

        var assemblyPath = _resolver.ResolveAssemblyToPath(assemblyName);
        if (string.IsNullOrWhiteSpace(assemblyPath))
            return null;

        return LoadFromAssemblyPath(assemblyPath);
    }

    /// <summary>
    /// Returns true when the named assembly must be resolved from the host's default load
    /// context rather than a private extension copy, to preserve type identity across the
    /// extension boundary. Exposed for testing the shared-assembly contract.
    /// </summary>
    internal static bool IsHostAssembly(string assemblyName) => HostAssemblies.Contains(assemblyName);
}
