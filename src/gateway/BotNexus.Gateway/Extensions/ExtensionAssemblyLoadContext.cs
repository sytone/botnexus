using System.Reflection;
using System.Runtime.Loader;

namespace BotNexus.Gateway.Extensions;

internal sealed class ExtensionAssemblyLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver;

    // Minimal explicit override list. This is NOT the primary mechanism for host unification -
    // the categorical check in Load() unifies any assembly the host has ALREADY loaded into
    // AssemblyLoadContext.Default. This list only covers assemblies the host may not have
    // lazily loaded yet at the moment an extension is loaded, but which must still resolve from
    // the host to preserve type identity (e.g. rarely-touched configuration/abstraction
    // assemblies referenced only through an extension's DI surface). Keeping it minimal avoids
    // the reactive, crash-prone maintenance burden of the old allow-list (recurring startup
    // crash, #2219).
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

    // Snapshot cache of assembly simple-names currently loaded into the host's default context.
    // Rebuilt lazily and refreshed on miss so lazily-loaded host assemblies are still picked up.
    private static volatile HashSet<string>? _hostLoadedNames;

    public ExtensionAssemblyLoadContext(string mainAssemblyPath, bool isCollectible = true)
        : base($"BotNexus.Extension::{Path.GetFileNameWithoutExtension(mainAssemblyPath)}::{Guid.NewGuid():N}", isCollectible)
    {
        _resolver = new AssemblyDependencyResolver(mainAssemblyPath);
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        var name = assemblyName.Name;

        // UNIFICATION (primary): if the requested assembly is already loaded in the host's
        // default context, resolve it from the host - categorically, regardless of name - so
        // the extension shares the host's type identity. Returning null delegates resolution to
        // the default context. This is the standard .NET plugin unification pattern and replaces
        // the old reactive allow-list that had to be manually extended for every shared contract
        // (recurring gateway startup crash, #2219).
        if (name is not null && ShouldUnifyWithHost(name))
            return null;

        var assemblyPath = _resolver.ResolveAssemblyToPath(assemblyName);
        if (string.IsNullOrWhiteSpace(assemblyPath))
            return null;

        return LoadFromAssemblyPath(assemblyPath);
    }

    /// <summary>
    /// Returns true when the named assembly must be resolved from the host's default load
    /// context rather than a private extension copy, to preserve type identity across the
    /// extension boundary. An assembly unifies with the host when it is either already loaded in
    /// <see cref="AssemblyLoadContext.Default"/> (categorical) or present in the minimal explicit
    /// override list. Exposed for testing the shared-assembly contract.
    /// </summary>
    internal static bool ShouldUnifyWithHost(string assemblyName)
        => IsLoadedInHost(assemblyName) || IsHostAssembly(assemblyName);

    /// <summary>
    /// Returns true when the named assembly is in the minimal explicit host-unification override
    /// list. This list is a fallback for assemblies the host may not have lazily loaded yet;
    /// the categorical <see cref="IsLoadedInHost"/> check is the primary unification mechanism.
    /// Exposed for testing the shared-assembly contract.
    /// </summary>
    internal static bool IsHostAssembly(string assemblyName) => HostAssemblies.Contains(assemblyName);

    /// <summary>
    /// Returns true when an assembly with the given simple name is already loaded into the host's
    /// default <see cref="AssemblyLoadContext"/>. Refreshes the cached snapshot on a miss so
    /// assemblies the host loads lazily after this context was created are still unified.
    /// </summary>
    internal static bool IsLoadedInHost(string assemblyName)
    {
        var snapshot = _hostLoadedNames;
        if (snapshot is not null && snapshot.Contains(assemblyName))
            return true;

        // Miss (or first call): rebuild the snapshot from the current default-context assemblies.
        // Lazily-loaded host assemblies appear here once loaded, so a rebuild-on-miss keeps the
        // unification decision correct without paying enumeration cost on every hit.
        var rebuilt = BuildHostLoadedNames();
        _hostLoadedNames = rebuilt;
        return rebuilt.Contains(assemblyName);
    }

    private static HashSet<string> BuildHostLoadedNames()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var assembly in Default.Assemblies)
        {
            var simpleName = assembly.GetName().Name;
            if (!string.IsNullOrEmpty(simpleName))
                names.Add(simpleName);
        }

        return names;
    }
}
