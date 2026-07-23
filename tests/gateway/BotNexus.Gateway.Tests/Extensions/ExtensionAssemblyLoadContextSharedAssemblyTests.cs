using System.Runtime.Loader;
using BotNexus.Gateway.Extensions;

namespace BotNexus.Gateway.Tests.Extensions;

/// <summary>
/// Guards the shared-assembly contract of <see cref="ExtensionAssemblyLoadContext"/>. Dynamically
/// loaded channel extensions must receive the host's type identity for the configuration
/// assemblies; otherwise DI cannot satisfy an <c>IConfiguration</c> constructor parameter and
/// silently injects null, breaking config self-binding (regression: Service Bus channel adapter).
/// </summary>
public class ExtensionAssemblyLoadContextSharedAssemblyTests
{
    [Theory]
    [InlineData("Microsoft.Extensions.Configuration.Abstractions")]
    [InlineData("Microsoft.Extensions.Configuration.Binder")]
    [InlineData("Microsoft.Extensions.Options")]
    [InlineData("Microsoft.Extensions.DependencyInjection.Abstractions")]
    [InlineData("Microsoft.Extensions.Logging.Abstractions")]
    // System.IO.Abstractions assemblies must be shared so an extension's IFileSystem endpoint/tool
    // parameter keeps the host's type identity and stays bindable as a DI service (regression #2184).
    [InlineData("Testably.Abstractions.FileSystem.Interface")]
    [InlineData("TestableIO.System.IO.Abstractions.Wrappers")]
    public void IsHostAssembly_returns_true_for_shared_configuration_and_di_assemblies(string assemblyName)
    {
        ExtensionAssemblyLoadContext.IsHostAssembly(assemblyName).ShouldBeTrue();
    }

    [Theory]
    [InlineData("Azure.Messaging.ServiceBus")]
    [InlineData("Azure.Identity")]
    [InlineData("SomeRandom.Extension.PrivateDependency")]
    public void IsHostAssembly_returns_false_for_extension_private_assemblies(string assemblyName)
    {
        ExtensionAssemblyLoadContext.IsHostAssembly(assemblyName).ShouldBeFalse();
    }

    /// <summary>
    /// Categorical unification (the fix for #2219): an extension that ships a private copy of an
    /// arbitrary assembly that the host has already loaded must still resolve that assembly from
    /// the host to preserve type identity - regardless of whether the assembly name appears in the
    /// minimal explicit override list. We use assemblies that are loaded in the current process's
    /// default context but are NOT in the static <c>HostAssemblies</c> list, so this exercises the
    /// categorical AssemblyLoadContext.Default check rather than the allow-list.
    /// </summary>
    [Fact]
    public void ShouldUnifyWithHost_returns_true_for_arbitrary_host_loaded_assembly_not_in_override_list()
    {
        // The assembly under test (BotNexus.Gateway) is loaded in the default context but is not a
        // member of the explicit override list. It must still unify categorically.
        const string gatewayAssembly = "BotNexus.Gateway";

        ExtensionAssemblyLoadContext.IsHostAssembly(gatewayAssembly).ShouldBeFalse();
        AssemblyLoadContext.Default.Assemblies
            .Any(a => string.Equals(a.GetName().Name, gatewayAssembly, StringComparison.OrdinalIgnoreCase))
            .ShouldBeTrue();

        ExtensionAssemblyLoadContext.IsLoadedInHost(gatewayAssembly).ShouldBeTrue();
        ExtensionAssemblyLoadContext.ShouldUnifyWithHost(gatewayAssembly).ShouldBeTrue();
    }

    /// <summary>
    /// Every assembly currently loaded in the host's default context must be reported as
    /// host-loaded, confirming the unification check is categorical rather than name-specific.
    /// </summary>
    [Fact]
    public void IsLoadedInHost_returns_true_for_every_default_context_assembly()
    {
        var hostLoaded = AssemblyLoadContext.Default.Assemblies
            .Select(a => a.GetName().Name)
            .Where(n => !string.IsNullOrEmpty(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        hostLoaded.ShouldNotBeEmpty();
        foreach (var name in hostLoaded)
        {
            ExtensionAssemblyLoadContext.IsLoadedInHost(name!).ShouldBeTrue();
        }
    }

    /// <summary>
    /// No behavior change for genuinely private extension dependencies: an assembly that is
    /// neither loaded in the host nor in the override list must NOT unify, so it continues to load
    /// isolated inside the extension's own context.
    /// </summary>
    [Theory]
    [InlineData("SomeRandom.Extension.PrivateDependency")]
    [InlineData("Totally.Fictional.Assembly.That.Is.Not.Loaded")]
    public void ShouldUnifyWithHost_returns_false_for_genuinely_private_dependency(string assemblyName)
    {
        // Guard: the fictional assembly is genuinely not present in the host.
        ExtensionAssemblyLoadContext.IsLoadedInHost(assemblyName).ShouldBeFalse();
        ExtensionAssemblyLoadContext.IsHostAssembly(assemblyName).ShouldBeFalse();

        ExtensionAssemblyLoadContext.ShouldUnifyWithHost(assemblyName).ShouldBeFalse();
    }
}
